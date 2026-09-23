using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using WinUIEx;

namespace JASM.OverlaySpike;

/// <summary>
/// Phase 0 原型窗口：无边框 + 置顶 + 不夺前台焦点。
///
/// 三个能力各自对应一个待验证的假设：
///   1. 无边框 + <c>OverlappedPresenter.IsAlwaysOnTop</c> —— 能不能盖在鸣潮上（独占全屏时多半不能，见 README）；
///   2. <c>WS_EX_NOACTIVATE</c> —— 点击它时游戏会不会被挤掉前台，以及 WinUI 3 在此样式下鼠标输入是否正常；
///   3. 全局热键 —— 游戏占着前台（且以管理员运行）时，能不能把窗口叫出来。
///
/// 面板上所有状态都是**实时回读**的（样式位、前台窗口归属），而不是把"我调过什么 API"当成事实 ——
/// 这几种样式设置失败时 win32 都不会抛异常，只有回读才能证伪。
/// </summary>
public sealed partial class MainWindow : WindowEx
{
    /// <summary>本原型的热键 id。同一窗口注册多个热键时靠它区分（取 'JS' 两个字母的 ASCII 凑个能认的数）。</summary>
    private const int HotkeyId = 0x4A53;

    /// <summary>
    /// 候选热键，**按顺序尝试、注册第一个可用的**。
    ///
    /// 为什么不只用 Ctrl+Alt+M：本机实测这个组合已经被别的程序占用（注册返回"热键已注册"），
    /// 原型压根注册不上 → 第 4 个问题（提权游戏前台时热键是否有效）无从验证。
    /// 用组合键而不是单键（F8 之类）的理由：单键太容易和游戏或输入法抢。
    ///
    /// 注意 RegisterHotKey 是**独占**的：注册成功后这个组合会被系统拦下、不再传给任何程序
    /// （包括游戏）。所以正式实现里必须让用户能自己改键。
    /// 要换键改这个列表即可，每一项是 (修饰键, 主键, 说明)。
    /// </summary>
    private static readonly HotkeyCandidate[] HotkeyCandidates =
    [
        new(HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_ALT, VirtualKey.M, "Ctrl + Alt + M"),
        new(HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_ALT, VirtualKey.J, "Ctrl + Alt + J"),
        new(HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_ALT, VirtualKey.K, "Ctrl + Alt + K"),
        new(HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_SHIFT, VirtualKey.J, "Ctrl + Shift + J"),
        new(HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_ALT, VirtualKey.F9, "Ctrl + Alt + F9"),
    ];

    private readonly HWND _hwnd;
    private readonly GlobalHotkey? _hotkey;
    private readonly DispatcherTimer _diagnosticsTimer;

    private int _clickCount;
    private bool _isVisible = true;
    private bool _noActivateEnabled = true;
    private bool _acrylicEnabled;

    /// <summary>
    /// 是否**希望**窗口置顶。默认希望。
    /// 单独用一个字段而不是读托管属性，是因为托管属性和真实样式会不一致（见 <see cref="EnsureTopMost"/>）；
    /// 自愈逻辑也必须认这个意图，否则「切换置顶」按钮刚关掉置顶，下一秒就被自愈打开了。
    /// </summary>
    private bool _wantTopMost = true;

    /// <summary>
    /// 上一次记录到日志里的扩展样式。用途是捕捉**运行中**的样式变化：
    /// 窗口"自己消失"最可能的原因就是某个样式位（尤其 TOPMOST）被谁悄悄抹掉了，
    /// 每秒比对一次，变了就落盘，事后能直接看到是哪一刻变的。
    /// </summary>
    private nint _lastLoggedExStyle;

    // 拖动窗口用：按下瞬间的指针位置（DIP）与窗口位置（物理像素）——两者单位不同，换算见 OnDragStripPointerMoved
    private Point _dragOriginPointer;
    private PointInt32 _dragOriginWindow;
    private bool _isDragging;

    public MainWindow()
    {
        InitializeComponent();

        // 用 AddHandler(handledEventsToo: true) 订阅，而不是在 XAML 上写 PointerPressed="..."。
        // 原因：Button 自己会处理 PointerPressed 并标记 Handled，XAML 订阅收不到这类事件 ——
        // 实测点 8 次按钮，点击计数一直是 0。而这个计数正是"鼠标输入有没有到达窗口"的唯一证据，
        // 计数不动会把人误导成"NOACTIVATE 把鼠标输入挡住了"，方向完全错。
        RootGrid.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(OnRootPointerPressed),
            handledEventsToo: true);

        _hwnd = (HWND)WinRT.Interop.WindowNative.GetWindowHandle(this);

        ConfigureOverlayWindow();

        var hotkey = new GlobalHotkey(this, HotkeyId, HotkeyCandidates);

        if (hotkey.IsRegistered)
        {
            hotkey.Pressed += ToggleVisibility;
            _hotkey = hotkey;

            // 用了备选键就明说，否则用户按 Ctrl+Alt+M 没反应会以为功能坏了
            var substituted = hotkey.Description != HotkeyCandidates[0].Description
                ? $"\n（Ctrl+Alt+M 已被其他程序占用，自动改用这个）"
                : string.Empty;

            HotkeyText.Text = $"热键: {hotkey.Description}（全局生效，游戏在前台时也可用；再按一次隐藏）{substituted}";
        }
        else
        {
            // 注册失败是**必须**让用户看见的错误：否则用户会以为功能坏了，其实是热键被占了
            HotkeyText.Text = $"热键注册失败: {hotkey.ErrorMessage}\n（改 MainWindow.xaml.cs 里的 HotkeyCandidates 列表换几组键）";
            hotkey.Dispose();
        }

        Closed += OnClosed;

        // 居中放在首次激活之后：激活前窗口尺寸还是 0，那时算出来的位置会跑偏到屏幕左上角
        Activated += OnFirstActivated;

        _diagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _diagnosticsTimer.Tick += (_, _) => RefreshDiagnostics();
        _diagnosticsTimer.Start();

        RefreshDiagnostics();

        SpikeLog.Write($"MainWindow 初始化完成: 尺寸={Width}x{Height}(DIP) 初始可见={_isVisible} " +
                       $"热键={(_hotkey is null ? $"不可用（{hotkey.ErrorMessage}）" : _hotkey.Description)}");
    }

    /// <summary>
    /// 把这扇窗配置成浮窗形态。顺序有讲究：先设边框/置顶，最后再加 NOACTIVATE。
    /// </summary>
    private void ConfigureOverlayWindow()
    {
        // 尺寸用 WindowEx 的 DIP 语义（它会按当前 DPI 换算成物理像素），
        // 免得在 150% 缩放的屏幕上内容被挤变形
        Width = 470;
        Height = 440;

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // 无边框：浮窗上不该出现 Windows 的标题栏和关闭按钮
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // 置顶**刻意不走** presenter.IsAlwaysOnTop，也不用 WinUIEx 的 WindowEx.IsAlwaysOnTop：
        // 两者实测都不会往窗口样式里写 WS_EX_TOPMOST（连续多次启动读回的都是 0x08000100，
        // 有 NOACTIVATE、**没有** TOPMOST），而托管属性一律读回 true。
        // 不置顶的窗口会被一个普通的最大化 Chrome 窗口盖住 —— 看起来就是"浮窗自己消失了"，
        // 而且热键只能切显隐、修不了样式，唤出也白搭。所以这里直接用 SetWindowPos。
        var topMostStyle = OverlayWindowStyles.SetTopMost(_hwnd, onTop: true);
        SpikeLog.Write($"构造时设置置顶: 扩展样式 0x{(long)topMostStyle:X16}，" +
                       $"TOPMOST={(OverlayWindowStyles.HasTopMost(topMostStyle) ? "已设置 ✓" : "未设置 ✗（激活后还会再补一次）")}");

        // 不进 Alt+Tab、不占任务栏槽位。用 WinAppSDK 的托管 API，
        // 比 P/Invoke 加 WS_EX_TOOLWINDOW 干净，效果一样。
        AppWindow.IsShownInSwitchers = false;

        ApplyNoActivate(true);

        SpikeLog.Write($"窗口形态配置完成: 无边框/无标题栏=已设置 " +
                       $"置顶={(AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true } ? "已设置" : "未设置")} " +
                       $"IsShownInSwitchers={AppWindow.IsShownInSwitchers}");
    }

    /// <summary>开关 <c>WS_EX_NOACTIVATE</c> 并刷新面板（面板会回读真实样式位）。</summary>
    private void ApplyNoActivate(bool enabled)
    {
        var (before, after) = OverlayWindowStyles.SetNoActivate(_hwnd, enabled);
        _noActivateEnabled = enabled;

        SpikeLog.Write($"设置 NOACTIVATE={(enabled ? "开" : "关")}: 扩展样式 0x{(long)before:X16} -> 0x{(long)after:X16}（回读值），该位实际={(OverlayWindowStyles.HasNoActivate(after) ? "已设置" : "未设置")}");
    }

    /// <summary>
    /// 确认窗口**真的**置顶了，没有就用 <c>SetWindowPos</c> 补上。
    ///
    /// 起因是实测到的怪事：<c>OverlappedPresenter.IsAlwaysOnTop = true</c> 和
    /// WinUIEx 的 <c>WindowEx.IsAlwaysOnTop = true</c> 都不会往窗口样式里写
    /// <c>WS_EX_TOPMOST</c>（连续多次启动读回的都是 <c>0x08000100</c>），
    /// 而托管属性都读回 true。不置顶的窗口会被普通窗口盖住，用户看到的就是"浮窗自己消失了"，
    /// 而且它不会自己回来：热键只切显隐、不修样式。
    ///
    /// 所以这里一律以**扩展样式**为准做证伪，缺了就补，并把结果落盘。
    /// </summary>
    private void EnsureTopMost(string stage)
    {
        var before = OverlayWindowStyles.ReadExStyle(_hwnd);

        if (OverlayWindowStyles.HasTopMost(before))
        {
            SpikeLog.Write($"{stage}: 扩展样式确认已置顶（0x{(long)before:X16} 含 TOPMOST），无需处理");
            return;
        }

        SpikeLog.Write($"{stage}: 扩展样式 0x{(long)before:X16} **不含 TOPMOST** —— 用 SetWindowPos 补一次");

        var after = OverlayWindowStyles.SetTopMost(_hwnd, onTop: true);

        SpikeLog.Write($"{stage}: 补完后扩展样式 0x{(long)after:X16}，" +
                       $"TOPMOST={(OverlayWindowStyles.HasTopMost(after) ? "已设置 ✓" : "仍未设置 ✗ —— SetWindowPos 都不管用，问题比想象的深")}");
    }

    /// <summary>每秒刷新一次面板。全部字段都是从系统**重新读**出来的，不复用缓存值。</summary>
    private void RefreshDiagnostics()
    {
        ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        ClickCountText.Text = _clickCount.ToString();

        // 把计数写进窗口标题（无边框所以用户看不见标题栏），
        // 这样外部进程能通过 EnumWindows 读到它 —— 自动化验证"点击有没有到达窗口"就靠这个。
        Title = $"OverlaySpike 已点击 {_clickCount} 次";

        // 置顶位自愈。为什么要每秒自愈而不是设一次就算完：实测这个位会在窗口显示之后
        // 被 WinUI 自己抹掉（构造时设好、显示完就没了），而"没了"在屏幕上的表现就是
        // "浮窗被别的窗口盖住/看起来消失了"，且热键只切显隐、修不了它。
        // 只在 _wantTopMost 为真时补，否则「切换置顶」按钮的 A/B 对比会被自愈立刻推翻。
        var exStyle = OverlayWindowStyles.ReadExStyle(_hwnd);
        if (_wantTopMost && !OverlayWindowStyles.HasTopMost(exStyle))
        {
            exStyle = OverlayWindowStyles.SetTopMost(_hwnd, onTop: true);
            SpikeLog.Write($"置顶位丢失，已自动补回: 现在 0x{(long)exStyle:X16}，" +
                           $"TOPMOST={(OverlayWindowStyles.HasTopMost(exStyle) ? "已恢复 ✓" : "补不回来 ✗")}");
        }

        var noActivateActuallySet = OverlayWindowStyles.HasNoActivate(exStyle);

        // 样式位一旦变化就落盘。实测这个位**确实会**在启动过程中不一致
        // （同一次构建，两次启动一次带 TOPMOST 一次不带），所以这不是防御性代码，是真的会发生。
        if (exStyle != _lastLoggedExStyle)
        {
            SpikeLog.Write($"扩展样式变化: 0x{(long)_lastLoggedExStyle:X16} -> 0x{(long)exStyle:X16}  " +
                           $"TOPMOST={(OverlayWindowStyles.HasTopMost(exStyle) ? "在" : "不在")}  " +
                           $"NOACTIVATE={(noActivateActuallySet ? "在" : "不在")}");
            _lastLoggedExStyle = exStyle;
        }

        ExStyleText.Text = _hwnd.IsNull ? "扩展样式: 拿不到窗口句柄（HWND 为空）" : $"扩展样式: 0x{(long)exStyle:X16}";
        NoActivateText.Text = $"WS_EX_NOACTIVATE: {(noActivateActuallySet ? "实际已设置 ✓" : "实际未设置 ✗")}（本程序打算: {(_noActivateEnabled ? "开" : "关")}）";

        // 这里**不读** OverlappedPresenter.IsAlwaysOnTop：实测它会在扩展样式里根本没有
        // TOPMOST 位的情况下仍然返回 true。托管属性只作为"我以为设了什么"的对照显示。
        TopMostText.Text = $"置顶(读扩展样式): {(OverlayWindowStyles.HasTopMost(exStyle) ? "已设置 ✓" : "未设置 ✗ ← 会被别的窗口盖住！")}" +
                           $"（托管属性 IsAlwaysOnTop 说: {(AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true } ? "已启用" : "未启用")}）";
        BackdropText.Text = $"背景: {(_acrylicEnabled ? "亚克力（半透明，能看到后面的东西）" : "不透明深色")}";

        ForegroundText.Text = OverlayWindowStyles.IsOwnWindowForeground(_hwnd)
            ? "前台焦点: 本窗口持有 ← 点击把焦点从游戏抢走了（NOACTIVATE 没起作用）"
            : "前台焦点: 不在本窗口 ← 游戏仍在最前（期望结果）";
    }

    /// <summary>热键回调：切换显示/隐藏。已经在 UI 线程上，可直接操作界面。</summary>
    private void ToggleVisibility()
    {
        _isVisible = !_isVisible;

        // 用 ShowWindow(SW_SHOWNOACTIVATE) 而不是 AppWindow.Show()：后者可能顺带激活窗口，
        // 那就白瞎了 WS_EX_NOACTIVATE —— 一唤出就把游戏挤掉前台。
        // 隐藏窗口后热键依然有效：WM_HOTKEY 仍会派发到隐藏窗口的消息队列。
        PInvoke.ShowWindow(_hwnd, _isVisible ? SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE : SHOW_WINDOW_CMD.SW_HIDE);

        // 记下"我请求的"和"系统实际的"两个状态：ShowWindow 不返回成功与否，
        // 只有回读 IsWindowVisible 才能知道窗口到底藏没藏。热键按下去没反应时，
        // 这两行就是区分"热键没触发"和"触发了但窗口没动"的唯一证据。
        SpikeLog.Write($"切换显隐: 请求={(_isVisible ? "显示" : "隐藏")} " +
                       $"系统实际 IsWindowVisible={IsWindowVisibleNow()} 前台是否本窗口={OverlayWindowStyles.IsOwnWindowForeground(_hwnd)}");

        // 唤出时顺手确认置顶还在：如果 TOPMOST 位在这期间被抹掉，
        // 窗口会显示出来但立刻被别的窗口盖住 —— 用户看到的仍是"按了热键没反应"
        if (_isVisible)
            EnsureTopMost("热键唤出时");

        RefreshDiagnostics();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        CenterOnPrimaryDisplay();

        // 放在首次激活之后：这时窗口已经真的显示出来了，样式位才是可信的。
        // 构造阶段读到的值可能是"还没应用"的中间态（实测就是这个坑）。
        EnsureTopMost("首次激活后");

        SpikeLog.Write($"首次激活并居中完成: 位置={AppWindow.Position.X},{AppWindow.Position.Y} 尺寸={AppWindow.Size.Width}x{AppWindow.Size.Height}(物理像素)");
    }

    /// <summary>
    /// 把窗口摆到主显示器的可用区域中央。
    ///
    /// 没有用 WinUIEx 的居中扩展（2.5.1 里没有这个方法），自己算反而更稳：
    /// 用 <see cref="DisplayArea.WorkArea"/> 而不是整个屏幕，任务栏就不会把窗口压住。
    /// 位置必须以**物理像素**给 <see cref="AppWindow.Move"/>，而 <see cref="AppWindow.Size"/>
    /// 本身就是物理像素，所以这里两边单位一致、不需要 DPI 换算。
    /// </summary>
    private void CenterOnPrimaryDisplay()
    {
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;

        AppWindow.Move(new PointInt32(
            workArea.X + ((workArea.Width - size.Width) / 2),
            workArea.Y + ((workArea.Height - size.Height) / 2)));
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 必须注销热键：RegisterHotKey 占的是系统级资源，不注销会在进程活着期间一直扣着这个组合键
        SpikeLog.Write($"窗口 Closed 事件: 已点击 {_clickCount} 次，开始注销热键并停止计时器");
        _diagnosticsTimer.Stop();
        _hotkey?.Dispose();
        SpikeLog.Write("窗口 Closed 处理完毕");
    }

    /// <summary>
    /// 向系统查询这扇窗此刻的真实可见状态，而不是复用 <see cref="_isVisible"/> 这个意图值。
    /// <c>ShowWindow</c> 的返回值只表示"之前是否可见"，不代表这次操作成功，所以必须回读。
    /// </summary>
    private bool IsWindowVisibleNow() => PInvoke.IsWindowVisible(_hwnd);

    /// <summary>
    /// 窗口内**任意位置**的鼠标按下都算一次。
    ///
    /// 刻意做成"点哪都算"而不是"只算那个测试按钮"：要验证的是
    /// 「NOACTIVATE 样式下鼠标输入到底能不能到达这扇窗」这个更基础的问题。
    ///
    /// 靠构造函数里的 <c>AddHandler(..., handledEventsToo: true)</c> 订阅才收得到 ——
    /// 按钮会自己把 PointerPressed 标记为已处理，普通订阅在按钮上是收不到的。
    /// </summary>
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _clickCount++;
        SpikeLog.Write($"窗口内鼠标按下: 累计 {_clickCount} 次");
        RefreshDiagnostics();
    }

    private void OnToggleNoActivateClick(object sender, RoutedEventArgs e)
    {
        SpikeLog.Write("按钮: 切换不激活");
        ApplyNoActivate(!_noActivateEnabled);
        RefreshDiagnostics();
    }

    private void OnToggleTopMostClick(object sender, RoutedEventArgs e)
    {
        // 直接按扩展样式的**当前真实值**取反，而不是按某个托管属性 ——
        // 托管属性和真实样式会不一致（见 EnsureTopMost 的说明），以真实值为准才不会切反
        var current = OverlayWindowStyles.ReadExStyle(_hwnd);
        _wantTopMost = !OverlayWindowStyles.HasTopMost(current);
        var after = OverlayWindowStyles.SetTopMost(_hwnd, onTop: _wantTopMost);

        // 置顶关掉后窗口会沉到其它窗口下面，屏幕上看跟"浮窗自己关了"一模一样 ——
        // 这条日志就是为了把这种情况和"进程退出"区分开
        SpikeLog.Write($"按钮: 切换置顶 -> {(OverlayWindowStyles.HasTopMost(after) ? "扩展样式含 TOPMOST（真的置顶）" : "扩展样式不含 TOPMOST（会被其它窗口盖住，看起来像消失了）")}");
        RefreshDiagnostics();
    }

    /// <summary>
    /// 在「不透明」与「亚克力」之间切换。这不在 Phase 0 的必答项里，是顺手验一下：
    /// 系统背景（亚克力 / 云母）能不能在「无边框 + NOACTIVATE」的窗口上正常工作，
    /// 是 Phase 1 做半透明浮窗的前提。若这里一开亚克力窗口就消失或变全黑，Phase 1 的方案要先改。
    /// </summary>
    private void OnToggleBackdropClick(object sender, RoutedEventArgs e)
    {
        _acrylicEnabled = !_acrylicEnabled;

        if (_acrylicEnabled)
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();

            // 遮罩的不透明度不是随便定的：0x40（25%）时亮色桌面会直接透上来，面板上的文字和按钮
            // 一起糊掉 —— 实测就是"两个背景都看不清楚"。亚克力那层模糊很亮，必须靠遮罩把内容压出对比度，
            // 所以这里取 0xCC（80%），既保住了"背后是模糊的桌面"这个观感，又不牺牲可读性。
            // 按钮另外在 XAML 里用了实心填充，不靠主题笔刷，两种背景下都看得见。
            RootGrid.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x16));
        }
        else
        {
            SystemBackdrop = null;
            RootGrid.Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x10, 0x10, 0x16));
        }

        SpikeLog.Write($"按钮: 切换背景 -> {(_acrylicEnabled ? "亚克力" : "不透明")} " +
                       $"切换后 IsWindowVisible={IsWindowVisibleNow()}");
        RefreshDiagnostics();
    }

    private void OnRecenterClick(object sender, RoutedEventArgs e)
    {
        // 无边框窗口拖出屏幕外就再也抓不回来了（没有标题栏可拖），所以留一个逃生按钮
        SpikeLog.Write($"按钮: 回到屏幕中央（移动前位置={AppWindow.Position.X},{AppWindow.Position.Y}）");
        CenterOnPrimaryDisplay();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        // 这个按钮会让进程真的结束 —— 用户报的"点一下就没了/热键也唤不回来"，
        // 最可能的解释就是点到了它。日志里必须留下痕迹。
        SpikeLog.Write("按钮: 退出 —— 主动调用 Close()，进程即将结束（之后热键不再有任何作用）");
        Close();
    }

    private void OnDragStripPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // 指针捕获：拖出窗口范围时仍然能收到 PointerMoved，否则拖动会在边界处断掉
        if (!((UIElement)sender).CapturePointer(e.Pointer))
            return;

        _dragOriginPointer = e.GetCurrentPoint(null).Position;
        _dragOriginWindow = AppWindow.Position;
        _isDragging = true;

        SpikeLog.Write($"开始拖动: 指针={_dragOriginPointer.X:F0},{_dragOriginPointer.Y:F0}(DIP) 窗口={_dragOriginWindow.X},{_dragOriginWindow.Y}(物理像素)");
    }

    private void OnDragStripPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            return;

        var current = e.GetCurrentPoint(null).Position;

        // 指针坐标是 DIP（逻辑像素），AppWindow.Move 收的是物理像素 ——
        // 不做这一步换算，在非 100% 缩放的屏幕上拖动会明显跟不上手
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        var dx = (int)Math.Round((current.X - _dragOriginPointer.X) * scale);
        var dy = (int)Math.Round((current.Y - _dragOriginPointer.Y) * scale);

        AppWindow.Move(new PointInt32(_dragOriginWindow.X + dx, _dragOriginWindow.Y + dy));
    }

    private void OnDragStripPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        SpikeLog.Write($"结束拖动: 当前位置={AppWindow.Position.X},{AppWindow.Position.Y}");
    }
}