using System.ComponentModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using GIMI_ModManager.Core.Entities.Mods.Helpers;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.DragDrop;
using GIMI_ModManager.WinUI.Services.Input;
using GIMI_ModManager.WinUI.Services.Overlay;
using GIMI_ModManager.WinUI.ViewModels.Overlay;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.Foundation;
using WinUIEx;

namespace GIMI_ModManager.WinUI.Views.Overlay;

/// <summary>
/// 游戏内的浮窗：无边框 + 置顶 + **点击不夺前台 + 唤出时主动抢前台**。
///
/// 窗口形态与自愈逻辑来自 Phase 0 原型（<c>src/OverlaySpike</c>），四件事必须照搬，别"优化"掉：
///
///   1. **置顶只认扩展样式**：<c>OverlappedPresenter.IsAlwaysOnTop</c> 与 WinUIEx 的
///      <c>WindowEx.IsAlwaysOnTop</c> 都不写 <c>WS_EX_TOPMOST</c>，而两者都读回 true。
///   2. **置顶位要定期自愈**：实测它在窗口显示之后会被抹掉，抹掉的表现就是
///      "浮窗被别的窗口盖住／看起来消失了"，而热键只切显隐、修不了样式，唤出也白搭。
///      自愈能做的只有"把置顶位补回来"：想用 <c>SetWindowPos</c> 把自己在置顶带里重新排到最前
///      是**做不到的**（实测三种写法都返回成功、位置一动不动，见 <see cref="EnsureTopMost"/> 的说明），
///      所以"到底有没有被压住"只能靠现场日志（<see cref="OverlayStackProbe"/>）判断，不能靠猜。
///   3. **显隐走 <c>ShowWindow(SW_*)</c>**，不走 <c>AppWindow.Show()</c>／<c>Window.Activate()</c> 那一套：
///      前台归属是浮窗最敏感的一件事，"什么时候抢、什么时候还"必须由本类自己说了算（见第 5 条），
///      不能让框架在显隐的顺手动作里替我们决定。
///   4. **层级现场定期记进日志**（<see cref="OverlayStackProbe"/>，可见时约 5 秒一次）：浮窗被盖住时用户
///      已经在游戏里，屏幕上的现场没人看得到，只能靠窗口层级关系事后反推 ——
///      而"被游戏压住"与"被合成器绕开"的修法完全不同，日志必须能把两者分开。
///   5. **唤出时主动抢前台、隐藏时还回去**（<see cref="TakeForeground"/> / <see cref="HandForegroundBack"/>）：
///      "看得见点不到"的正面修法。前台一直在游戏手里时，游戏也持着鼠标捕获
///      （实测 <c>鼠标被=UnrealWindow</c>）—— 鼠标消息先给抓捕获的那个窗口，点浮窗这一下根本轮不到我们，
///      用户得先按住 Alt 逼游戏松开捕获（这就是"Alt + 左键才点得到"的来历）。
///      把前台从游戏手里拿过来，游戏失去激活、自己松开捕获，这一步就省掉了。
///
///      抢前台与 <c>WS_EX_NOACTIVATE</c> **不矛盾**：那一位挡的是"用户点击引起的前台转移"
///      （防独占全屏下点一下浮窗就把游戏踢出全屏），而这里是显式的、成对的一抢一还。
///      抢发生在两处：唤出那一拍（<see cref="TakeForeground"/>），以及可见期间前台被游戏抢回去之后
///      光标正落在浮窗上的那些拍（<see cref="EnsureForeground"/>）—— 后者的做法与取舍写在那一处。
/// </summary>
public sealed partial class OverlayWindow : WindowEx, IExternalDropSurface
{
    /// <summary>
    /// 浮窗尺寸由 <see cref="OverlayViewModel.CurrentSizeOption"/> 的三档预设决定（小/中/大，DIP）。
    /// 内部是「标题条 + 角色下拉 + 搜索 + 模式行 + 列表(*)+ 底栏」，
    /// 只有列表那一行是 <c>*</c>，所以整体变矮只是列表变短，不会把别的挤掉。
    /// </summary>
    private OverlaySizeOption CurrentSize => ViewModel.CurrentSizeOption;

    /// <summary>置顶自愈的间隔。1 秒是原型的取值：既够快（用户几乎来不及看到它被盖住），也不至于每秒都去动窗口。</summary>
    private static readonly TimeSpan TopMostCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 回读前台归属前的等待上限（见 <see cref="WaitForForeground"/>）。
    /// 只为了让日志说真话，窗口自己不等这个：前台切换成功后通常一两拍就回读到。
    /// </summary>
    private const int ForegroundSettleTimeoutMilliseconds = 300;

    /// <summary>等待前台归属稳定下来时的轮询间隔。</summary>
    private const int ForegroundSettlePollMilliseconds = 20;

    /// <summary>
    /// 层级现场日志的降频基数：自愈 1 秒一次，现场日志每 <c>StackProbeTickInterval</c> 次自愈记一条（即 5 秒）。
    /// 降频是为了让一场几十分钟的游戏只留下几百行日志，而不是几万行。
    /// </summary>
    private const int StackProbeTickInterval = 5;

    private readonly OverlayWindowStyles _styles;
    private readonly OverlayScreenshotService _screenshotService;
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

    /// <summary>
    /// 本次拖拽是否已经记过一行诊断（见 <see cref="LogDragProbeOnce"/>）。
    ///
    /// DragOver 随指针移动触发，逐拍记就是日志洪水，所以每轮拖拽只记**第一拍**；
    /// 而"一轮拖拽"的边界取「浮窗唤出」与「松手落下」两处把它重新武装
    /// （<see cref="ShowOverlay"/> / <see cref="RootGrid_OnDrop"/>）。
    ///
    /// 这样「唤出浮窗 → 拖一次」必然在日志里留下一行，于是**一行都没有就等于
    /// 「拖拽事件根本没进到进程」**：JASM 提权时它与 Explorer 的完整性级别不同，
    /// UIPI 会在 OLE 那层把整件事掐掉，浮窗与主窗口会同时只剩禁止光标、松手没反应、且不报任何错。
    /// 用户报「拖不进去」时这一行是日志里唯一能一眼分开「事件没到」与「数据里没有文件」的证据。
    /// </summary>
    private bool _dragProbeLogged;

    /// <summary>
    /// 唤出那一刻的前台窗口（游戏在跑时就是游戏），隐藏时按原样还回去。
    ///
    /// **记的是"谁"而不是"游戏那个进程"**：浮窗这一层对具体游戏没有任何硬编码，
    /// 谁是前台就还给谁，换游戏、在桌面上唤出都自动成立。
    /// </summary>
    private HWND _foregroundBeforeShow = HWND.Null;

    /// <summary>自愈计时器已经跑过的次数，只用来给层级现场日志降频（见 <see cref="StackProbeTickInterval"/>）。</summary>
    private int _topMostTick;

    /// <summary>前台被抢走这件事是否已经记过日志（见 <see cref="EnsureForeground"/>）。</summary>
    private bool _foregroundLossLogged;

    /// <summary>本次"前台被抢走"期间重申了几次，收回后一起记进日志。</summary>
    private int _foregroundRetakeCount;

    /// <summary>本次"前台被抢走"期间是否已经记过"改走注入空输入那条路"（见 <see cref="EnsureForeground"/>）。</summary>
    private bool _foregroundNudged;

    /// <summary>
    /// 「把某一行滚进视野」的代次（见 <see cref="BringItemIntoView"/>）。
    /// 多拍对齐时只有最后一次请求算数，避免早先那名目标的延迟对齐把列表拽回去。
    /// </summary>
    private int _scrollIntoViewEpoch;

    /// <summary>
    /// 隐藏前记下的滚动锚点：哪一行、以及它当时相对可视区上沿的位置。
    ///
    /// 记「行 + 相对位置」而不是单纯的滚动偏移：重开浮窗时列表会整体重建（角色/Mod 行都是新对象），
    /// 而勾选行的按键面板重新展开会改变条目高度 —— 只记偏移的话，重开后会落到别的行上。
    /// </summary>
    private ScrollAnchor? _scrollAnchor;

    /// <summary><see cref="_scrollAnchor"/> 的内容。用 <c>Guid</c> 而不是行对象：行每次重建都是新实例。</summary>
    private readonly record struct ScrollAnchor(Guid ModId, double OffsetFromViewportTop);

    /// <summary>浮窗的 ViewModel（internal：它和它手上的协调器都只在本程序集里用）。</summary>
    internal OverlayViewModel ViewModel { get; }

    internal OverlayWindow(OverlayViewModel viewModel, OverlayScreenshotService screenshotService,
        ILogger logger)
    {
        ViewModel = viewModel;
        _screenshotService = screenshotService;
        _logger = logger.ForContext<OverlayWindow>();

        InitializeComponent();

        InitializePageBindings();

        // 键盘选中换行时把那一行滚进视野：浮窗只有 440 DIP 高，列表比它长，而游戏里用户看不到鼠标，
        // 「选中的那行在不在屏幕里」只能靠这一下滚动告诉他。
        // 挂在 ViewModel 的属性变更上而不是逐个赋值点去调：改选中的入口不止一个
        // （导航热键、过滤之后的重定位），挂在这里它们都自动得到同一件事。
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // 行内按键面板换行时把那一行滚进视野
        ViewModel.KeyPanelTargetChanged += OnKeyPanelTargetChanged;

        // 用户自己滚了列表：还没跑完的延迟对齐就不该再把他拽回去（见 CancelPendingScrollAdjustments）。
        // 挂在 ListView 上而不是 ScrollViewer 上：后者要等模板应用之后才找得到，而滚轮事件会冒泡到这里。
        ModList.PointerWheelChanged += (_, _) => CancelPendingScrollAdjustments();

        _styles = new OverlayWindowStyles(_logger);
        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

        // 勾选触发的刷新（F10）送完键后要把前台交还给我们，协调器得知道交还给哪个窗口
        // （我们自己拿不回来：那一刻的输入所有者是刚注入按键的提权助手）。只有窗口知道自己的 hwnd，
        // 所以在拿到它的这里写一次。
        ViewModel.RefreshCoordinator.OverlayWindowHandle = (nint)_hwnd;

        // 按键面板的徽章点击送完键也要交还前台，理由同上
        ViewModel.OverlayWindowHandle = (nint)_hwnd;

        // 拖放投递通道：关掉 UAC 的机器上 WinUI 收不到外部拖放，由自有落点顶上。
        // **只多挂一个落点** —— 浮窗的显隐、置顶、前台归属走的是下面那些自己写的路径
        // （_styles / _topMostTimer / EnsureForeground），与落点毫无关系，一行都没动。
        // 第四个参数是本窗口自己的落点面：**浮窗必须给** —— Window 不在其内容的视觉父链上，
        // 通道顺着视觉树往上找是找不到本窗口的（实测现象：浮窗一直显示禁止符）。
        App.GetService<ExternalDropChannel>().Attach(_hwnd, "浮窗", () => Content as UIElement, this);

        ConfigureOverlayWindow();

        _topMostTimer = new DispatcherTimer { Interval = TopMostCheckInterval };
        _topMostTimer.Tick += (_, _) =>
        {
            EnsureTopMost("定期自愈");

            // 守着前台，理由见 EnsureForeground：游戏会在浮窗显示期间自己把前台和鼠标捕获抢回去。
            EnsureForeground();

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
        Width = CurrentSize.Width;
        Height = CurrentSize.Height;

        // 内容（角色下拉、Mod 列表）是后来才填满的，填满那一刻窗口会被撑大 —— 见 ReassertSize
        RootGrid.SizeChanged += (_, _) => ReassertSize();

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
    /// 把实际生效的截图热键写进按钮 tooltip。没注册上时写明"请用按钮" ——
    /// tooltip 里写死 Ctrl+Alt+S 会骗用户（首选被占时会退到备选）。
    /// </summary>
    public void SetScreenshotHotkeyHint(string text)
        => ToolTipService.SetToolTip(CaptureLastRegionButton, $"按上次区域截图（{text}）");

    /// <summary>
    /// 把窗口尺寸**再按一遍**那两个常量。
    ///
    /// 为什么需要：实机反馈「浮窗本来很小，用着用着突然变大」。构造时设一次的 <c>Width</c>/<c>Height</c>
    /// 挡不住内容后来把窗口撑开（角色下拉与 Mod 列表是后来才填上的，填上之后内容的 DesiredSize 变大）。
    /// 内容尺寸一变就校一次；只在校不动的时候才动窗口，免得「调窗口 → 内容变 → 再调」转圈。
    /// </summary>
    internal void ReassertSize()
    {
        var size = CurrentSize;

        if (Math.Abs(Width - size.Width) > 0.5)
            Width = size.Width;

        if (Math.Abs(Height - size.Height) > 0.5)
            Height = size.Height;
    }

    /// <summary>
    /// 窗口尺寸变化后把它夹回工作区：浮窗没有标题栏，下半截被摆到屏幕外就再也点不到了。
    /// 只在可见时做 —— 隐藏时等下一次唤出，<see cref="RestorePosition"/> 会再夹一次。
    /// </summary>
    private void KeepOnScreen()
    {
        if (!IsOverlayVisible)
            return;

        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        var position = AppWindow.Position;

        var (x, y) = OverlayPlacement.ResolveTopLeft(
            (position.X, position.Y),
            new OverlayRect(workArea.X, workArea.Y, workArea.Width, workArea.Height),
            size.Width, size.Height);

        if (x != position.X || y != position.Y)
            AppWindow.Move(new PointInt32(x, y));
    }

    /// <summary>
    /// 键盘把选中行挪走时，把新选中的那一行滚进视野。
    ///
    /// 只认 <see cref="OverlayViewModel.SelectedMod"/> 这一个属性名，别的一律早返回 ——
    /// 这个 ViewModel 上属性变更很频繁（刷新状态、搜索结果、待刷新提示），
    /// 每条都去碰一次 ListView 属于白花钱。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 拖拽安装的进度行刚露面：放一遍进场动画。
        //
        // 为什么要在代码里开这一枪而不是绑在 XAML 上：绑定只能把行「显出来」（Visibility），
        // 而淡入 + 上滑是一次性动画、没有对应的属性可绑 —— Storyboard 得有人在状态翻起来那一刻启动。
        if (e.PropertyName == nameof(OverlayViewModel.ShowInstallStatus))
        {
            if (ViewModel.ShowInstallStatus)
                InstallStatusInStoryboard.Begin();

            return;
        }

        // 标题栏换了大小档位：按新档位设尺寸，再把窗口夹回工作区
        if (e.PropertyName == nameof(OverlayViewModel.SelectedSizeIndex))
        {
            ReassertSize();
            KeepOnScreen();
            return;
        }

        if (e.PropertyName != nameof(OverlayViewModel.SelectedMod))
            return;

        // 键盘逐行走：只做最小滚动（行已在可视区里就不动），别每按一下都把整屏跳一遍
        if (ViewModel.SelectedMod is { } item)
            BringItemIntoView(item, alignToTop: false);
    }

    /// <summary>
    /// 显示浮窗，**不激活**。隐藏状态下热键照样有效（<c>WM_HOTKEY</c> 仍会派发到隐藏窗口的消息队列），
    /// 所以隐藏不是"关掉"，只是看不见。
    /// </summary>
    public void ShowOverlay()
    {
        EnsureTopMost("唤出时");

        // 每次唤出都把尺寸按回常量：内容填满那一下会把窗口撑大（见 ReassertSize）
        ReassertSize();

        // 拖拽诊断重新武装：唤出之后拖一次就必然会留下一行（见 _dragProbeLogged）。
        // 不重新武装的话，上一次拖拽留下的"已记过"会让这一轮的失败在日志里彻底静音。
        _dragProbeLogged = false;

        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

        // 把列表滚回隐藏前的落点（用户要求：重开浮窗不跳动）。排进队列、不能当场做，理由见该方法。
        ScheduleScrollAnchorRestore();

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

        // 抢前台放在探针**之前**：探针那行里的「前台=我 / 别人」正好就是这次抢没抢到的直接读数。
        TakeForeground();

        // 唤出这一刻的现场：可见性、最小化、置顶位（回读）、压在浮窗上面的是谁，
        // 以及这一下点击会不会落到我们身上（光标下 / 谁抓着鼠标 / 光标被裁在哪）。
        //
        // 读法：**「前台=我」是这次唤出的预期结果**（见 TakeForeground）——读到「别人」就说明前台锁
        // 没放行（热键那一下没被系统当成"JASM 收到的输入"），这一轮用户仍然只能 Alt + 左键。
        // 抢到了却还是点不动，才轮到后面几项：谁抓着鼠标、光标被裁在哪、压在上面的是谁。
        _logger.Information("{Probe}", OverlayStackProbe.Describe(_hwnd));
    }

    /// <summary>
    /// 藏起浮窗，并把前台还给唤出的那一刻（见 <see cref="HandForegroundBack"/>）。
    /// </summary>
    public void HideOverlay()
    {
        // 记下此刻的滚动落点，重开时再放回去（见 ScheduleScrollAnchorRestore）。
        // 必须在 SW_HIDE **之前**读：藏起来之后行容器可能被回收，位置就读不到了。
        CaptureScrollAnchor();

        // "该不该还前台"必须在 SW_HIDE **之前**判定：一藏起来系统立刻就把前台分配给别的窗口，
        // 之后再读"前台是不是我"永远是假 —— 这段还前台的逻辑会整个静默失效。
        var foregroundWasOurs = !_foregroundBeforeShow.IsNull && OverlayWindowStyles.IsOwnWindowForeground(_hwnd);
        var previous = _foregroundBeforeShow;
        _foregroundBeforeShow = HWND.Null;

        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_HIDE);

        if (foregroundWasOurs)
            HandForegroundBack(previous);

        _logger.Debug("浮窗隐藏：IsWindowVisible={IsVisible}", PInvoke.IsWindowVisible(_hwnd));
    }

    // ── 前台归属（唤出抢 / 隐藏还）────────────────────────────

    /// <summary>
    /// 把前台从游戏手里拿过来（唤出时）。**这是浮窗唯一一处主动改变前台归属的地方。**
    ///
    /// 为什么必须抢：前台是游戏的时候，鼠标捕获也在游戏手里（实测 <c>鼠标被=UnrealWindow</c>），
    /// 而鼠标消息先给抓着捕获的那个窗口 —— 点浮窗这一下根本轮不到我们。用户得先按住 Alt，
    /// 让系统给游戏发 WM_CANCELMODE、逼它松开捕获，才点得动浮窗上的 Mod
    /// （用户报的"得用 Alt + 左键"就是这么来的）。把前台拿过来之后游戏失去激活，
    /// 绝大多数游戏会自己松开捕获，这一步就省掉了。
    ///
    /// **抢不抢得到由系统说了算**（前台锁只认"谁最近收到过输入"），判据也不是
    /// <c>SetForegroundWindow</c> 的返回值 —— 它被前台锁挡下时返回 0、而窗口其实已经切过去的情况存在
    /// （见 <c>ForegroundWindowActivator</c> 的说明）。所以这里一律回读 <c>GetForegroundWindow</c>
    /// 并把结果写进日志：抢没抢到，日志里说真话，不靠猜。
    /// </summary>
    private void TakeForeground()
    {
        var previous = PInvoke.GetForegroundWindow();

        // 前台本来就是我们（比如加载界面那会儿系统按 Z 序把它交给了浮窗）时不覆盖：
        // 那时"唤出前的前台"已经没有意义了，留着更早记下的那个（游戏），隐藏时才还得到正确的地方。
        if (previous != _hwnd)
            _foregroundBeforeShow = previous;

        ForegroundWindowActivator.Activate(_hwnd, handOverRightToSetForeground: false, _logger);

        _logger.Information("浮窗唤出：夺取前台={Result}；唤出前的前台={Previous}",
            WaitForForeground(_hwnd)
                ? "成功"
                : "失败（前台仍在别人手里，这一轮还得 Alt + 左键）",
            WindowProcessQuery.DescribeWindow(previous));
    }

    /// <summary>
    /// 守着前台：可见期间每一拍自愈都确认前台还在不在自己手里，不在就重申一次。
    ///
    /// **为什么抢一次不够**：2026-09-24 实机（游戏 pid 29804 正在跑）读到这样一串现场 ——
    /// <c>18:19:42 前台=我 鼠标被=无</c> → <c>18:19:48 前台=别人 鼠标被=0x6805E8 UnrealWindow[前台]</c>，
    /// 而**光标全程都停在浮窗上**：没有人点游戏，是游戏自己把前台连同鼠标捕获抢回去的
    /// （这类游戏会注册后台 raw input 或按帧重申锁）。前台一丢，鼠标消息就全归游戏，
    /// 光标被它按帧拽回原位 —— 用户的原话是「鼠标老是被拉扯到浮窗的同一个位置」。
    /// 所以浮窗只要还看得见就由它持前台，藏起来时才还（见 <see cref="HandForegroundBack"/>）。
    ///
    /// **为什么"重申"要分两种做法**：前台是被游戏抢走的时候，本进程的输入身份已经过期，
    /// 一句 <c>SetForegroundWindow</c> 会被前台锁拒掉、而且再调多少次都拒（本机实测连撞 16 拍）。
    /// 要拿回来必须先**自己注入一次输入**把身份要回来 —— 这一步在
    /// <see cref="ForegroundWindowActivator.Activate"/> 的 <c>nudgeInputForForegroundLock</c> 上，
    /// 那里记了完整的实测过程。
    ///
    /// **什么时候才做那一步**：只在**光标正压在浮窗上**时。那一刻用户的意图没有歧义（他正在点浮窗），
    /// 而其余时间（光标在游戏里 = 他在玩）不能去抢 —— 抢一次就是让游戏丢一次激活，
    /// 抢来抢去会变成卡顿和闪屏，那不是用户要的。
    ///
    /// **代价是有意接受的**：光标落在浮窗上期间等于"输入归浮窗"（Steam 浮层就是这个契约）。
    /// 副作用是游戏会反复丢掉 / 拿回前台，可能伴随卡顿或画面闪一下 —— 若实机测下来比"抢不到"还难受，
    /// 该退的是这条重申策略：把计时器里对 <see cref="EnsureForeground"/> 的调用去掉，
    /// 就回到"只在唤出那一拍抢一次"。
    ///
    /// 日志按**状态变化**记、不按拍数记：游戏若每秒抢一次，按拍记就是一秒一行的日志洪水。
    /// </summary>
    private void EnsureForeground()
    {
        if (!IsOverlayVisible)
            return;

        // 刷新在飞：这段时间前台必须**留在游戏上**，浮窗不许插手。
        // 送键链路刚在同步段把游戏切到前台，而提权助手 / 主程序都要回读到「前台是游戏进程」
        // 才肯发 F10（否则回执 not-foreground、这一发**根本不会发出去**）。本类每秒一拍的自愈
        // 若在中间把前台抢回浮窗，就是「勾了、界面显示在刷、游戏里没换」。
        // 切换到别的程序再回来之后尤其容易撞上：那时前台本来就不在浮窗上，
        // 光标一压到浮窗就每拍都在重申（还带空鼠标事件解锁），正好横在送礼这一段里。
        // 刷新结束后前台本就会被交还回来（见 RequestRefreshAsync 的 handback），跳过这几拍不会丢。
        if (ViewModel.RefreshCoordinator.IsRefreshing)
            return;

        if (OverlayWindowStyles.IsOwnWindowForeground(_hwnd))
        {
            if (_foregroundLossLogged)
            {
                _logger.Information("浮窗前台已收回（共重申 {Count} 次）", _foregroundRetakeCount);
                _foregroundLossLogged = false;
                _foregroundRetakeCount = 0;
                _foregroundNudged = false;
            }

            return;
        }

        var cursorOnOverlay = OverlayStackProbe.IsCursorOverOwnProcessWindow(_hwnd);

        if (!_foregroundLossLogged)
        {
            _logger.Warning("浮窗可见但前台被抢走了（当前前台={Foreground}，光标在浮窗上={OnOverlay}），此后光标压在浮窗上时每拍重申一次",
                WindowProcessQuery.DescribeWindow(PInvoke.GetForegroundWindow()), cursorOnOverlay);
            _foregroundLossLogged = true;
        }

        // 光标不在浮窗上 = 用户人在游戏里（或切去了别的程序）。这时前台就该留在别人手上，
        // 去抢等于每隔一秒把用户从游戏里踢一下 —— 也正是上面那段取舍说明里的「不能抢」。
        // 这里只记账、不动手；用户把光标移回来，下一拍就会走下面的重申。
        if (!cursorOnOverlay)
            return;

        _foregroundRetakeCount++;

        // 这一次记一条（按状态变化记，不按拍数记）：它同时是"用户正伸手点浮窗"的凭据，
        // 也是"为什么这次能抢回来"的答案 —— 没这一行的话，日志里两种重申看起来一模一样。
        if (!_foregroundNudged)
        {
            _logger.Information("光标在浮窗上，改走「注入空鼠标事件解锁前台锁」那条路重申前台");
            _foregroundNudged = true;
        }

        // 这里**不等**回读：每秒都在跑的路径上阻塞 UI 线程 300ms 不可接受，
        // 而这次重申成没成下一拍就知道（前台是我们的就记"已收回"）。
        // 注入空鼠标事件这一步必须带：前台是被别人拿走的时，本进程的输入身份已经过期，
        // 不带它的话 SetForegroundWindow 会被前台锁一直拒（实测连撞 16 拍）。
        ForegroundWindowActivator.Activate(_hwnd, handOverRightToSetForeground: false, _logger,
            nudgeInputForForegroundLock: true);
    }

    /// <summary>
    /// 把前台还给唤出那一刻的前台窗口（通常是游戏）。
    ///
    /// 只在**隐藏前一刻前台还是浮窗**时才还：浮窗显示期间用户可能已经点过别的窗口、前台早就易主了，
    /// 那时候去 <c>SetForegroundWindow</c> 等于从别人手里硬抢，不是浮窗该做的事。
    ///
    /// 还失败也不影响显隐本身（顶多是用户得自己点一下游戏才回得去），所以只记结果、不重试、不报错。
    /// </summary>
    private void HandForegroundBack(HWND previous)
    {
        ForegroundWindowActivator.Activate(previous, handOverRightToSetForeground: false, _logger);

        // 必须等回读，不能调完立刻读：前台切换是**异步**的（SetForegroundWindow 只是把请求交给目标线程）。
        // 2026-09-24 本机实测踩到过：立刻回读读到的是还没切走的浮窗，把已经成功的还原记成了「没还成」，
        // 而同一拍用外部脚本读到的前台**已经是游戏**了 —— 日志自己在骗人。
        _logger.Information("浮窗隐藏：前台还给 {Previous}，结果={Result}",
            WindowProcessQuery.DescribeWindow(previous),
            WaitForForeground(previous)
                ? "成功"
                : "没还成（前台落在别的窗口上，用户可能需要点一下游戏）");
    }

    /// <summary>
    /// 短等前台窗口变成 <paramref name="expected"/>，等到返回 <c>true</c>。
    ///
    /// 存在的理由只有一个：**让日志说真话**。抢 / 还前台都是异步生效的，马上回读会把成功记成失败
    /// （本机实测过，见 <see cref="HandForegroundBack"/>）。等不到也不改变行为 —— 调用方只拿它写日志。
    ///
    /// 会阻塞 UI 线程最多 <see cref="ForegroundSettleTimeoutMilliseconds"/> 毫秒，但只在"没等到"时才会走满，
    /// 而这两个调用点都在显隐的那一拍（窗口此刻是刚显示 / 刚隐藏），这一小段等待不落在任何交互上。
    /// </summary>
    private static bool WaitForForeground(HWND expected)
    {
        var deadline = Environment.TickCount64 + ForegroundSettleTimeoutMilliseconds;

        while (PInvoke.GetForegroundWindow() != expected && Environment.TickCount64 < deadline)
            Thread.Sleep(ForegroundSettlePollMilliseconds);

        return PInvoke.GetForegroundWindow() == expected;
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

    // ── 截图（框选 / 复用上次区域）────────────────────────────────

    private void OnCaptureRegionClick(object sender, RoutedEventArgs e)
        => _ = CaptureScreenshotAsync(useSavedRegion: false);

    private void OnCaptureLastRegionClick(object sender, RoutedEventArgs e)
        => _ = CaptureScreenshotAsync(useSavedRegion: true);

    /// <summary>
    /// 截图热键的入口。**浮窗隐藏时也会走到这里**：全局热键注册在窗口上，与显隐无关。
    /// </summary>
    internal Task CaptureLastRegionAsync() => CaptureScreenshotAsync(useSavedRegion: true);

    /// <summary>
    /// 截图主流程：先确定唯一的 Mod 目标，再决定「复用记忆区域」还是「抓整屏后框选」。
    ///
    /// <para>
    /// 浮窗一定先藏起来：它压在游戏画面上，不藏就会被拍进截图（而且遮罩窗口也不能出现在自己的底图里）。
    /// 拍完按**截图前的显隐**恢复 —— 用户是按热键截的，截完不该平白冒出一个浮窗。
    /// </para>
    ///
    /// <para>
    /// 框选窗口会拿走前台；无论成败都要把它还回截图前的那个窗口（通常是游戏）。
    /// </para>
    /// </summary>
    internal async Task CaptureScreenshotAsync(bool useSavedRegion)
    {
        if (ViewModel.IsCapturing)
            return;

        // 记在目标判定之前：热键截图的常态是浮窗藏着，失败反馈要走系统通知
        var wasVisible = IsOverlayVisible;

        var target = ViewModel.GetUniqueCheckedMod();

        if (target is null)
        {
            ViewModel.ReportScreenshotProblem("请先勾选，且只勾选一件 Mod，再截图。",
                notifyViaSystem: !wasVisible);
            return;
        }

        ViewModel.IsCapturing = true;

        if (wasVisible)
            HideOverlay();

        var foregroundBefore = PInvoke.GetForegroundWindow();

        try
        {
            var bounds = OverlayScreenshotService.GetVirtualScreenBounds();

            // 「上次区域」先夹回当前虚拟屏幕；换过分辨率 / 显示器之后旧坐标可能已经失效
            var saved = useSavedRegion
                ? ViewModel.Settings.SavedCaptureRegion?.ClampTo(bounds)
                : null;

            if (saved is { } region)
            {
                var frame = await Task.Run(() => _screenshotService.Capture(region));
                await ViewModel.SaveScreenshotAsync(frame, region, target, notifyViaSystem: !wasVisible);
                return;
            }

            // 没有记忆区域（或已经失效）：抓整屏，让用户在上面框
            var fullRegion = new OverlayCaptureRegion(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            var fullScreen = await Task.Run(() => _screenshotService.Capture(fullRegion));
            var selector = new ScreenshotRegionSelectorWindow(fullScreen, _logger);
            var chosen = await selector.PickAsync();

            if (chosen is null)
            {
                ViewModel.ReportScreenshotProblem("已取消截图。", notifyViaSystem: !wasVisible);
                return;
            }

            var cropped = _screenshotService.Crop(fullScreen, chosen.Value);
            await ViewModel.SaveScreenshotAsync(cropped, chosen.Value, target, notifyViaSystem: !wasVisible);
        }
        catch (Exception e)
        {
            _logger.Error(e, "[浮窗截图] 截图失败");
            ViewModel.ReportScreenshotProblem($"截图失败：{e.Message}", notifyViaSystem: !wasVisible);
        }
        finally
        {
            // 框选窗口在时前台是它；不在时这句是幂等的。先还前台，再决定浮窗回不回来
            ForegroundWindowActivator.Activate(foregroundBefore, handOverRightToSetForeground: false, _logger);

            if (wasVisible)
                ShowOverlay();

            ViewModel.IsCapturing = false;
        }
    }

    // ── 按键面板（挂在勾选行下面）────────────────────────────────

    /// <summary>
    /// 面板目标换行：把那一行滚进视野。展开的面板会把行撑高，
    /// 不滚的话它可能整块落在列表可视区下面，看起来像"勾了但没出面板"。
    /// </summary>
    private void OnKeyPanelTargetChanged(OverlayModItemViewModel item)
        => App.MainWindow.DispatcherQueue.TryEnqueue(() => BringItemIntoView(item));

    /// <summary>
    /// 把一行滚进视野。分几拍：行内按键面板是**先出现、后撑高**的，按键绑定还会再解析回来长一截，
    /// 每一拍都可能把已经摆好的落点推歪，所以面板稳定之后再各补一次。
    /// </summary>
    /// <param name="alignToTop">
    /// <c>true</c> = 行顶贴列表上沿：按键面板换了主人时用（落点必须是行，面板要跟着露出来）。
    /// <c>false</c> = 最小滚动：键盘逐行移动时用，行已经在可视区里就完全不动。
    /// </param>
    private void BringItemIntoView(OverlayModItemViewModel item, bool alignToTop = true)
    {
        // 连着按上下键时，每一行都会排上三拍；只认最后那一次 ——
        // 否则前一行的延迟对齐会在 150ms 后把列表拽回去。
        var epoch = ++_scrollIntoViewEpoch;

        ScrollItemIntoView(item, alignToTop);

        // 第二拍：面板刚展开，等这一步布局跑完
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (epoch == _scrollIntoViewEpoch)
                ScrollItemIntoView(item, alignToTop);
        });

        // 第三拍：按键绑定解析回来后面板内容还会再长一截，稍等一拍再对齐
        App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(PanelLayoutSettleMilliseconds);

            // 不用 ConfigureAwait(false)：续体要回 UI 线程才能动列表。
            // 行对象可能已经整批换过（换角色 / 重开浮窗），或者用户已经选中了别的行 —— 那几拍都不作数。
            if (epoch != _scrollIntoViewEpoch || !ViewModel.Mods.Contains(item))
                return;

            ScrollItemIntoView(item, alignToTop);
        });
    }

    /// <summary>面板内容稳定大约要这么久（见 <see cref="BringItemIntoView"/> 的第三拍）。</summary>
    private const int PanelLayoutSettleMilliseconds = 150;

    /// <summary>
    /// 把某一行滚进视野。
    ///
    /// **量的是「行」不是容器**：容器是「行 + 按键面板」一整个 item，面板展开后它比可视区还高，
    /// 按容器算落点会停在面板上（用户反馈：重开浮窗后看到的是一屏按键映射，而不是勾的那件 Mod）。
    /// 行元素从模板里按 <c>Tag</c> 找（模板最外层那层 Grid 挂的就是行 VM）。
    /// </summary>
    private void ScrollItemIntoView(OverlayModItemViewModel item, bool alignToTop)
    {
        if (ModList.ContainerFromItem(item) is not FrameworkElement container)
        {
            // 容器还没被虚拟化出来（列表刚重建）：先交给 ListView 按估算高度滚一次，
            // 后面那几拍等容器实现出来之后，这里的精确对齐会接手。
            ModList.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
            return;
        }

        try
        {
            if (FindScrollViewer(ModList) is not { } scrollViewer)
                return;

            var row = FindRowElement(container, item) ?? container;
            var top = row.TransformToVisual(scrollViewer).TransformPoint(new Point(0, 0)).Y;

            if (alignToTop)
            {
                // 已经贴顶就不动；否则把行顶对齐到上沿（面板照旧在下面展开）
                if (Math.Abs(top) < 0.5)
                    return;

                scrollViewer.ChangeView(null, scrollViewer.VerticalOffset + top, null, disableAnimation: true);
                return;
            }

            var height = row.ActualHeight;
            var viewportHeight = scrollViewer.ViewportHeight;

            // 整行都在可视区里就不动 —— 键盘逐行移动时画面保持稳定
            if (top >= -0.5 && top + height <= viewportHeight + 0.5)
                return;

            // 最小滚动：只挪露不出来的那一截（上沿露不出就贴顶，下沿露不出就贴底）
            var delta = top < -0.5 ? top : top + height - viewportHeight;

            scrollViewer.ChangeView(null, scrollViewer.VerticalOffset + delta, null, disableAnimation: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // 容器正在被回收 / 不在同一棵可视树上：这一次跳过，下一拍再来
            _logger.Debug(e, "把行滚进视野失败");
        }
    }

    /// <summary>
    /// 在行容器里找模板最外层那层「行」元素（模板最外层的 StackPanel → 第一个挂
    /// <c>Tag="{x:Bind}"</c> 的 Grid）。深度优先，所以先命中的是行本身，
    /// 不会拿到行尾那个同样挂了 Tag 的「下一个」按钮。
    /// </summary>
    private static FrameworkElement? FindRowElement(DependencyObject root, OverlayModItemViewModel item)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);

            if (child is FrameworkElement element && ReferenceEquals(element.Tag, item))
                return element;

            if (FindRowElement(child, item) is { } found)
                return found;
        }

        return null;
    }

    // ── 留在隐藏前的滚动落点（重开不跳）─────────────────────────

    /// <summary>
    /// 记下此刻列表最上面那一行。**必须在 SW_HIDE 之前调**：藏起来之后行容器可能被回收。
    /// 读不到可用行（列表空 / 正在重建）时置 <c>null</c>，重开时按默认落点处理。
    /// </summary>
    private void CaptureScrollAnchor()
    {
        _scrollAnchor = null;

        if (FindScrollViewer(ModList) is not { } scrollViewer)
            return;

        try
        {
            foreach (var item in ViewModel.Mods)
            {
                if (ModList.ContainerFromItem(item) is not FrameworkElement container)
                    continue;

                var top = container.TransformToVisual(scrollViewer).TransformPoint(new Point(0, 0)).Y;

                // 整个在可视区上方的跳过；第一个「下沿还在可视区里」的就是锚点
                if (top + container.ActualHeight <= 0)
                    continue;

                _scrollAnchor = new ScrollAnchor(item.Id, top);
                return;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _logger.Debug(e, "记录列表滚动位置失败");
        }
    }

    /// <summary>
    /// 重开浮窗后把列表滚回隐藏前的落点（产品决定：重开不跳动，所有 Mod 位置保持原样）。
    ///
    /// **排进 DispatcherQueue、不当场做**：唤出流程里的 <c>RefreshCharacters</c> 已经排了一条
    /// 「滚到勾选行」（按键面板换了目标），要等它先跑完再由这里接管；接管时代次 +1，
    /// 把它后面的几拍对齐一并作废，免得两边互相拽。
    /// </summary>
    private void ScheduleScrollAnchorRestore()
    {
        if (_scrollAnchor is null)
            return;

        App.MainWindow.DispatcherQueue.TryEnqueue(BeginScrollAnchorRestore);
    }

    /// <summary>
    /// 锚点补拍的延时（毫秒）：面板刚撑高、按键绑定解析回来（可能到 1 秒）各要一次。
    /// 用户在这期间自己滚了会被 <see cref="CancelPendingScrollAdjustments"/> 作废。
    /// </summary>
    private static readonly int[] ScrollAnchorRestoreDelays = [16, 150, 500, 1000];

    private void BeginScrollAnchorRestore()
    {
        var epoch = ++_scrollIntoViewEpoch;

        RestoreScrollAnchor();

        foreach (var delay in ScrollAnchorRestoreDelays)
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(delay);

                // 不用 ConfigureAwait(false)：续体要回 UI 线程才能动列表
                if (epoch == _scrollIntoViewEpoch)
                    RestoreScrollAnchor();
            });
        }
    }

    private void RestoreScrollAnchor()
    {
        if (_scrollAnchor is not { } anchor)
            return;

        var item = ViewModel.Mods.FirstOrDefault(mod => mod.Id == anchor.ModId);

        if (item is null)
        {
            // 那一件没了（删了 / 换了角色）：退回默认 —— 把当前选中行露出来
            _scrollAnchor = null;

            if (ViewModel.SelectedMod is { } selected)
                BringItemIntoView(selected, alignToTop: false);

            return;
        }

        if (ModList.ContainerFromItem(item) is not FrameworkElement container)
        {
            // 还没被虚拟化出来：先按估算高度滚过去，下一拍再精确对齐
            ModList.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
            return;
        }

        try
        {
            if (FindScrollViewer(ModList) is not { } scrollViewer)
                return;

            var top = container.TransformToVisual(scrollViewer).TransformPoint(new Point(0, 0)).Y;
            var delta = top - anchor.OffsetFromViewportTop;

            if (Math.Abs(delta) < 0.5)
                return;

            scrollViewer.ChangeView(null, scrollViewer.VerticalOffset + delta, null, disableAnimation: true);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _logger.Debug(e, "恢复列表滚动位置失败");
        }
    }

    /// <summary>
    /// 用户自己滚了：把还没跑完的延迟对齐作废，否则 150 / 500ms 后那几拍会把他刚滚到的位置拽回去。
    /// 记下的锚点也一并丢掉 —— 隐藏时会按新位置重新记。
    /// </summary>
    private void CancelPendingScrollAdjustments()
    {
        _scrollIntoViewEpoch++;
        _scrollAnchor = null;
    }

    /// <summary>在可视树里找 ListView 内部那个 ScrollViewer（ListView 不公开它）。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer scrollViewer)
            return scrollViewer;

        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var index = 0; index < count; index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>
    /// 点击面板里的按键徽章 → 把这一键发给游戏。
    /// 条目从 <c>Tag</c> 取（模板里 <c>Tag="{x:Bind}"</c>），不依赖 DataContext。
    /// </summary>
    private async void OnKeyBadgeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ModIniKeyBindingEntry entry)
            return;

        await ViewModel.SendKeyBindingAsync(entry);
    }

    // ── 右键一行 = 打 NSFW 标记（一件件过 Mod 时的审阅记号）──────────────

    /// <summary>
    /// 右键一行：给这一件打上 / 取消 NSFW 标记（实现在 <see cref="OverlayViewModel.ToggleNsfwMarkAsync"/>，
    /// 只动名单、不动磁盘上的 Mod）。
    ///
    /// **为什么是右键而不是再占一个热键**：标记与"换到这一件"本来就是同一行上的前后两拍
    /// （左键勾上看效果 → 右键记一笔），右键既不跟勾选抢同一个手势，也不用再往这扇窗上挤一个全局热键。
    /// 事件打在行根 Grid 上，所以勾选框、缩略图、名字上右键都算在这一行上。
    /// </summary>
    /// <summary>
    /// 鼠标中键一行：标记 / 取消「已看过」（实现在 <see cref="OverlayViewModel.ToggleSeenMarkAsync"/>）。
    ///
    /// 与右键 NSFW 分开：右键判内容、中键记进度，两件事互相独立。
    /// 中键在 WinUI 里没有默认行为（不会触发选中或滚动），标 Handled 免得它继续往上传。
    /// </summary>
    /// <summary>
    /// 行尾「下一个」按钮：勾上列表里的下一件。
    /// 实际切换走 <see cref="OverlayViewModel.SelectNextModAsync"/>，与手动点勾选框同一条路。
    /// </summary>
    private async void OnNextModClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is OverlayModItemViewModel item)
            await ViewModel.SelectNextModAsync(item);
    }

    private async void OnModRowPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint((UIElement)sender);

        if (!point.Properties.IsMiddleButtonPressed)
            return;

        e.Handled = true;

        if ((sender as FrameworkElement)?.Tag is OverlayModItemViewModel item)
            await ViewModel.ToggleSeenMarkAsync(item);
    }

    private async void OnModRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not OverlayModItemViewModel item)
            return;

        // 不让它往上冒：列表控件收到右键会走它自己那套（取消选中之类），而行的高亮是浮窗自己
        // 维护的（SelectionMode="None"、ListViewItem 压根没有选中态），别让两套语义打架。
        e.Handled = true;

        await ViewModel.ToggleNsfwMarkAsync(item);
    }

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
        if (!_isDragging)
            return;

        // 左键已经松开却还挂着 _isDragging：说明 PointerReleased / PointerCaptureLost 有一个没送到
        //（注入的点击，或拖动途中窗口被抢走捕获，都可能造成）。这时再拖下去窗口会"焊"在光标上 ——
        // 窗口追着光标跑，光标就永远停在窗口里的同一处，表现正是"鼠标动不了、老被拉扯"。
        // 所以收尾一律以**按键状态**为准，不假设那两个事件一定会来。
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
        {
            OnDragStripPointerReleased(sender, e);
            return;
        }

        if (!PInvoke.GetCursorPos(out var cursor))
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

        // 摘掉订阅：ViewModel 由 Host 持有、比这扇窗活得久，留着订阅等于让窗口被它拖住不放
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.KeyPanelTargetChanged -= OnKeyPanelTargetChanged;

        RememberPosition();
        _logger.Debug("浮窗已关闭");
    }

    // ── 把包拖到浮窗上直接装（装给当前选中的角色） ──────────────────────────

    /// <summary>
    /// 拖着包经过浮窗时表态接不接。
    ///
    /// <b>有文件就接</b>，不看选没选中角色：装给谁要等落下、解压之后才认（见
    /// <see cref="OverlayViewModel.DropModPackageAsync"/>）。这里不能拿「有没有选中角色」去拒绝 ——
    /// 浮窗的角色列表只列**已经有 Mod 的角色**，一个 Mod 都没有时它是空的，
    /// 而「拖第一个 Mod 进来」恰恰就是那个场景（实机症状：光标显示禁用、怎么拖都装不了）。
    /// </summary>
    private void RootGrid_OnDragOver(object sender, DragEventArgs e)
    {
        var hasItems = e.DataView.Contains(StandardDataFormats.StorageItems);

        LogDragProbeOnce(hasItems, e);

        if (!hasItems)
        {
            // 光有禁止光标是不够的：浮窗是游戏内的浮层，用户没有别的地方能被告知「为什么拖不进来」。
            // 这一句写进已有那条状态行（XAML 里绑 ViewModel.ErrorMessage）；同一条文案重复赋值是幂等的，
            // DragOver 每次指针移动都来一发也不会把界面刷花（ObservableProperty 自己比过值）。
            ViewModel.ErrorMessage = OverlayViewModel.NonFileDropMessage;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;

        // 光标旁就写一句「自动识别角色」：装给谁要等落下后认包才知道，这里给具体角色名反而是骗人的
        if (e.DragUIOverride is { } hint)
        {
            hint.IsCaptionVisible = true;
            hint.Caption = "自动识别角色";
        }

        e.Handled = true;
    }

    /// <summary>
    /// 每轮拖拽记一行：**事件到底有没有进到进程、进来时数据对象里装的是什么**。
    ///
    /// 「进没进来」与「进来但没有文件」是浮窗拖不进去的两条完全不同的来路，光标却都是禁止图标：
    /// 前者要查完整性级别（提权/UIPI，见 <see cref="_dragProbeLogged"/>），
    /// 后者要查拖拽来源（网盘占位文件 / 压缩软件的虚拟文件 / 只拖了个链接 —— 都只给格式清单不给真实文件）。
    /// 所以拒绝那一路把 <c>AvailableFormats</c> 一并记下来：那时它是唯一能说明"它到底给的是什么"的线索。
    ///
    /// 含文件这一路**不读**格式清单：那是个可能阻塞的同步调用（来源进程无响应时会卡住拖拽），
    /// 不该落在每次拖拽的必经之路上。
    /// </summary>
    private void LogDragProbeOnce(bool hasItems, DragEventArgs e)
    {
        if (_dragProbeLogged)
            return;

        _dragProbeLogged = true;

        if (hasItems)
        {
            _logger.Information("[浮窗] 拖拽事件已到达：含文件=True，已放行；选中角色={Character} 角色数={Count}",
                ViewModel.SelectedCharacter?.DisplayName ?? "<无>", ViewModel.Characters.Count);
            return;
        }

        string formats;
        try
        {
            formats = string.Join(",", e.DataView.AvailableFormats);
        }
        catch (Exception ex)
        {
            // 延迟渲染的数据对象读格式清单会抛（来源进程忙 / 已经退出）。这是诊断，不能反过来打断拖拽本身
            formats = $"<读取失败: {ex.GetType().Name}>";
        }

        _logger.Warning("[浮窗] 拖拽事件已到达，但数据里没有文件（含文件=False，光标会显示禁止）：可用格式=[{Formats}]",
            formats);
    }

    private async void RootGrid_OnDrop(object sender, DragEventArgs e)
    {
        // 这一轮拖拽到此结束，把诊断重新武装，下一次拖拽才会再留下一行（见 _dragProbeLogged）。
        // 放在最前面：下面"读不出文件就早返回"那条路同样是一轮拖拽的收尾。
        _dragProbeLogged = false;

        // 真的落下一个包了，DragOver 那会儿写的「这一拖里没有文件」就不再成立
        // （DropModPackageAsync 随后会按这单的实际情况自己写状态行）
        ViewModel.ErrorMessage = null;

        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        // 先标 Handled 再 await：拖拽事件照样会上报给窗口，而窗口那层没有 AllowDrop
        e.Handled = true;

        var storageItems = await e.DataView.GetStorageItemsAsync();
        _logger.Information("[浮窗] 收到拖入：{Count} 项，选中角色={Character}", storageItems.Count,
            ViewModel.SelectedCharacter?.DisplayName ?? "<无>");

        await ViewModel.DropModPackageAsync(storageItems);
    }

    // ── 外部拖放通道（关掉 UAC 的机器上 WinUI 收不到拖放时走这条）──────────────────

    string IExternalDropSurface.DropSurfaceName => "浮窗";

    /// <summary>
    /// 光标旁那行说明 —— 与 XAML 那侧 <see cref="RootGrid_OnDragOver"/> 里
    /// <c>e.DragUIOverride.Caption</c> 写的是**同一句**：装给谁要等落下后认包才知道，
    /// 这里给具体角色名反而是骗人的。
    /// </summary>
    string? IExternalDropSurface.DragCaption => "自动识别角色";

    /// <summary>
    /// 浮窗的提示就是那行光标说明（见 <see cref="DragCaption"/>），页面内没有第二个提示层，
    /// 所以进出这两个回调不需要做事 —— 留着是因为接口要求。
    /// </summary>
    void IExternalDropSurface.OnExternalDragEnter()
    {
    }

    void IExternalDropSurface.OnExternalDragLeave()
    {
    }

    /// <summary>
    /// 整窗都接。判据与 XAML 那侧一致：**有文件就接**，不看选没选中角色 ——
    /// 角色列表只列「已经有 Mod 的角色」，一个都没有时它是空的，
    /// 而「拖第一个 Mod 进来」恰恰就是那个场景（见 <see cref="RootGrid_OnDragOver"/>）。
    ///
    /// <para>
    /// 至于「这一拖里到底有没有文件」，通道在问到这里之前已经查过 <c>CF_HDROP</c> 了，
    /// 所以这里不必再判一次 —— 真的没有文件时根本不会问到。
    /// </para>
    /// </summary>
    bool IExternalDropSurface.CanAcceptDropAt(Point point) => true;

    /// <summary>
    /// 外部通道送来的落下。走到这一步已经确认有文件，所以不走
    /// 「这一拖里没有文件」那条状态行，直接交给与 XAML 那条路**同一个**处理入口
    /// （<see cref="OverlayViewModel.DropModPackageAsync"/>）—— 判据只有一份。
    /// </summary>
    async Task IExternalDropSurface.HandleExternalDropAsync(IReadOnlyList<string> paths, Point point)
    {
        var storageItems = new List<IStorageItem>(paths.Count);

        foreach (var path in paths)
        {
            try
            {
                storageItems.Add(Directory.Exists(path)
                    ? await StorageFolder.GetFolderFromPathAsync(path)
                    : await StorageFile.GetFileFromPathAsync(path));
            }
            catch (Exception ex)
            {
                // 一个坏路径不该让整次拖放全废
                _logger.Warning(ex, "[浮窗] 拖入的路径取不到，已跳过这一项");
            }
        }

        _logger.Information("[浮窗] 外部通道收到拖入：{Count} 项，选中角色={Character}",
            storageItems.Count, ViewModel.SelectedCharacter?.DisplayName ?? "<无>");

        await ViewModel.DropModPackageAsync(storageItems);
    }
}