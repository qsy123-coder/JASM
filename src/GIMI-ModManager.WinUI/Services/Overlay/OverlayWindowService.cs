using CommunityToolkitWrapper;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.Input;
using GIMI_ModManager.WinUI.Services.ModHandling;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.ViewModels.Overlay;
using GIMI_ModManager.WinUI.Views.Overlay;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>
/// 浮窗的宿主：造窗口、注册全局热键、按热键切显隐。全程序只有一个实例（DI 单例）。
///
/// 为什么不复用 <c>IWindowManagerService.CreateWindow</c>：它建窗口时会 <c>Show()</c> / <c>BringToFront()</c>，
/// 那正是浮窗最不能有的行为（<c>WS_EX_NOACTIVATE</c> 会被自己的激活动作抵消，一唤出就把游戏的前台挤掉）。
/// 浮窗的显隐只走 <c>ShowWindow(SW_*)</c>，见 <see cref="OverlayWindow"/>。
///
/// 窗口是**在启动时就建好、然后立刻藏起来**的，不是"第一次按热键才建"：全局热键必须注册在某个窗口上
/// （<c>RegisterHotKey</c> 要 HWND），没有窗口就注册不了热键，也就没有"第一次按热键"这回事。
/// 隐藏的窗口照样收 <c>WM_HOTKEY</c>（消息仍会派发到它的消息队列），所以藏起来不影响唤出。
///
/// <see cref="InitializeAsync"/> 会被调用**不止一次**（启动时一次、启动向导保存后一次），所以它是幂等的：
/// 窗口只建一次、热键按"当前选中的游戏"装了拆、拆了装。细节见各成员的说明。
///
/// 线程：<see cref="InitializeAsync"/> 与热键回调都在 UI 线程上，可直接碰窗口。
/// </summary>
internal sealed class OverlayWindowService : IDisposable
{
    /// <summary>浮窗目前只对鸣潮开（本阶段的范围）。别的游戏不给它注册全局热键 ——
    /// 热键是系统级独占资源，白占一个组合键、对用户却是"按了没反应"。</summary>
    private const string SupportedGame = "WuWa";

    private readonly OverlayViewModel _viewModel;
    private readonly OverlayScreenshotService _screenshotService;
    private readonly SelectedGameService _selectedGameService;
    private readonly NotificationManager _notificationManager;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger _logger;

    /// <summary>
    /// 浮窗窗口。**建一次就一直留着**，直到进程退出（<see cref="Dispose"/>）。
    ///
    /// 窗口本身与游戏无关 —— 列哪些角色、勾哪个 Mod，走的都是同一套服务；跟游戏绑定的只有
    /// "能不能用热键唤出它"这一件事，那由 <see cref="InitializeAsync"/> 按当前选中的游戏动态管。
    /// 所以换了游戏**不该重建窗口**：重建要多挂一次拖放落点、重新摆一次位置，全是白费。
    /// </summary>
    private OverlayWindow? _window;

    /// <summary>热键的注册者（唤出键 + 浮窗显示期间的导航键）；<c>null</c> = 此刻一个都没注册。</summary>
    private OverlayHotkeyRegistrar? _hotkeys;

    /// <summary>
    /// 初始化**已经为哪个游戏做过**了；<c>null</c> = 还没做过（或上一次以失败告终）。
    ///
    /// 它是 <see cref="InitializeAsync"/> 的幂等判据 —— 同一个游戏重复调用直接返回。
    /// 记"哪个游戏"而不是"做过没有"：换了游戏必须**重新**做（手上的热键要先还给系统再按新游戏注册），
    /// 而记游戏名才能一并表达"当前不是鸣潮，确认过不用做"。
    ///
    /// 为什么要它：<c>RegisterHotKey</c> 是系统级**独占**资源 —— 同一个组合键注册第二次会被系统拒掉，
    /// 于是新的一份一个键都拿不到、旧的还扣着不放，而用户看到的是"热键注册失败"、功能本来是好的。
    /// </summary>
    private string? _initializedForGame;

    private bool _disposed;

    public OverlayWindowService(ISkinManagerService skinManagerService, IGameService gameService,
        OverlayRefreshCoordinator refreshCoordinator, ILocalSettingsService localSettingsService,
        ModDragAndDropService dragAndDropService, OverlayScreenshotService screenshotService,
        ModSettingsService modSettingsService, IGameKeySender gameKeySender,
        SelectedGameService selectedGameService, NotificationManager notificationManager,
        WuwaModFixerEngine fixerEngine, WuwaFixSession fixSession, WuwaRollbackService rollbackService,
        ILogger logger)
    {
        _selectedGameService = selectedGameService;
        _screenshotService = screenshotService;
        _notificationManager = notificationManager;
        _localSettingsService = localSettingsService;
        _logger = logger.ForContext<OverlayWindowService>();

        // ViewModel 不经过 DI：它是 internal 的、且 DI 的 ActivatorUtilities 只认公开构造函数。
        // 依赖项由本类转交，顺带保证"浮窗与设置页共用同一个刷新协调器"这件事是显式的。
        _viewModel = new OverlayViewModel(skinManagerService, gameService, refreshCoordinator,
            localSettingsService, dragAndDropService, screenshotService, modSettingsService,
            gameKeySender, notificationManager, fixerEngine, fixSession, rollbackService, logger);
    }

    /// <summary>
    /// 启动浮窗。当前不是鸣潮、或热键一个都没注册上时**不建窗口**，直接返回（各自有日志/提示）。
    ///
    /// <para>
    /// <b>可以重复调用，且是幂等的</b>：启动时调一次（<c>ActivationService.StartupAsync</c>），
    /// 启动向导里保存好设置之后再调一次（<c>StartupViewModel.SaveStartupSettings</c>）。后一次不是可有可无的 ——
    /// 首次启动时启动那次跑在"游戏还没选"的那一刻（选游戏正是向导里的一步），于是它判定「不是鸣潮」直接返回、
    /// 建窗口与注册热键**都没发生**，而在那之后没有任何东西会初始化第二次：用户看到的是
    /// 「在向导里配好 Mod 环境、点保存、Ctrl+Alt+J 怎么按都没反应，重启 JASM 才好」。
    /// </para>
    ///
    /// <para>
    /// 同一个游戏已经就绪时什么都不做；**换了游戏**则把热键还给系统（见 <see cref="ReleaseHotkeys"/>），
    /// 换回鸣潮时再注册一次 —— 窗口不重建，理由见 <see cref="_window"/> 的说明。
    /// </para>
    /// </summary>
    public async Task InitializeAsync()
    {
        // 窗口已经关掉了（进程正在退出）就不要再建了：这时做什么都是在一个空句柄上做，注定失败
        if (_disposed)
            return;

        // 整段都搬回 UI 线程：读游戏之后的每一步都可能碰 XAML 与窗口（建窗口、注册热键、切显隐），
        // 而在线程池上 new Window() 不是普通异常，是 stowed exception（0xc000027b）——
        // 直接把进程打掉、日志里连一行都不留（见 InitializeOnUiThreadAsync 的说明）。
        await App.MainWindow.DispatcherQueue.EnqueueAsync(InitializeOnUiThreadAsync);
    }

    /// <summary>
    /// 建窗口、注册热键、把窗口收起来。**必须在 UI 线程上跑**。
    ///
    /// 为什么这里要自己切线程，而不是指望调用方本来就在 UI 线程上：<c>ActivationService.StartupAsync</c>
    /// 里前面几步带着 <c>ConfigureAwait(false)</c>，续体会落到线程池上；而在线程池上 <c>new Window()</c>
    /// 会以 <c>RPC_E_WRONG_THREAD</c> 失败 —— 这种失败不是普通异常，它变成 stowed exception
    /// （<c>0xc000027b</c>）**直接把进程打掉**，日志里连一行都不留（本机冒烟测试踩到，查了半天事件查看器）。
    /// </summary>
    private async Task InitializeOnUiThreadAsync()
    {
        var selectedGame = await _selectedGameService.GetSelectedGameAsync();

        // 已经为这个游戏做过：什么都不做（理由见 _initializedForGame）
        if (selectedGame == _initializedForGame)
            return;

        // 先占位再往下做：下面建窗口那一段带一个 await，而本方法是异步的 ——
        // 两次调用挨得太近（启动那次还没跑完、向导里又保存了一次）会在这里交错，
        // 不先占位就会各建一扇窗、第二份热键还被系统拒掉，表现是一次假的"热键注册失败"。
        _initializedForGame = selectedGame;

        // 先把上一次留给别的游戏的热键还回去：当前不是鸣潮却还占着 Ctrl+Alt+J，
        // 对用户就是"按了没反应"外加白占一个组合键（见 SupportedGame）。
        // 窗口留着 —— 它跟游戏无关，换回鸣潮时直接拿它再注册一次就行。
        ReleaseHotkeys();

        if (selectedGame != SupportedGame)
        {
            _logger.Debug("[浮窗] 当前选中的不是{Game}，不启动浮窗", SupportedGame);

            // 浮窗可能正显示着（用向导换游戏时用户人就在 JASM 里，浮窗还挂在屏幕上），跟着一起收掉
            _window?.HideOverlay();
            return;
        }

        var window = _window;

        if (window is null)
        {
            // 先把设置与角色列表读出来：窗口的构造与首次摆位都依赖设置里的坐标
            await _viewModel.InitializeAsync();

            window = new OverlayWindow(_viewModel, _screenshotService, _logger);
            _window = window;

            // 窗口关闭（正常退出）时把热键注销掉：RegisterHotKey 占的是系统级资源，
            // 不注销会在进程活着的期间一直扣着这个组合键不放给别的程序。
            window.Closed += (_, _) => Dispose();

            // 窗口在构造时就已经被 WinUI 建出来了（可能已可见），这里立刻收起来 ——
            // 浮窗的常态是隐藏，只在热键唤出时出现。
            window.HideOverlay();
        }

        // 热键是注册在浮窗自己身上的，所以窗口必须先存在。
        // 用户自定义键排在最前；它被占用时仍按默认候选表兜底（设置页会显示实际生效的是哪个）。
        var hotkeys = new OverlayHotkeyRegistrar(window, _logger, await BuildToggleCandidatesAsync());

        if (!hotkeys.IsRegistered)
        {
            // 一个候选键都没注册上 = 浮窗没有任何入口。这种情况**必须**让用户看见：
            // 静默留着只会表现为"功能没做"，而真正的原因是热键被别的程序占了（原因里有系统文案）。
            _logger.Error("[浮窗] 全局热键注册失败，浮窗无法唤出: {Reason}", hotkeys.FailureMessage);

            _notificationManager.ShowNotification("浮窗热键注册失败",
                hotkeys.FailureMessage ?? "所有候选热键都被占用了，浮窗暂时无法唤出。",
                TimeSpan.FromSeconds(15));

            // 一个键都没落下的注册者就别留着了：Dispose 会把挂上去的消息钩子摘掉，
            // 下一次重试才能干净地再挂一个。
            // **把记账退回未做**：热键被别的程序占着是**会变的**（占它的那个一关就腾出来了），
            // 记成"这个游戏做过了"会让后面每次初始化都直接返回、再也不会重试。
            // 窗口保持从未显示的状态：既不占屏幕，也不至于让用户以为哪里冒出来一块东西。
            hotkeys.Dispose();
            _initializedForGame = null;
            return;
        }

        AttachHotkeys(hotkeys, registerNavigation: false);

        if (hotkeys.IsFallback)
            _logger.Warning("[浮窗] 首发热键被占用，已自动改用备选键 {Hotkey}", hotkeys.Description);

        _logger.Information("[浮窗] 已就绪，热键 {Hotkey}（游戏在前台时也可用；再按一次隐藏）", hotkeys.Description);
    }

    /// <summary>当前实际生效的唤出键；未注册时为 <c>(未注册)</c>。</summary>
    public string ToggleHotkeyDescription => _hotkeys?.Description ?? "(未注册)";

    /// <summary>当前用的不是候选表第一个（自定义键被占用退到了默认键）。设置页据此提示。</summary>
    public bool IsToggleHotkeyFallback => _hotkeys?.IsFallback == true;

    /// <summary>唤出键当前是否注册着（<c>false</c> + 窗口存在 = 全部候选被占用）。</summary>
    public bool IsToggleHotkeyRegistered => _hotkeys is not null;

    /// <summary>浮窗窗口是否已建出来（用来区分“还没初始化”和“初始化了但热键全失败”）。</summary>
    public bool IsOverlayWindowCreated => _window is not null;

    /// <summary>浮窗是否已经为当前选中游戏初始化过。</summary>
    public bool IsInitializedForSupportedGame =>
        _initializedForGame == SupportedGame && _window is not null;

    /// <summary>
    /// 供主窗口「打开浮窗」按钮使用：按当前游戏初始化后直接显示浮窗。
    /// 不要求热键注册成功 —— 热键被占时按钮仍应能用。
    /// </summary>
    public async Task<bool> ShowOverlayAsync()
    {
        if (_disposed)
            return false;

        var selectedGame = await _selectedGameService.GetSelectedGameAsync();
        if (selectedGame != SupportedGame)
            return false;

        await InitializeAsync();

        return await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            if (_window is null)
                return false;

            ShowOverlay();
            return true;
        });
    }

    /// <summary>
    /// 换唤出热键。新键先探测、再拆旧键：被占用时旧键原样保留，设置也不用改。
    /// 当前不是鸣潮时只回「保存后生效」——真正的注册等切到鸣潮初始化时再做。
    /// </summary>
    public async Task<OverlayHotkeyChangeResult> TryChangeToggleHotkeyAsync(OverlayHotkeyCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (candidate.Modifiers == HotkeyModifiers.None)
            return new OverlayHotkeyChangeResult(false, "快捷键至少需要 Ctrl / Alt / Shift / Win 之一", null);

        if (_disposed)
            return new OverlayHotkeyChangeResult(false, "浮窗已关闭", null);

        var selectedGame = await _selectedGameService.GetSelectedGameAsync();
        if (selectedGame != SupportedGame)
            return new OverlayHotkeyChangeResult(true, null, candidate.FriendlyName);

        await InitializeAsync();
        return await App.MainWindow.DispatcherQueue.EnqueueAsync(() => ApplyHotkeyChange(candidate));
    }

    private OverlayHotkeyChangeResult ApplyHotkeyChange(OverlayHotkeyCandidate candidate)
    {
        var window = _window;
        if (window is null)
            return new OverlayHotkeyChangeResult(false, "浮窗尚未初始化", null);

        var current = _hotkeys;
        if (current?.RegisteredCandidate is { } registered &&
            registered.Modifiers == candidate.Modifiers && registered.VirtualKey == candidate.VirtualKey)
            return new OverlayHotkeyChangeResult(true, null, registered.FriendlyName);

        if (current is not null && !current.IsCandidateAvailable(candidate))
            return new OverlayHotkeyChangeResult(false, "该组合键已被其他程序占用", current.Description);

        var previous = current?.RegisteredCandidate;
        var wasVisible = window.IsOverlayVisible;
        ReleaseHotkeys();

        var replacement = new OverlayHotkeyRegistrar(window, _logger, [candidate]);
        if (!replacement.IsRegistered)
        {
            var error = replacement.FailureMessage ?? "该组合键无法注册";
            replacement.Dispose();
            RestoreHotkeys(window, previous, wasVisible);
            return new OverlayHotkeyChangeResult(false, error, _hotkeys?.Description);
        }

        AttachHotkeys(replacement, wasVisible);
        _logger.Information("[浮窗] 唤出热键已切换为 {Hotkey}", replacement.Description);
        return new OverlayHotkeyChangeResult(true, null, replacement.Description);
    }

    private void RestoreHotkeys(OverlayWindow window, OverlayHotkeyCandidate? previous, bool wasVisible)
    {
        if (previous is null)
            return;

        var restored = new OverlayHotkeyRegistrar(window, _logger, [previous]);
        if (!restored.IsRegistered)
        {
            var reason = restored.FailureMessage;
            restored.Dispose();
            _logger.Warning("[浮窗] 新热键失败后恢复旧热键也失败: {Reason}", reason);
            return;
        }

        AttachHotkeys(restored, wasVisible);
    }

    private void AttachHotkeys(OverlayHotkeyRegistrar hotkeys, bool registerNavigation)
    {
        _hotkeys = hotkeys;
        hotkeys.Pressed += ToggleOverlay;
        hotkeys.NavigationPressed += OnNavigationPressed;
        hotkeys.ScreenshotPressed += OnScreenshotPressed;

        if (registerNavigation)
            hotkeys.RegisterNavigationHotkeys();

        var window = _window;
        if (window is null)
            return;

        // 热键名由这里填：只有注册完才知道最终用的是首选键还是被挤到了备选键
        window.SetHotkeyHint(hotkeys.Description);
        window.SetScreenshotHotkeyHint(hotkeys.ScreenshotDescription);
    }

    /// <summary>
    /// 自定义键在前、默认候选在后。自定义键被占用时仍至少有默认键可用，
    /// 设置页通过 <see cref="IsToggleHotkeyFallback"/> 把「实际用的不是你要的键」说出来。
    /// </summary>
    private async Task<IReadOnlyList<OverlayHotkeyCandidate>> BuildToggleCandidatesAsync()
    {
        var defaults = OverlayHotkeyCandidates.CreateDefaults();
        var custom = await ReadCustomHotkeyAsync();
        if (custom is null)
            return defaults;

        var candidates = new List<OverlayHotkeyCandidate>(defaults.Count + 1) { custom };
        candidates.AddRange(defaults.Where(candidate =>
            candidate.Modifiers != custom.Modifiers || candidate.VirtualKey != custom.VirtualKey));
        return candidates;
    }

    private async Task<OverlayHotkeyCandidate?> ReadCustomHotkeyAsync()
    {
        try
        {
            var settings = await _localSettingsService
                .ReadOrCreateSettingAsync<OverlaySettings>(OverlaySettings.Key, SettingScope.App);

            _logger.Information("[浮窗] 自定义热键设置: mods={Modifiers}, vk={VirtualKey}, name={Name}",
                settings.OverlayHotkeyModifiers, settings.OverlayHotkeyVirtualKey, settings.OverlayHotkeyKeyName);

            if (settings.OverlayHotkeyModifiers is not { } modifiers
                || settings.OverlayHotkeyVirtualKey is not { } virtualKey
                || modifiers == 0
                || virtualKey is <= 0 or > 0xFF)
                return null;

            var displayName = string.IsNullOrWhiteSpace(settings.OverlayHotkeyKeyName)
                ? $"VK{virtualKey:X2}"
                : settings.OverlayHotkeyKeyName;

            return new OverlayHotkeyCandidate((HotkeyModifiers)(uint)modifiers, (ushort)virtualKey, displayName);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗] 读取自定义热键失败，沿用默认候选");
            return null;
        }
    }

    /// <summary>
    /// 把注册着的那一份热键还给系统（唤出键 + 当前可能注册着的导航键），**窗口本身不动**。
    ///
    /// 换游戏时调：热键跟着"当前游戏是不是鸣潮"走，不跟着窗口走（见 <see cref="_window"/> 的说明）。
    /// 之后 <see cref="InitializeOnUiThreadAsync"/> 再按新游戏决定要不要注册一次 ——
    /// 换键的姿势是**重建注册者**而不是就地把键改掉：<c>RegisterHotKey</c> 是独占的，必须先注销。
    ///
    /// 只管热键，不碰 <see cref="_initializedForGame"/>：那是"为哪个游戏初始化过"的记账，
    /// 由调用方按自己的语义写（换游戏是**重新**记账，不是清空）。
    /// </summary>
    private void ReleaseHotkeys()
    {
        if (_hotkeys is null)
            return;

        // 先摘事件再注销，免得注销过程中打进来的 WM_HOTKEY 还去切一次显隐
        _hotkeys.Pressed -= ToggleOverlay;
        _hotkeys.NavigationPressed -= OnNavigationPressed;
        _hotkeys.ScreenshotPressed -= OnScreenshotPressed;
        _hotkeys.Dispose();
        _hotkeys = null;
    }

    /// <summary>
    /// 热键回调：切显隐。判断依据取系统**实际**的可见性（<see cref="OverlayWindow.IsOverlayVisible"/>），
    /// 不是本类记的意图值 —— 窗口可能被别的代码藏起来，记着的那份就骗人了。
    /// </summary>
    private void ToggleOverlay()
    {
        var window = _window;
        if (window is null)
            return;

        if (window.IsOverlayVisible)
            HideOverlay();
        else
            ShowOverlay();
    }

    /// <summary>
    /// 唤出浮窗。导航键**在这里才注册**（理由见 <see cref="OverlayHotkeyRegistrar.RegisterNavigationHotkeys"/>），
    /// 注册完再把这一族键写进提示行 —— 只有这样提示里写的才是**真注册上**的那几个，
    /// 某个键被别的程序占了就不会被白纸黑字地承诺出去。
    /// </summary>
    private void ShowOverlay()
    {
        var window = _window;
        if (window is null)
            return;

        // 每次唤出都重新对一遍角色列表：启动那一刻建的那份快照可能早于 mod 扫描完成，而那时它是空的
        // 且**不会自愈**（空态分支连订阅都不建）。实机症状就是「浮窗里一个角色都没有」——
        // 详细理由见 OverlayViewModel.RefreshCharacters。
        _viewModel.RefreshCharacters();

        _hotkeys?.RegisterNavigationHotkeys();
        window.SetHotkeyHint(_hotkeys?.Hint ?? string.Empty);
        window.ShowOverlay();
    }

    /// <summary>
    /// 藏起浮窗，并把导航键还回系统。顺序是先藏后还：藏在先，这一拍里迟到的 <c>WM_HOTKEY</c>
    /// 就会被 <see cref="OnNavigationPressed"/> 的可见性判断挡掉，不会让一个已经看不见的浮窗去刷新游戏。
    /// </summary>
    private void HideOverlay()
    {
        _window?.HideOverlay();
        _hotkeys?.UnregisterNavigationHotkeys();
    }

    /// <summary>
    /// 截图热键：按上次区域截图。**浮窗隐藏时也有效** —— 这正是它的使用场景
    /// （在游戏里看到满意效果，不唤出浮窗直接按一下）。
    /// 窗口不存在（没选鸣潮）时什么也不做。
    /// </summary>
    private void OnScreenshotPressed()
    {
        var window = _window;
        if (window is null)
            return;

        _ = window.CaptureLastRegionAsync();
    }

    /// <summary>
    /// 导航热键回调：上下移动选中行、切换勾选、手动刷新。
    ///
    /// 自己再判一次可见性：注册本来就随显隐走，但注销那一刻队列里可能还留着一条迟到的
    /// <c>WM_HOTKEY</c>；而这一支里有两条会**碰游戏**（切换、刷新都会让游戏重载 Mod 池），
    /// 让一个藏起来的浮窗去动游戏是明确的错误。
    /// </summary>
    private void OnNavigationPressed(OverlayHotkeyAction action)
    {
        var window = _window;
        if (window is null || !window.IsOverlayVisible)
            return;

        // 修复页没有键盘可操作的目标：↑/↓ 会切换隐藏的 Mod 列表、Enter 会改盘、R 会刷新。
        // 删除确认卡盖着时同理：卡片要用户明确点“删除/取消”，背后不能被热键改动。
        if (window.ViewModel.IsFixTabActive || window.ViewModel.IsDeleteConfirmVisible)
            return;

        switch (action)
        {
            case OverlayHotkeyAction.SelectPrevious:
                window.ViewModel.MoveSelection(-1);
                break;

            case OverlayHotkeyAction.SelectNext:
                window.ViewModel.MoveSelection(1);
                break;

            // 这两个走 ViewModel 的命令而不是普通方法：命令自带"运行中不重入"，
            // 连按回车 / 连按刷新不会让同一次动盘或同一次刷新叠着跑。
            case OverlayHotkeyAction.ToggleSelected:
                window.ViewModel.ToggleSelectedCommand.Execute(null);
                break;

            case OverlayHotkeyAction.Refresh:
                window.ViewModel.RefreshNowCommand.Execute(null);
                break;

            default:
                // ToggleVisibility 走的是 Pressed 那条路，不该出现在这里
                _logger.Debug("[浮窗] 收到不归导航处理的动作 {Action}", action);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        ReleaseHotkeys();

        _window = null;

        _logger.Debug("[浮窗] 已停止（热键已注销）");
    }
}

/// <summary>改键结果。<see cref="EffectiveHotkey"/> 是当前实际生效的键（失败时用于提示）。</summary>
public sealed record OverlayHotkeyChangeResult(bool Success, string? Error, string? EffectiveHotkey);