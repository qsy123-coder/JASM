using System.ComponentModel;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.Input;
using GIMI_ModManager.WinUI.Services.Overlay;
using GIMI_ModManager.WinUI.ViewModels.Overlay;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Serilog;
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
public sealed partial class OverlayWindow : WindowEx
{
    /// <summary>浮窗尺寸。**DIP**（<see cref="WindowEx.Width"/> 的语义），由 WinUIEx 按 DPI 换成物理像素。</summary>
    private const int OverlayWidth = 470;

    private const int OverlayHeight = 440;

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

    /// <summary>浮窗的 ViewModel（internal：它和它手上的协调器都只在本程序集里用）。</summary>
    internal OverlayViewModel ViewModel { get; }

    internal OverlayWindow(OverlayViewModel viewModel, ILogger logger)
    {
        ViewModel = viewModel;
        _logger = logger.ForContext<OverlayWindow>();

        InitializeComponent();

        InitializePageBindings();

        // 键盘选中换行时把那一行滚进视野：浮窗只有 440 DIP 高，列表比它长，而游戏里用户看不到鼠标，
        // 「选中的那行在不在屏幕里」只能靠这一下滚动告诉他。
        // 挂在 ViewModel 的属性变更上而不是逐个赋值点去调：改选中的入口不止一个
        // （导航热键、过滤之后的重定位），挂在这里它们都自动得到同一件事。
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        _styles = new OverlayWindowStyles(_logger);
        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

        // 勾选触发的刷新（F10）送完键后要把前台交还给我们，协调器得知道交还给哪个窗口
        // （我们自己拿不回来：那一刻的输入所有者是刚注入按键的提权助手）。只有窗口知道自己的 hwnd，
        // 所以在拿到它的这里写一次。
        ViewModel.RefreshCoordinator.OverlayWindowHandle = (nint)_hwnd;

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
    /// 键盘把选中行挪走时，把新选中的那一行滚进视野。
    ///
    /// 只认 <see cref="OverlayViewModel.SelectedMod"/> 这一个属性名，别的一律早返回 ——
    /// 这个 ViewModel 上属性变更很频繁（刷新状态、搜索结果、待刷新提示），
    /// 每条都去碰一次 ListView 属于白花钱。
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OverlayViewModel.SelectedMod))
            return;

        if (ViewModel.SelectedMod is { } item)
            ModList.ScrollIntoView(item);
    }

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
            _logger.Warning("浮窗可见但前台被抢走了（当前前台={Foreground}，光标在浮窗上={OnOverlay}），此后每拍重申一次",
                WindowProcessQuery.DescribeWindow(PInvoke.GetForegroundWindow()), cursorOnOverlay);
            _foregroundLossLogged = true;
        }

        _foregroundRetakeCount++;

        // 光标在浮窗上这一次记一条（按状态变化记，不按拍数记）：它同时是"用户正伸手点浮窗"的凭据，
        // 也是"为什么这次能抢回来"的答案 —— 没这一行的话，日志里两种重申看起来一模一样。
        if (cursorOnOverlay && !_foregroundNudged)
        {
            _logger.Information("光标在浮窗上，改走「注入空鼠标事件解锁前台锁」那条路重申前台");
            _foregroundNudged = true;
        }

        // 这里**不等**回读：每秒都在跑的路径上阻塞 UI 线程 300ms 不可接受，
        // 而这次重申成没成下一拍就知道（前台是我们的就记"已收回"）。
        ForegroundWindowActivator.Activate(_hwnd, handOverRightToSetForeground: false, _logger,
            nudgeInputForForegroundLock: cursorOnOverlay);
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

        RememberPosition();
        _logger.Debug("浮窗已关闭");
    }
}