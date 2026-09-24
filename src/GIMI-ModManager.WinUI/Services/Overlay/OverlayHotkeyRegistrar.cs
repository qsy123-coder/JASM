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
/// </summary>
internal sealed class OverlayHotkeyRegistrar : IDisposable
{
    /// <summary>
    /// 本浮窗热键的 id。同一个窗口上将来可能注册多个热键，<c>WM_HOTKEY</c> 的 wParam 就是靠它区分的，
    /// 所以收消息时必须比对。
    /// </summary>
    internal const int HotkeyId = 0x4A53;

    /// <summary><c>WM_HOTKEY</c> 是 <c>#define</c> 而非 API，CsWin32 不生成它，自己收一个。</summary>
    private const int WmHotkey = 0x0312;

    private readonly ILogger _logger;
    private readonly WindowMessageMonitor _monitor;
    private readonly HWND _hwnd;
    private bool _disposed;

    /// <summary>
    /// 热键被按下。回调在 UI 线程上（<c>WM_HOTKEY</c> 由窗口消息循环派发），可直接改界面 ——
    /// 但**不要**在这里做耗时的事，切显隐是即时动作，重活（刷新）应交给后台。
    /// </summary>
    public event Action? Pressed;

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
        // 只认自己这个 id：同一个窗口上将来可能有别的热键
        if (e.Message.MessageId != WmHotkey || (int)e.Message.WParam != HotkeyId)
            return;

        _logger.Debug("[浮窗] 收到 WM_HOTKEY，切换浮窗显隐");
        Pressed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 先摘事件再注销：注销过程中若还有排队中的 WM_HOTKEY 打进来，不该再触发切换
        _monitor.WindowMessageReceived -= OnWindowMessageReceived;

        if (RegisteredCandidate is not null)
        {
            // RegisterHotKey 占的是系统级资源：不注销，这个组合键会在进程活着的期间一直不被放给别的程序
            PInvoke.UnregisterHotKey(_hwnd, HotkeyId);
            _logger.Debug("[浮窗] 已注销全局热键 {Hotkey}", Description);
        }
    }
}