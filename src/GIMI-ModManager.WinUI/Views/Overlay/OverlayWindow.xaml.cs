using Windows.Foundation;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.Overlay;
using GIMI_ModManager.WinUI.ViewModels.Overlay;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Serilog;
using WinUIEx;

namespace GIMI_ModManager.WinUI.Views.Overlay;

/// <summary>
/// 游戏内的浮窗：无边框 + 置顶 + **不夺前台焦点**。
///
/// 窗口形态与自愈逻辑来自 Phase 0 原型（<c>src/OverlaySpike</c>），三件事必须照搬，别"优化"掉：
///
///   1. **置顶只认扩展样式**：<c>OverlappedPresenter.IsAlwaysOnTop</c> 与 WinUIEx 的
///      <c>WindowEx.IsAlwaysOnTop</c> 都不写 <c>WS_EX_TOPMOST</c>，而两者都读回 true。
///   2. **置顶位要定期自愈**：实测它在窗口显示之后会被抹掉，抹掉的表现就是"浮窗被别的窗口盖住／看起来消失了"，
///      而热键只切显隐、修不了样式，唤出也白搭。
///   3. **显隐走 <c>ShowWindow(SW_*)</c>**，不走 <c>AppWindow.Show()</c>／<c>Window.Activate()</c> 那一套 ——
///      后者可能顺带激活窗口，一唤出就把游戏的前台挤掉。
/// </summary>
public sealed partial class OverlayWindow : WindowEx
{
    /// <summary>浮窗尺寸。**DIP**（<see cref="WindowEx.Width"/> 的语义），由 WinUIEx 按 DPI 换成物理像素。</summary>
    private const int OverlayWidth = 470;

    private const int OverlayHeight = 440;

    /// <summary>置顶自愈的间隔。1 秒是原型的取值：既够快（用户几乎来不及看到它被盖住），也不至于每秒都去动窗口。</summary>
    private static readonly TimeSpan TopMostCheckInterval = TimeSpan.FromSeconds(1);

    private readonly OverlayWindowStyles _styles;
    private readonly ILogger _logger;
    private readonly HWND _hwnd;
    private readonly DispatcherTimer _topMostTimer;

    // 拖动用：按下瞬间的指针位置（DIP）与窗口位置（物理像素）。两者单位不同，换算见 OnDragStripPointerMoved。
    private Point _dragOriginPointer;
    private PointInt32 _dragOriginWindow;
    private bool _isDragging;

    /// <summary>浮窗的 ViewModel（internal：它和它手上的协调器都只在本程序集里用）。</summary>
    internal OverlayViewModel ViewModel { get; }

    internal OverlayWindow(OverlayViewModel viewModel, ILogger logger)
    {
        ViewModel = viewModel;
        _logger = logger.ForContext<OverlayWindow>();

        InitializeComponent();

        _styles = new OverlayWindowStyles(_logger);
        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

        ConfigureOverlayWindow();

        _topMostTimer = new DispatcherTimer { Interval = TopMostCheckInterval };
        _topMostTimer.Tick += (_, _) => EnsureTopMost("定期自愈");
        _topMostTimer.Start();

        Closed += OnClosed;

        // 尺寸/位置要等窗口真的显示出来之后才算得准：构造阶段 AppWindow.Size 还是 0，
        // 那时算出来的"居中"会跑到屏幕左上角（原型踩过）。
        Activated += OnFirstActivated;
    }

    /// <summary>
    /// 把这扇窗配置成浮窗形态。顺序有讲究：先边框、再置顶、**最后** NOACTIVATE ——
    /// NOACTIVATE 要在窗口第一次被激活之前就位，否则那一次激活照样会把游戏的前台抢走。
    /// </summary>
    private void ConfigureOverlayWindow()
    {
        Width = OverlayWidth;
        Height = OverlayHeight;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // 不进 Alt+Tab、不占任务栏槽位（用托管 API，比手加 WS_EX_TOOLWINDOW 干净）
        AppWindow.IsShownInSwitchers = false;

        var exStyle = _styles.SetTopMost(_hwnd, onTop: true);
        _logger.Debug("浮窗构造：置顶后扩展样式 0x{ExStyle:X16}，TOPMOST={HasTopMost}",
            (long)exStyle, OverlayWindowStyles.HasTopMost(exStyle));

        _styles.SetNoActivate(_hwnd, enabled: true);
    }

    /// <summary>把热键名填进标题栏右侧。由 <c>OverlayWindowService</c> 在注册完热键后调用。</summary>
    public void SetHotkeyHint(string text) => HotkeyHintText.Text = text;

    /// <summary>
    /// 显示浮窗，**不激活**。隐藏状态下热键照样有效（<c>WM_HOTKEY</c> 仍会派发到隐藏窗口的消息队列），
    /// 所以隐藏不是"关掉"，只是看不见。
    /// </summary>
    public void ShowOverlay()
    {
        EnsureTopMost("唤出时");

        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        // 记下"系统实际的"可见性与前台归属：ShowWindow 不返回成功与否，而"前台有没有被我们抢走"
        // 只有回读才算数 —— 这条日志也是实机验收"点了浮窗游戏没掉全屏"的证据。
        _logger.Debug("浮窗显示：IsWindowVisible={IsVisible} 前台是否本窗口={IsForeground}",
            PInvoke.IsWindowVisible(_hwnd), OverlayWindowStyles.IsOwnWindowForeground(_hwnd));
    }

    public void HideOverlay()
    {
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        _logger.Debug("浮窗隐藏：IsWindowVisible={IsVisible}", PInvoke.IsWindowVisible(_hwnd));
    }

    /// <summary>窗口此刻是否可见。取系统实际值，不是本类的意图值。</summary>
    public bool IsOverlayVisible => PInvoke.IsWindowVisible(_hwnd);

    /// <summary>
    /// 确认窗口**真的**置顶了，没有就用 <c>SetWindowPos</c> 补上。
    /// 详见类注释第 1、2 条 —— 这里一律以扩展样式为准做证伪。
    /// </summary>
    private void EnsureTopMost(string stage)
    {
        var current = OverlayWindowStyles.ReadExStyle(_hwnd);

        if (OverlayWindowStyles.HasTopMost(current))
            return;

        var after = _styles.SetTopMost(_hwnd, onTop: true);

        _logger.Debug("浮窗置顶位在「{Stage}」时缺失，已补回：0x{Before:X16} -> 0x{After:X16}，TOPMOST={HasTopMost}",
            stage, (long)current, (long)after, OverlayWindowStyles.HasTopMost(after));
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;

        RestorePosition();

        // 放在首次激活之后：这时窗口已经真的显示出来了，样式位才是可信的。
        // 构造阶段读到的值可能是"还没应用"的中间态（原型实测就是这个坑）。
        EnsureTopMost("首次激活后");

        _logger.Information("浮窗就绪：位置={X},{Y} 尺寸={Width}x{Height}（物理像素）",
            AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
    }

    /// <summary>
    /// 把窗口摆回上次拖动的位置；没存过 / 存的坐标已经不在当前工作区里，就居中。
    ///
    /// 夹取这件事交给 <see cref="OverlayPlacement"/>（纯算术、有单测）：浮窗没有标题栏，
    /// 一旦被摆到屏幕外就再也拖不回来了，而换了显示器或改了缩放都会让旧坐标失效。
    /// </summary>
    private void RestorePosition()
    {
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;

        // 两边都是物理像素：WorkArea 与 AppWindow.Size 同一坐标系，不需要 DPI 换算
        var (x, y) = OverlayPlacement.ResolveTopLeft(
            ViewModel.Settings.SavedTopLeft,
            new OverlayRect(workArea.X, workArea.Y, workArea.Width, workArea.Height),
            size.Width, size.Height);

        AppWindow.Move(new PointInt32(x, y));
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => HideOverlay();

    private void OnDragStripPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 指针捕获：拖出窗口范围时仍然能收到 PointerMoved，否则拖动会在边界处断掉
        if (!((UIElement)sender).CapturePointer(e.Pointer))
            return;

        _dragOriginPointer = e.GetCurrentPoint(null).Position;
        _dragOriginWindow = AppWindow.Position;
        _isDragging = true;
    }

    private void OnDragStripPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            return;

        var current = e.GetCurrentPoint(null).Position;

        // 指针坐标是 DIP（逻辑像素），AppWindow.Move 收的是物理像素 ——
        // 不做这一步换算，在非 100% 缩放的屏幕上拖动会明显跟不上手。
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var dx = (int)Math.Round((current.X - _dragOriginPointer.X) * scale);
        var dy = (int)Math.Round((current.Y - _dragOriginPointer.Y) * scale);

        AppWindow.Move(new PointInt32(_dragOriginWindow.X + dx, _dragOriginWindow.Y + dy));
    }

    private void OnDragStripPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        RememberPosition();
    }

    /// <summary>
    /// 拖动被系统打断（窗口失去捕获）时也要收尾，否则 <c>_isDragging</c> 会一直挂着，
    /// 之后鼠标在标题条上划过都会拖着窗口跑。
    /// </summary>
    private void OnDragStripPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            return;

        _isDragging = false;
        RememberPosition();
    }

    private void RememberPosition()
    {
        var position = AppWindow.Position;

        // 不 await：落盘慢一点无所谓，拖动结束时界面不该卡一下。
        // RememberWindowPositionAsync 自己吞掉异常，不会变成未观察的异常。
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        ViewModel.RememberWindowPositionAsync(position.X, position.Y);
#pragma warning restore CS4014
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _topMostTimer.Stop();
        RememberPosition();
        _logger.Debug("浮窗已关闭");
    }
}