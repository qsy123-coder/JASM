using System.ComponentModel;
using System.Runtime.InteropServices;
using GIMI_ModManager.Core.Helpers;
using Microsoft.UI.Xaml;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using WinUIEx.Messaging;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>
/// 浮窗的全局热键：注册、拦截、回收。
///
/// JASM 此前**完全没有**全局热键——全仓库 <c>RegisterHotKey</c> / <c>WM_HOTKEY</c> / 键盘钩子零命中，
/// README 里列的 SPACE / F10 / F5 全是应用内快捷键（要求 JASM 自己有焦点）。而浮窗的前提恰恰相反：
/// 游戏占着前台、JASM 没焦点，所以这一层绕不过去。
///
/// 原理上这条路不受 UIPI 完整性级别限制：<c>RegisterHotKey</c> 注册的是系统级热键，命中时由系统
/// 直接把 <c>WM_HOTKEY</c> 投递给注册窗口，不走 <c>SendInput</c> / 前台窗口那一套检查。鸣潮是
/// <c>require_admin = true</c>（游戏以管理员运行）而 JASM 不提权，因此这一点是「游戏在前台时热键还有效」
/// 的唯一依据 —— Phase 0 实测确认过。
///
/// 候选键的挑选与失败记账在 <see cref="OverlayHotkeyCandidates"/>（纯逻辑、可单测），
/// 本类只负责把 <c>RegisterHotKey</c> 与消息钩子接上去。
///
/// 本类管着**两组**热键，生命周期刻意不同：
/// <list type="bullet">
/// <item><b>唤出键</b>（候选表挑一个）必须**常驻**注册 —— 浮窗藏着的时候按它才能唤出。</item>
/// <item><b>导航键</b>（选中上 / 下、切换、刷新）只在**浮窗显示期间**注册（见
/// <see cref="RegisterNavigationHotkeys"/>）。<c>RegisterHotKey</c> 是系统级**独占**的：注册着这个组合，
/// 系统就不再把它传给任何程序（游戏与 Mod 也在内）。浮窗绝大多数时间是隐藏的，常驻注册等于在用户
/// 玩游戏时一直从游戏嘴里抢走这四个组合键，换来的只是"反正没人看得见的导航"。</item>
/// </list>
/// </summary>
internal sealed class OverlayHotkeyRegistrar : IDisposable
{
    /// <summary>
    /// 唤出键的 id。同一个窗口上注册着多个热键，<c>WM_HOTKEY</c> 的 wParam 就是靠它区分的，
    /// 所以收消息时必须比对。
    /// </summary>
    internal const int HotkeyId = 0x4A53;

    /// <summary>导航键的 id 起点，与唤出键错开。</summary>
    private const int NavigationHotkeyIdBase = HotkeyId + 1;

    /// <summary>
    /// 浮窗显示期间的导航键。**没有候选回退** —— 与唤出键不同，这几个键的语义是固定的
    /// （上下移动、回车切换、R 刷新），换一个键用户就猜不到了；注册不上就少一个键，其余照常可用。
    ///
    /// 主键码在这里按需声明：CsWin32 不生成完备的虚拟键枚举，而 Core 那边也用不到这几个码。
    /// </summary>
    private static readonly (OverlayHotkeyAction Action, ushort VirtualKey, string KeyDisplayName)[] NavigationKeys =
    [
        (OverlayHotkeyAction.SelectPrevious, 0x26, "↑"),     // VK_UP
        (OverlayHotkeyAction.SelectNext, 0x28, "↓"),         // VK_DOWN
        (OverlayHotkeyAction.ToggleSelected, 0x0D, "Enter"), // VK_RETURN
        (OverlayHotkeyAction.Refresh, 0x52, "R")             // VK_R
    ];

    /// <summary><c>WM_HOTKEY</c> 是 <c>#define</c> 而非 API，CsWin32 不生成它，自己收一个。</summary>
    private const int WmHotkey = 0x0312;

    private readonly ILogger _logger;
    private readonly WindowMessageMonitor _monitor;
    private readonly HWND _hwnd;

    /// <summary>当前注册着的导航键（id + 动作 + 显示名）。空 = 没注册，也就是浮窗隐藏时的常态。</summary>
    private readonly List<(int Id, OverlayHotkeyAction Action, string DisplayName)> _navigation = new();

    /// <summary>已经报过 Warning 的导航键。注册随每次显示重来，注册不上的那个每次都会失败 ——
    /// 不记账就会每次显示都往日志里刷一行同样的话。</summary>
    private readonly HashSet<OverlayHotkeyAction> _navigationFailures = new();

    private bool _disposed;

    /// <summary>
    /// 热键被按下。回调在 UI 线程上（<c>WM_HOTKEY</c> 由窗口消息循环派发），可直接改界面 ——
    /// 但**不要**在这里做耗时的事，切显隐是即时动作，重活（刷新）应交给后台。
    /// </summary>
    public event Action? Pressed;

    /// <summary>
    /// 导航热键被按下，参数是哪一个动作。
    ///
    /// 与 <see cref="Pressed"/> 分开而不是合成一个带参数的事件：唤出键**隐藏时也一直有效**，
    /// 导航键只在显示期间有效，两者的生命周期不同；分开写，回调方一眼就能看出自己那一支的生效范围。
    /// 回调同样在 UI 线程上。注意注销那一刻队列里可能还留着一条迟到的 <c>WM_HOTKEY</c>，
    /// 所以回调方仍要自己判一次"浮窗现在可见吗"再动手。
    /// </summary>
    public event Action<OverlayHotkeyAction>? NavigationPressed;

    /// <summary>最终注册成功的那个候选键；<c>null</c> = 一个都没注册上。</summary>
    public OverlayHotkeyCandidate? RegisteredCandidate { get; }

    /// <summary>注册失败的那些候选键与各自的原因，用于给用户一句完整的解释。</summary>
    public IReadOnlyList<OverlayHotkeyFailure> Failures { get; }

    public bool IsRegistered => RegisteredCandidate is not null;

    /// <summary>
    /// 注册上的**不是**候选表里的第一个 —— 说明首选被别的程序占了。
    /// 设置页必须把这件事说出来：用户按首选键没反应时，否则只会以为功能坏了。
    /// </summary>
    public bool IsFallback => IsRegistered && Failures.Count > 0;

    /// <summary>
    /// 没注册上时给用户看的一句话（把每个候选键和它各自的原因都列出来）；注册成功时为 <c>null</c>。
    /// 失败**必须**让用户看见：热键被占是常态而非意外，静默降级成一堆"按了没反应"最糟。
    /// </summary>
    public string? FailureMessage { get; }

    /// <summary>实际生效的热键写法（<c>Ctrl+Alt+J</c>），直接显示在界面上。</summary>
    public string Description => RegisteredCandidate?.FriendlyName ?? "(一个都没注册上)";

    /// <summary>
    /// 依次尝试候选键，注册**第一个可用的**。
    ///
    /// 消息钩子只挂一次（它跟"最终注册了哪个组合键"无关）：放在候选循环外面，
    /// 免得为每个候选反复替换 / 还原窗口过程 —— <c>WindowMessageMonitor</c> 就是靠替换窗口过程实现的。
    ///
    /// <paramref name="candidates"/> 为 <c>null</c> 时用默认候选表；设置页将来可传入用户改过的键。
    /// 换键的姿势是**重建本对象**（热键是独占注册的，改键必须先注销），而不是就地改。
    /// </summary>
    public OverlayHotkeyRegistrar(Window window, ILogger logger,
        IReadOnlyList<OverlayHotkeyCandidate>? candidates = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        _logger = logger.ForContext<OverlayHotkeyRegistrar>();
        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(window);

        _monitor = new WindowMessageMonitor(window);
        _monitor.WindowMessageReceived += OnWindowMessageReceived;

        var selection = OverlayHotkeyCandidates.TrySelect(
            candidates ?? OverlayHotkeyCandidates.CreateDefaults(), TryRegister);

        RegisteredCandidate = selection.Candidate;
        Failures = selection.Failures;

        if (selection.IsRegistered)
        {
            _logger.Information("[浮窗] 全局热键注册成功: {Hotkey}（id={Id}，用了备选键={Fallback}）",
                Description, HotkeyId, selection.IsFallback);
            return;
        }

        FailureMessage = OverlayHotkeyCandidates.DescribeFailure(Failures);
        _logger.Warning("[浮窗] 全局热键注册失败: {Reason}", FailureMessage);
    }

    /// <summary>
    /// 真正调用 <c>RegisterHotKey</c>。交给 <see cref="OverlayHotkeyCandidates.TrySelect"/> 逐项调用，
    /// 这里只报「成没成、系统给的原因是什么」。失败原因取系统文案（最常见是「已被占用」）——
    /// 换成自己的措辞就会把「被谁占了」这个关键信息抹掉。
    /// </summary>
    private HotkeyRegistrationAttempt TryRegister(OverlayHotkeyCandidate candidate)
    {
        // MOD_NOREPEAT 由 Core 的 ToRegisterHotKeyFlags 强制带上：漏了不会报错，
        // 只会在用户按住热键时把「切换显隐」刷成不停闪烁。
        var flags = (HOT_KEY_MODIFIERS)OverlayHotkeyCandidates.ToRegisterHotKeyFlags(candidate.Modifiers);

        if (PInvoke.RegisterHotKey(_hwnd, HotkeyId, flags, candidate.VirtualKey))
        {
            _logger.Debug("[浮窗] 热键候选可用: {Hotkey}", candidate.FriendlyName);
            return HotkeyRegistrationAttempt.Ok;
        }

        var reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        _logger.Warning("[浮窗] 热键候选不可用: {Hotkey} 原因={Reason}", candidate.FriendlyName, reason);
        return HotkeyRegistrationAttempt.Failed(reason);
    }

    private void OnWindowMessageReceived(object? sender, WindowMessageEventArgs e)
    {
        if (e.Message.MessageId != WmHotkey)
            return;

        // 同一个窗口上注册着多个热键，靠 id 分辨。认不出的 id（不是我们注册的，或者是刚注销、
        // 消息已经排在队列里的那一条）一律忽略 —— 忽略比"照最像的那个处理"安全。
        var id = (int)e.Message.WParam;

        if (id == HotkeyId)
        {
            _logger.Debug("[浮窗] 收到 WM_HOTKEY，切换浮窗显隐");
            Pressed?.Invoke();
            return;
        }

        foreach (var (navigationId, action, _) in _navigation)
        {
            if (navigationId != id)
                continue;

            _logger.Debug("[浮窗] 收到 WM_HOTKEY，导航动作={Action}", action);
            NavigationPressed?.Invoke(action);
            return;
        }
    }

    // ── 导航键（只在浮窗显示期间注册）──────────────────────────

    /// <summary>
    /// 注册导航键。**浮窗显示时调**，隐藏时用 <see cref="UnregisterNavigationHotkeys"/> 还回去
    /// （为什么不能常驻注册见类注释）。
    ///
    /// 修饰键跟随**唤出键实际注册到的**那一个：首选被占时会退到备选（<c>Ctrl+Shift+J</c> 之类），
    /// 导航键若还钉死在 <c>Ctrl+Alt</c> 上，提示行写成一族键就成了骗人。
    ///
    /// 重复调用无副作用（已经注册着就直接返回）：显示 / 隐藏是两条独立的路，不该要求调用方自己记账。
    /// </summary>
    internal void RegisterNavigationHotkeys()
    {
        if (_disposed || RegisteredCandidate is null || _navigation.Count > 0)
            return;

        var modifiers = RegisteredCandidate.Modifiers;
        var flags = (HOT_KEY_MODIFIERS)OverlayHotkeyCandidates.ToRegisterHotKeyFlags(modifiers);

        for (var index = 0; index < NavigationKeys.Length; index++)
        {
            var (action, virtualKey, displayName) = NavigationKeys[index];
            var id = NavigationHotkeyIdBase + index;

            if (PInvoke.RegisterHotKey(_hwnd, id, flags, virtualKey))
            {
                _navigation.Add((id, action, displayName));
                continue;
            }

            // 少一个键不致命（其余照常可用），但要让用户查得出来 —— 提示行只列注册上的那些
            var reason = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            var hotkey = OverlayHotkeyCandidates.Describe(modifiers, displayName);

            if (_navigationFailures.Add(action))
                _logger.Warning("[浮窗] 导航热键 {Hotkey} 注册不上（{Reason}），这个键在浮窗显示时不可用",
                    hotkey, reason);
            else
                _logger.Debug("[浮窗] 导航热键 {Hotkey} 仍然注册不上（{Reason}）", hotkey, reason);
        }

        _logger.Information("[浮窗] 导航热键就绪（仅浮窗显示期间生效）: {Hint}", Hint);
    }

    /// <summary>
    /// 注销导航键。**浮窗隐藏时调**，不注销它们会一直被扣着不还给游戏 ——
    /// 用户看不见浮窗的时候按这几个键应该什么也不发生（那个组合属于他正在玩的游戏）。
    /// </summary>
    internal void UnregisterNavigationHotkeys()
    {
        foreach (var (id, _, _) in _navigation)
            PInvoke.UnregisterHotKey(_hwnd, id);

        _navigation.Clear();
    }

    /// <summary>
    /// 标题栏右侧那句提示：唤出键 + **此刻真的注册上**的导航键，如 <c>Ctrl+Alt+J / ↑↓ / Enter / R</c>。
    ///
    /// 一族键共享同一个修饰键前缀，所以前缀只写一次；只列注册上的那些 —— 注册不上却写在提示里
    /// 就是骗用户（唤出键那边"写死了就会骗人"是同一条道理）。
    /// </summary>
    internal string Hint =>
        _navigation.Count == 0
            ? Description
            : Description + " / " + string.Join(" / ", _navigation.Select(key => key.DisplayName));

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 先摘事件再注销：注销过程中若还有排队中的 WM_HOTKEY 打进来，不该再触发切换
        _monitor.WindowMessageReceived -= OnWindowMessageReceived;

        // 导航键可能还注册着（浮窗可见时退出程序就是这样）：一起还回去，别把它们扣在系统里
        UnregisterNavigationHotkeys();

        if (RegisteredCandidate is not null)
        {
            // RegisterHotKey 占的是系统级资源：不注销，这个组合键会在进程活着的期间一直不被放给别的程序
            PInvoke.UnregisterHotKey(_hwnd, HotkeyId);
            _logger.Debug("[浮窗] 已注销全局热键 {Hotkey}", Description);
        }
    }
}

/// <summary>
/// 浮窗热键能触发的动作。导航类只在**浮窗显示期间**才可能触发（热键随显隐注册 / 注销，
/// 见 <see cref="OverlayHotkeyRegistrar.RegisterNavigationHotkeys"/>）。
/// </summary>
internal enum OverlayHotkeyAction
{
    /// <summary>切换浮窗显隐（常驻的唤出键）。</summary>
    ToggleVisibility,

    /// <summary>选中行上移一行。</summary>
    SelectPrevious,

    /// <summary>选中行下移一行。</summary>
    SelectNext,

    /// <summary>切换选中行的勾选状态（与点勾选框同一条路）。</summary>
    ToggleSelected,

    /// <summary>手动刷新（多选模式下攒够了一次性生效）。</summary>
    Refresh
}