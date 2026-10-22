using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.Overlay;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinUIEx;

namespace GIMI_ModManager.WinUI.Views.Overlay;

/// <summary>
/// 全屏框选窗口：底图是**按下截图那一刻的整屏画面**，用户在上面拖一个矩形，松手就返回那块区域。
///
/// <para>
/// 为什么先抓整屏再框，而不是做一个半透明窗口盖在游戏上：
/// ① 截出来的图一定不含框选遮罩自己；② 不用和 WinUI 3 的透明窗口 / 分层窗口打交道；
/// ③ 框选期间底图是静止的，不会出现「一边框、游戏一边动」导致框到的画面和看到的画面不一致。
/// </para>
///
/// <para>
/// 最终区域用 <c>GetCursorPos</c> 的屏幕物理坐标算，**不用** XAML 的指针坐标：
/// 窗口横跨多块显示器、各自缩放不同时，DIP 到物理像素不是一条线性关系；而光标位置本来就是
/// 与整屏截图同一个坐标系。
/// </para>
/// </summary>
internal sealed partial class ScreenshotRegionSelectorWindow : WindowEx
{
    private readonly CapturedFrame _frame;
    private readonly ILogger _logger;
    private readonly OverlayWindowStyles _styles;
    private readonly HWND _hwnd;
    private readonly TaskCompletionSource<OverlayCaptureRegion?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _dragging;
    private Point _startPoint;
    private (int X, int Y) _startScreen;

    internal ScreenshotRegionSelectorWindow(CapturedFrame frame, ILogger logger)
    {
        _frame = frame;
        _logger = logger.ForContext<ScreenshotRegionSelectorWindow>();

        InitializeComponent();

        _styles = new OverlayWindowStyles(_logger);
        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

        ConfigureWindow();

        RootGrid.Loaded += OnRootGridLoaded;
        Closed += OnClosed;
    }

    /// <summary>
    /// 显示窗口并等待用户框选。返回 <c>null</c> = 取消（Esc / 右键 / 框得太小）。
    /// **必须在 UI 线程上调用**（要建窗口、动 XAML）。
    /// </summary>
    internal async Task<OverlayCaptureRegion?> PickAsync()
    {
        await LoadBackgroundAsync();

        Activate();
        FocusSink.Focus(FocusState.Programmatic);

        return await _completion.Task;
    }

    private void ConfigureWindow()
    {
        // 位置与尺寸都是物理像素：直接铺满整个虚拟屏幕（含任务栏区域），框选范围不受工作区限制
        AppWindow.MoveAndResize(new RectInt32(_frame.X, _frame.Y, _frame.Width, _frame.Height));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        AppWindow.IsShownInSwitchers = false;

        // 框选时要压在游戏上面；与浮窗同一套置顶写法（托管属性会说谎，以扩展样式为准）
        _styles.SetTopMost(_hwnd, true);
    }

    private async Task LoadBackgroundAsync()
    {
        try
        {
            var buffer = CryptographicBuffer.CreateFromByteArray(_frame.Pixels);

            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8,
                _frame.Width, _frame.Height, BitmapAlphaMode.Ignore);

            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);

            // 两张图共用同一个源：一张在压暗层下面当底，一张在上层按框选区域裁出来提亮
            BackgroundImage.Source = source;
            BrightImage.Source = source;
        }
        catch (Exception e)
        {
            // 底图加载失败时窗口仍可取消；记录原因，别让用户对着黑屏猜
            _logger.Error(e, "[浮窗截图] 框选底图加载失败，改为在黑底上框选");
        }
    }

    private void OnRootGridLoaded(object sender, RoutedEventArgs e)
        => FocusSink.Focus(FocusState.Programmatic);

    // ── 拖框 ──────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootGrid);

        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (!PInvoke.GetCursorPos(out var cursor))
            return;

        _dragging = true;
        _startPoint = point.Position;
        _startScreen = (cursor.X, cursor.Y);

        RootGrid.CapturePointer(e.Pointer);
        SelectionBorder.Visibility = Visibility.Visible;
        UpdateSelection(_startPoint, _startPoint);

        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        var point = e.GetCurrentPoint(RootGrid);

        // 松开事件偶尔会丢（捕获被抢 / 注入的点击）：以按键状态为准收尾，别让框一直挂在那里
        if (!point.Properties.IsLeftButtonPressed)
        {
            FinishSelection(e);
            return;
        }

        UpdateSelection(_startPoint, point.Position);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => FinishSelection(e);

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);
    }

    private void UpdateSelection(Point start, Point end)
    {
        var x = Math.Min(start.X, end.X);
        var y = Math.Min(start.Y, end.Y);
        var width = Math.Abs(end.X - start.X);
        var height = Math.Abs(end.Y - start.Y);

        SelectionBorder.Margin = new Thickness(x, y, 0, 0);
        SelectionBorder.Width = width;
        SelectionBorder.Height = height;

        // 提亮层只露出框里的部分，框外保持压暗
        BrightClip.Rect = new Rect(x, y, width, height);
    }

    private void FinishSelection(PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
        RootGrid.ReleasePointerCapture(e.Pointer);

        if (!PInvoke.GetCursorPos(out var cursor))
        {
            Complete(null);
            return;
        }

        var raw = OverlayCaptureRegion.FromPoints(_startScreen.X, _startScreen.Y, cursor.X, cursor.Y);
        var bounds = new OverlayRect(_frame.X, _frame.Y, _frame.Width, _frame.Height);
        var region = raw.ClampTo(bounds);

        if (region is null)
            _logger.Information("[浮窗截图] 框选区域太小或超出屏幕范围，按取消处理");

        Complete(region);
    }

    // ── 取消 / 收尾 ────────────────────────────────────────

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
            return;

        e.Handled = true;
        Complete(null);
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        Complete(null);
    }

    private void Complete(OverlayCaptureRegion? region)
    {
        // TrySetResult 做幂等：松手那一下与 Closed 可能前后脚到，不能两次都去关窗口
        if (_completion.TrySetResult(region))
            Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 用户点窗口右上角（这里没有标题栏，但 Alt+F4 之类仍可能）或系统关闭时，别让等待方永远挂着
        _completion.TrySetResult(null);
    }
}
