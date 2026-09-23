using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Services.AppManagement;
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
/// 线程：<see cref="InitializeAsync"/> 与热键回调都在 UI 线程上，可直接碰窗口。
/// </summary>
internal sealed class OverlayWindowService : IDisposable
{
    /// <summary>浮窗目前只对鸣潮开（本阶段的范围）。别的游戏不给它注册全局热键 ——
    /// 热键是系统级独占资源，白占一个组合键、对用户却是"按了没反应"。</summary>
    private const string SupportedGame = "WuWa";

    private readonly OverlayViewModel _viewModel;
    private readonly SelectedGameService _selectedGameService;
    private readonly NotificationManager _notificationManager;
    private readonly ILogger _logger;

    private OverlayWindow? _window;
    private OverlayHotkeyRegistrar? _hotkeys;
    private bool _disposed;

    public OverlayWindowService(ISkinManagerService skinManagerService,
        OverlayRefreshCoordinator refreshCoordinator, ILocalSettingsService localSettingsService,
        SelectedGameService selectedGameService, NotificationManager notificationManager, ILogger logger)
    {
        _selectedGameService = selectedGameService;
        _notificationManager = notificationManager;
        _logger = logger.ForContext<OverlayWindowService>();

        // ViewModel 不经过 DI：它是 internal 的、且 DI 的 ActivatorUtilities 只认公开构造函数。
        // 依赖项由本类转交，顺带保证"浮窗与设置页共用同一个刷新协调器"这件事是显式的。
        _viewModel = new OverlayViewModel(skinManagerService, refreshCoordinator, localSettingsService, logger);
    }

    /// <summary>
    /// 启动浮窗。当前不是鸣潮、或热键一个都没注册上时**不建窗口**，直接返回（各自有日志/提示）。
    /// </summary>
    public async Task InitializeAsync()
    {
        if (await _selectedGameService.GetSelectedGameAsync() != SupportedGame)
        {
            _logger.Debug("[浮窗] 当前选中的不是{Game}，不启动浮窗", SupportedGame);
            return;
        }

        // 先把设置与角色列表读出来：窗口的构造与首次摆位都依赖设置里的坐标
        await _viewModel.InitializeAsync();

        var window = new OverlayWindow(_viewModel, _logger);
        _window = window;

        // 热键是注册在浮窗自己身上的，所以窗口必须先存在
        var hotkeys = new OverlayHotkeyRegistrar(window, _logger);
        _hotkeys = hotkeys;

        if (!hotkeys.IsRegistered)
        {
            // 一个候选键都没注册上 = 浮窗没有任何入口。这种情况**必须**让用户看见：
            // 静默留着只会表现为"功能没做"，而真正的原因是热键被别的程序占了（原因里有系统文案）。
            _logger.Error("[浮窗] 全局热键注册失败，浮窗无法唤出: {Reason}", hotkeys.FailureMessage);

            _notificationManager.ShowNotification("浮窗热键注册失败",
                hotkeys.FailureMessage ?? "所有候选热键都被占用了，浮窗暂时无法唤出。",
                TimeSpan.FromSeconds(15));

            // 窗口保持从未显示的状态：既不占屏幕，也不至于让用户以为哪里冒出来一块东西
            return;
        }

        hotkeys.Pressed += ToggleOverlay;

        // 热键名由这里填：只有注册完才知道最终用的是首选键还是被挤到了备选键
        window.SetHotkeyHint(hotkeys.Description);

        if (hotkeys.IsFallback)
            _logger.Warning("[浮窗] 首发热键被占用，已自动改用备选键 {Hotkey}", hotkeys.Description);

        // 窗口在构造时就已经被 WinUI 建出来了（可能已可见），这里立刻收起来 ——
        // 浮窗的常态是隐藏，只在热键唤出时出现。
        window.HideOverlay();

        // 窗口关闭（正常退出）时把热键注销掉：RegisterHotKey 占的是系统级资源，
        // 不注销会在进程活着的期间一直扣着这个组合键不放给别的程序。
        window.Closed += (_, _) => Dispose();

        _logger.Information("[浮窗] 已就绪，热键 {Hotkey}（游戏在前台时也可用；再按一次隐藏）", hotkeys.Description);
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
            window.HideOverlay();
        else
            window.ShowOverlay();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // 先摘事件再注销，免得注销过程中打进来的 WM_HOTKEY 还去切一次显隐
        if (_hotkeys is not null)
        {
            _hotkeys.Pressed -= ToggleOverlay;
            _hotkeys.Dispose();
            _hotkeys = null;
        }

        _window = null;

        _logger.Debug("[浮窗] 已停止（热键已注销）");
    }
}