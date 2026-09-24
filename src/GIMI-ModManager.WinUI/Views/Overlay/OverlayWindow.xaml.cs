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
///   2. **置顶位要定期自愈**：实测它在窗口显示之后会被抹掉，抹掉的表现就是
///      "浮窗被别的窗口盖住／看起来消失了"，而热键只切显隐、修不了样式，唤出也白搭。
///      自愈能做的只有"把置顶位补回来"：想用 <c>SetWindowPos</c> 把自己在置顶带里重新排到最前
///      是**做不到的**（实测三种写法都返回成功、位置一动不动，见 <see cref="EnsureTopMost"/> 的说明），
///      所以"到底有没有被压住"只能靠现场日志（<see cref="OverlayStackProbe"/>）判断，不能靠猜。
///   3. **显隐走 <c>ShowWindow(SW_*)</c>**，不走 <c>AppWindow.Show()</c>／<c>Window.Activate()</c> 那一套 ——
///      后者可能顺带激活窗口，一唤出就把游戏的前台挤掉。
///   4. **层级现场定期记进日志**（<see cref="OverlayStackProbe"/>，可见时约 5 秒一次）：浮窗被盖住时用户
///      已经在游戏里，屏幕上的现场没人看得到，只能靠窗口层级关系事后反推 ——
///      而"被游戏压住"与"被合成器绕开"的修法完全不同，日志必须能把两者分开。
/// </summary>
public sealed partial class OverlayWindow : WindowEx
{
    /// <summary>浮窗尺寸。**DIP**（<see cref="WindowEx.Width"/> 的语义），由 WinUIEx 按 DPI 换成物理像素。</summary>
    private const int OverlayWidth = 470;

    private const int OverlayHeight = 440;

    /// <summary>置顶自愈的间隔。1 秒是原型的取值：既够快（用户几乎来不及看到它被盖住），也不至于每秒都去动窗口。</summary>
    private static readonly TimeSpan TopMostCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 层级现场日志的降频基数：自愈 1 秒一次，现场日志每 <c>StackProbeTickInterval</c> 次自愈记一条（即 5 秒）。
    /// 降频是为了让一场几十分钟的游戏只留下几百行日志，而不是几万行。
    /// </summary>
    private const int StackProbeTickInterval = 5;

    private readonly OverlayWindowStyles _styles;
    private readonly ILogger _logger;
    private readonly HWND _hwnd;
    private readonly DispatcherTimer _topMostTimer;

    // 拖动用：按下瞬间的光标**屏幕**坐标与窗口位置。两边都是物理像素、同一个坐标系，因此全程不需要 DPI 换算。
    private PointInt32 _dragOriginCursor;
    private PointInt32 _dragOriginWindow;

    /// <summary>最近一次真正下发给窗口的位置，用来跳过"光标抖了一格但窗口该待在原地"的重复调用。</summary>
    private PointInt32 _lastDragPosition;

    private bool _isDragging;

    /// <summary>是否已经做过"首次显示"的那套收尾（摆位置 + 确认置顶）。见 <see cref="ShowOverlay"/>。</summary>
    private bool _hasShownOnce;

    /// <summary>自愈计时器已经跑过的次数，只用来给层级现场日志降频（见 <see cref="StackProbeTickInterval"/>）。</summary>
    private int _topMostTick;

    /// <summary>浮窗的 ViewModel（internal：它和它手上的协调器都只在本程序集里用）。</summary>
    internal OverlayViewModel ViewModel { get; }

    internal OverlayWindow(OverlayViewModel viewModel, ILogger logger)
    {
        ViewModel = viewModel;
        _logger = logger.ForContext<OverlayWindow>();

        InitializeComponent();

        InitializePageBindings();

        _styles = new OverlayWindowStyles(_logger);
        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

        ConfigureOverlayWindow();

        _topMostTimer = new DispatcherTimer { Interval = TopMostCheckInterval };
        _topMostTimer.Tick += (_, _) =>
        {
            EnsureTopMost("定期自愈");
            ProbeStackIfDue();
        };
        _topMostTimer.Start();

        Closed += OnClosed;
    }

    /// <summary>
    /// 手动把页面级 <c>x:Bind</c> 初始化掉。**这一行不能删 —— 删了浮窗就是个空壳。**
    ///
    /// 生成的绑定代码把"初始化"挂在**本窗口的 <c>Activated</c> 事件**上（<c>GetBindingConnector</c> 里
    /// <c>element1.Activated += bindings.Activated</c>，<c>element1</c> 取的是 XAML 根元素，
    /// 也就是本窗口自己 —— 根是 <c>Window</c> 时编译器就认这个事件）。而本窗口的设计恰恰是**永不激活**
    /// （见类注释：<c>WS_EX_NOACTIVATE</c> + <c>SW_SHOWNOACTIVATE</c>，全程不调 <c>Activate()</c>），
    /// 那个事件就永远不会触发，初始化也就永远不会发生。
    ///
    /// 症状是所有页面级绑定一条都不生效：角色下拉空、Mod 列表空、状态文案空、图标显隐停在默认值 ——
    /// 而静态文字（标题、搜索框占位符）照常显示，看上去像"数据没加载"，其实 ViewModel 里角色 / Mod 一个不少。
    /// 实测定位：下拉的 <c>ItemsSource</c> 压根不是 ViewModel 的那个集合。
    ///
    /// <c>Bindings</c> 是 XAML 编译器生成的私有字段（与本文件同属一个偏类），<c>Initialize()</c> 自带
    /// "初始化过就不再重复"的标志（以及数据源变更订阅），所以这一句与编译器期待的那一次激活等价，重复调用无害。
    /// </summary>
    private void InitializePageBindings() => Bindings?.Initialize();

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

        // 首次显示才做的收尾，刻意放在 ShowWindow **之后**：
        //   1. 构造阶段 AppWindow.Size 不可信（原型实测：那时算出来的"居中"会跑到屏幕左上角）；
        //   2. 本窗口从不调 Activate()（那会把游戏的前台挤掉），所以原型那种"挂在 Activated 事件上等它"
        //      的路子在浮窗里根本不会触发 —— 只能由"第一次显示"这个动作自己把该做的做了。
        if (!_hasShownOnce)
        {
            _hasShownOnce = true;

            RestorePosition();

            // 同样放在显示之后：这时窗口才真的建出来，样式位才是可信的（原型实测构造阶段读到的是中间态）
            EnsureTopMost("首次显示后");

            _logger.Information("浮窗就绪：位置={X},{Y} 尺寸={Width}x{Height}（物理像素）",
                AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        }

        // 唤出这一刻的现场：可见性、最小化、置顶位（回读）、压在浮窗上面的是谁，
        // 以及这一下点击会不会落到我们身上（光标下 / 谁抓着鼠标 / 光标被裁在哪）。
        //
        // 「前台=我」= 浮窗此刻真的占着前台。**它不必然是"点击把游戏的前台抢走了"**：
        // NOACTIVATE 挡的是点击引起的前台转移，挡不住系统在别的前台窗口消失时按 Z 序
        // 把前台交给最上面的窗口 —— 本机冒烟里它就自己出现过（两条连续日志都是「前台=我」）。
        // 游戏在跑时读到它才当作异常（独占全屏下丢了前台可能直接掉出全屏），别见到就归罪于点击。
        _logger.Information("{Probe}", OverlayStackProbe.Describe(_hwnd));
    }

    public void HideOverlay()
    {
        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        _logger.Debug("浮窗隐藏：IsWindowVisible={IsVisible}", PInvoke.IsWindowVisible(_hwnd));
    }

    /// <summary>窗口此刻是否可见。取系统实际值，不是本类的意图值。</summary>
    public bool IsOverlayVisible => PInvoke.IsWindowVisible(_hwnd);

    /// <summary>
    /// 确认浮窗的**置顶位**还在，不在就补回来。
    /// 详见类注释第 1、2 条 —— 这里一律以扩展样式为准做证伪。
    ///
    /// **为什么不做"顺手重申一次、让它排到最前"**：那件事 <c>SetWindowPos</c> 做不到。
    /// 按文档 <c>HWND_TOPMOST</c> 的语义只是"置于所有非置顶窗口之上"（= 成为置顶窗口），
    /// 对已经在置顶带里的窗口没有任何排队作用；而 <c>HWND_TOP</c>、以及"先撤置顶再置顶"
    /// 这两种看起来更狠的写法也一样 —— 2026-09-24 在本机浮窗上逐个实测过（三种写法调用都返回
    /// 成功、扩展样式也如实变化），位置一动不动：浮窗上面那一块是系统的 IME / shell 辅助窗口
    /// （<c>MSCTFIME UI</c> / <c>IME</c> / <c>XamlExplorerHostIslandWindow</c> / <c>ForegroundStaging</c> /
    /// <c>ThumbnailDeviceHelperWnd</c>），应用层本来就抢不过去、也不该去抢。
    /// 所以这里只维护"我还是不是置顶窗口"；"有没有被压住"交给
    /// <see cref="OverlayStackProbe"/> 定期把现场记进日志，由证据说话。
    /// </summary>
    private void EnsureTopMost(string stage)
    {
        var current = OverlayWindowStyles.ReadExStyle(_hwnd);

        if (OverlayWindowStyles.HasTopMost(current))
            return;

        var after = _styles.SetTopMost(_hwnd, onTop: true);

        // 置顶位被抹掉是异常，而且它是"浮窗看起来消失了"的直接原因 —— 记 Warning 级：
        // Debug 级不进日志文件（文件 sink 的门槛是 Information），真出事时反而看不到。
        _logger.Warning("浮窗置顶位在「{Stage}」时缺失，已补回：0x{Before:X16} -> 0x{After:X16}，TOPMOST={HasTopMost}",
            stage, (long)current, (long)after, OverlayWindowStyles.HasTopMost(after));
    }

    /// <summary>
    /// 定期把浮窗此刻的层级现场记进日志（仅可见时，见类注释第 4 条）。
    ///
    /// 「进游戏之后浮窗就没影了」这种事后没法复现的问题，只有这条日志能留下证据。
    /// 读法：游戏窗口**带着置顶位**且排在浮窗前面 → 置顶带里被压（重申置顶就能治，下一拍自愈就会干掉它）；
    /// 浮窗排在游戏前面（或游戏压根没有置顶位）却还是看不见 → 被合成器绕开了（进程内怎么改都没用）。
    /// </summary>
    private void ProbeStackIfDue()
    {
        if (!IsOverlayVisible)
            return;

        if (++_topMostTick % StackProbeTickInterval != 0)
            return;

        _logger.Information("{Probe}", OverlayStackProbe.Describe(_hwnd));
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

        if (!PInvoke.GetCursorPos(out var cursor))
        {
            _logger.Warning("拖动：读光标位置失败，这一次拖动不生效");
            return;
        }

        _dragOriginCursor = new PointInt32(cursor.X, cursor.Y);
        _dragOriginWindow = ReadWindowPosition();
        _lastDragPosition = _dragOriginWindow;
        _isDragging = true;
    }

    private void OnDragStripPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging || !PInvoke.GetCursorPos(out var cursor))
            return;

        // 目标位置一律用「按下时的窗口位置 + 光标从按下到现在的位移」算，**不用**指针在窗口内的坐标累积增量：
        // 窗口跟着指针走，指针在窗口里的坐标就几乎不涨，增量会自我抵消 —— 手感上就是发飘、一顿一顿。
        // 这里两端都是屏幕物理像素，所以原来那步 XamlRoot.RasterizationScale 换算整个不需要了
        // （它还有个隐患：多显示器下两个屏幕缩放不同时，取到的未必是光标所在那块屏的比例）。
        var target = new PointInt32(
            _dragOriginWindow.X + cursor.X - _dragOriginCursor.X,
            _dragOriginWindow.Y + cursor.Y - _dragOriginCursor.Y);

        // 窗口位置本来就是整数物理像素，光标抖动产生的"同位置"重复下发直接跳过
        if (target.X == _lastDragPosition.X && target.Y == _lastDragPosition.Y)
            return;

        // 走 SetWindowPos 而不是 AppWindow.Move：少一层托管记账，拖起来更跟手。
        // SWP_NOZORDER 必带 —— 不带会把置顶位顶掉；SWP_NOACTIVATE 也必带 —— 不带可能顺手把游戏的前台抢走。
        PInvoke.SetWindowPos(
            _hwnd,
            HWND.Null,
            target.X,
            target.Y,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        _lastDragPosition = target;
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

    /// <summary>
    /// 窗口此刻的真实位置（物理像素）。直接问系统，不读 <c>AppWindow.Position</c> ——
    /// 拖动期间窗口是用 <c>SetWindowPos</c> 挪的，绕开了 AppWindow 自己那套记账，回读未必跟得上。
    /// </summary>
    private PointInt32 ReadWindowPosition()
    {
        if (PInvoke.GetWindowRect(_hwnd, out var rect))
            return new PointInt32(rect.left, rect.top);

        _logger.Debug("拖动：读窗口位置失败，回退到 AppWindow.Position");
        return AppWindow.Position;
    }

    private void RememberPosition()
    {
        var position = ReadWindowPosition();

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