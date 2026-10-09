using System.Runtime.InteropServices;
using GIMI_ModManager.WinUI.Services.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.Foundation;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace GIMI_ModManager.WinUI.Services.DragDrop;

/// <summary>
/// 拖放投递通道：WinUI 3 收不到外部拖放时（关掉 UAC 的机器）由它顶上。
///
/// <para>
/// <b>为什么需要它</b>：<c>EnableLUA=0</c> 的机器上所有进程都是高完整性，WinUI 3 收不到外部拖放 ——
/// XAML 的 <c>DragEnter</c>/<c>Drop</c> 根本不触发。**已在真机实测**：那台机器上 JASM 自带的
/// `[拖拽探针]` 一条都不响，而挂在本通道上的自有落点拿到了完整的 <c>DragEnter</c> → <c>Drop</c>。
/// </para>
///
/// <para>
/// <b>它做三件事</b>：<br/>
/// ① 判定本机要不要启用（见 <see cref="IsEnvEnabled"/>）；<br/>
/// ② 在窗口上注册 <see cref="NativeDropTarget"/>，把 OLE 的**屏幕物理像素坐标**换算成
///    窗口客户区的 **XAML DIP 坐标**；<br/>
/// ③ 拿那个坐标在视觉树里命中测试，找出「这一点上愿意接住的页面」（<see cref="IExternalDropSurface"/>），
///    把文件路径交给它。
/// </para>
///
/// <para>
/// <b>它不做的两件事</b>（都是刻意的）：<br/>
/// ① **不自己判定收不收**。收不收、算谁的、装给哪个角色，全由页面按它既有的判据回答 ——
///    那条逻辑在页面里已经调过很多轮（卡片上落压缩包要改判给自动识别等），
///    在这条通道里抄第二份就等于埋一个"两条路迟早分家"的雷。<br/>
/// ② **不碰浮窗那条线**。浮窗的显隐、置顶、前台归属走的是 <c>OverlayWindowStyles</c> /
///    <c>OverlayHotkeyRegistrar</c>，与本通道无关；这里只多挂一个落点。
/// </para>
/// </summary>
// 类**不标 unsafe**（只有 Attach 需要）：unsafe 上下文里不允许 await，
// 而下面 HandleDropAsync 是异步的 —— 类级 unsafe 会让那条 await 直接编译不过（CS4004）。
internal sealed class ExternalDropChannel : IDisposable
{
    /// <summary>我们成功注册时得到的注册结果。</summary>
    private const int SOk = 0;

    /// <summary>
    /// 强制启用的环境变量名。门禁按设计会挡掉开发机（那边是中完整性、UAC 开着，
    /// 复现不出缺陷），但"路由对不对"又必须在本机验 —— 留这个口子，
    /// **默认关着**，只在手工排查/自测时临时设为 1。
    /// </summary>
    internal const string ForceEnableVariable = "JASM_FORCE_DROP_CHANNEL";

    private readonly ILogger _logger;

    /// <summary>挂过的窗口。每一条都持有落点对象的强引用，直到 <see cref="Dispose"/>。</summary>
    private readonly List<Attachment> _attachments = [];

    /// <summary>本机是否启用了这条通道（门禁的结论，只算一次）。</summary>
    internal bool IsEnabled { get; private set; }

    /// <summary>门禁的结论是否已判定过（<see cref="WindowProcessQuery.OwnIntegrityLevelRid"/> 是惰性的，读一次就够）。</summary>
    private bool _enabledEvaluated;

    /// <summary>
    /// 只验通道、不落盘。**仅在环境变量强开时成立**。
    ///
    /// <para>
    /// 为什么需要它：本机（正常机器）上 WinUI 自己的拖放是好的，强开通道会变成同一个文件
    /// **装两遍** —— 那是拿用户的 Mod 目录做实验，不行。但坐标换算与命中测试偏偏又是整条链路里
    /// 唯一无法靠推理确认的两处（屏幕像素 → 客户区 → DIP 要是不对，会整体偏到右下角去）。
    /// 所以强开时只跑这两处并记日志，**真正交给页面的那一步跳过**。
    /// </para>
    /// </summary>
    private bool _dryRun;

    /// <summary>
    /// 挂载时抓住的 **UI 线程 SynchronizationContext**，落下时用它把处理递回去。
    ///
    /// <para>
    /// <b>为什么非抓不可</b>：我们的入口是**原始 OLE 回调**，不是 XAML 事件 —— 实测那个线程上
    /// <c>SynchronizationContext.Current</c> 是空的，于是 <c>await</c> 之后的续体跑到线程池上去了。
    /// 表现是一条 <c>RPC_E_WRONG_THREAD</c>（<c>0x8001010E</c>）：
    /// 安装流程跑到「弹角色选择器」那一步才炸 —— <c>new StackPanel()</c> 在非 UI 线程上创建 XAML 控件。
    /// XAML 事件那条路没这个问题（派发事件时框架会把上下文装好），所以这个坑只有走自有落点才会踩到。
    /// </para>
    /// </summary>
    private SynchronizationContext? _uiContext;

    public ExternalDropChannel(ILogger logger) => _logger = logger.ForContext<ExternalDropChannel>();

    /// <summary>
    /// 门禁：本进程是不是高完整性（= 这台机器 UAC 关着，WinUI 的拖放必然失效）。
    ///
    /// <para>
    /// 为什么用「我们自己是高」而不是「读 EnableLUA」：启动时的降权逻辑（<c>IntegrityDowngrade</c>）
    /// 只在 shell 是中完整性时才把我们换成中完整性 —— **能一路走到这里是高，本身就等价于
    /// 「shell 也是高」= UAC 关闭**。这个判据现成、且不必去碰注册表。
    /// </para>
    ///
    /// <para>
    /// 判据读不到时按**不启用**处理：宁可少救一类机器，也不能让正常机器误进新代码路径。
    /// </para>
    /// </summary>
    private bool IsEnvEnabled()
    {
        if (_enabledEvaluated)
            return IsEnabled;

        _enabledEvaluated = true;

        var forced = Environment.GetEnvironmentVariable(ForceEnableVariable);

        if (!string.IsNullOrEmpty(forced) && forced != "0")
        {
            IsEnabled = true;
            _dryRun = true;
            _logger.Warning(
                "[拖放通道] 被环境变量 {Variable}={Value} 强制启用（本进程={OwnIntegrity}）——"
                + "**只验通道、不落盘**：不调用页面的处理，免得同一个文件被装两遍",
                ForceEnableVariable, forced, DescribeOwnIntegrity());
            return IsEnabled;
        }

        IsEnabled = WindowProcessQuery.IsOwnProcessElevated();

        _logger.Information("[拖放通道] 门禁：本进程={OwnIntegrity} → {Decision}",
            DescribeOwnIntegrity(), IsEnabled ? "启用（WinUI 拖放在这类机器上收不到，由本通道顶上）" : "不启用（正常机器，走原生 XAML 落点）");

        return IsEnabled;
    }

    /// <summary>
    /// 在一个窗口上挂落点。<paramref name="rootProvider"/> 返回该窗口的 XAML 根元素
    /// （命中测试要从它往下走）—— 用委托是因为窗口内容可能比本调用更晚就绪。
    /// </summary>
    internal unsafe void Attach(HWND window, string owner, Func<UIElement?> rootProvider,
        IExternalDropSurface? ownerSurface = null)
    {
        if (!IsEnvEnabled())
        {
            _logger.Debug("[拖放通道] {Owner} 门禁未通过，不挂载", owner);
            return;
        }

        // 抓 UI 线程的上下文（挂载一定在 UI 线程上做，见 ActivationService / OverlayWindow 的调用点）
        _uiContext ??= SynchronizationContext.Current;

        var handle = (nint)window.Value;

        // RegisterDragDrop 的前提是当前线程初始化过 OLE。UI 线程本来就是 STA（WinUI 建的），
        // 所以这一步多半只是确认；返回值如实记下来，因为"没初始化"是注册失败的常见原因之一。
        var oleResult = DropTargetInterop.OleInitialize(0);

        // ⚠️ **刻意不调 OleUninitialize**：返回值 S_FALSE 表示"本线程早就初始化过"，
        // 那是 WinUI 自己初始化来跑拖放的。我们在 Dispose 里减一次引用，
        // 就可能把 WinUI 还要用的 OLE 拆掉。进程活得比通道久，这点引用留着无害。
        var attachment = new Attachment(handle) { OwnerSurface = ownerSurface };

        _logger.Information(
            "[拖放通道] 挂载 {Owner}：OleInitialize=0x{OleResult:X8} 本进程={OwnIntegrity} shell={ShellRelation}"
            + " UI上下文={UiContext} 自有面={OwnerSurface}",
            owner, oleResult, DescribeOwnIntegrity(), AppElevation.CompareWithShell(),
            _uiContext is null ? "无（落下时会退回 DispatcherQueue）" : "有",
            ownerSurface?.DropSurfaceName ?? "无");

        attachment.Target = new NativeDropTarget(owner, handle, _logger,
            (x, y) => DecideAt(attachment, rootProvider, x, y),
            (paths, x, y) => HandleDropAsync(attachment, rootProvider, paths, x, y),
            () => OnDragEnded(attachment));

        // 把托管对象换成一个原生 COM 指针交给系统。这一步之后，系统的回调会直接进 NativeDropTarget。
        attachment.TargetPointer =
            Marshal.GetComInterfaceForObject(attachment.Target, typeof(DropTargetInterop.IDropTarget));

        var registerResult = DropTargetInterop.RegisterDragDrop(handle, attachment.TargetPointer);

        attachment.Registered = registerResult == SOk;

        _logger.Information(
            "[拖放通道] {Owner} RegisterDragDrop 结果=0x{Result:X8}（{Meaning}）；落点句柄=0x{Window:X}",
            owner, registerResult, DescribeRegisterResult(registerResult), handle);

        if (registerResult == DropTargetInterop.DragDropAlreadyRegistered)
        {
            // 系统保留先注册的那个。走到这里说明这个窗口已经被别的落点占了，
            // 我们的注册没生效 —— 后续回调不会进来。如实记下来，别把它当成"通道失效"。
            _logger.Warning("[拖放通道] {Owner} 该窗口已有落点，本次未接管；后续若没有 DragEnter，属于预期", owner);
        }

        LogWindowTree(handle, owner);

        // 挂完立刻自测一次坐标换算与命中测试。**不必等用户拖一次**：拿窗口中心当作"落点"，
        // 走一遍与真实落下完全相同的换算与命中，日志里就能看出 DIP 换算有没有偏。
        // 这一条在真机上也照样有用 —— 它给出一条不依赖用户操作的证据。
        RunSelfCheck(attachment, rootProvider);

        _attachments.Add(attachment);
    }

    /// <summary>
    /// 用窗口中心当假想落点，跑一遍 <see cref="ResolvePoint"/> + <see cref="ResolveSurface"/>。
    ///
    /// <para>
    /// 验的是整条链路里唯一两处**无法靠推理确认**的地方：<c>ScreenToClient</c> 与
    /// <c>RasterizationScale</c> 的换算（错了会整体偏到右下角），以及视觉树命中测试能不能
    /// 找到那个愿意接住的页面。整段包 try：自测绝不该影响启动。
    /// </para>
    /// </summary>
    private unsafe void RunSelfCheck(Attachment attachment, Func<UIElement?> rootProvider)
    {
        try
        {
            // 先把「窗口根本不可见」这一种单独挑出来说清楚：浮窗是**创建了但藏着**的（要按热键才显形），
            // 隐藏窗口没有可命中的内容，命中链一定是空的 —— 那是正常现象，不是换算偏了。
            // 不写这一条的话，日志里那句「命中链=<空>」会被当成故障去查。
            if (!PInvoke.IsWindowVisible(new HWND((void*)attachment.Window)))
            {
                _logger.Information("[拖放通道] 自测：窗口当前不可见（浮窗要唤出后才有内容），跳过命中测试");
                return;
            }

            if (!PInvoke.GetWindowRect(new HWND((void*)attachment.Window), out var rect))
            {
                _logger.Warning("[拖放通道] 自测：GetWindowRect 失败（错误码={ErrorCode}）", Marshal.GetLastWin32Error());
                return;
            }

            var screenX = (rect.left + rect.right) / 2;
            var screenY = (rect.top + rect.bottom) / 2;

            var (root, point) = ResolvePoint(attachment, rootProvider, screenX, screenY);
            if (root is null)
            {
                _logger.Warning("[拖放通道] 自测：拿不到 XAML 根元素，换算链断在这里");
                return;
            }

            var surface = ResolveSurface(root, point, attachment.OwnerSurface);

            _logger.Information(
                "[拖放通道] 自测（窗口中心）：屏幕=({ScreenX},{ScreenY}) → XAML DIP=({X:F1},{Y:F1})；"
                + "命中链={Chain}；接住的页面={Surface}",
                screenX, screenY, point.X, point.Y, DescribeHits(root, point),
                surface?.DropSurfaceName ?? "<无>");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[拖放通道] 自测失败（不影响通道本身）");
        }
    }

    /// <summary>
    /// 这一点上收不收 —— 换算坐标、命中测试出页面，再由页面按它自己的判据回答。
    /// 全程跑在 UI 线程（OLE 把回调派到窗口所属线程，也就是 WinUI 的 UI 线程），
    /// 所以可以直接摸视觉树。
    /// </summary>
    private DropDecision DecideAt(Attachment attachment, Func<UIElement?> rootProvider, int screenX, int screenY)
    {
        // 整段包 try：这段跑在系统的拖拽回调里，抛出去会穿过 COM 边界**直接打挂进程**。
        // 而且它只是决定光标画成什么，失败时画禁止符就够了。
        try
        {
            var (root, point) = ResolvePoint(attachment, rootProvider, screenX, screenY);
            if (root is null)
                return ClearActive(attachment);

            var surface = ResolveSurface(root, point, attachment.OwnerSurface);
            if (surface is null)
            {
                // 「光标显示禁止」有两种完全不同的来路：真的没压在任何落点上，和**该接的页面没被认出来**
                // （坐标换算偏了 / 页面没实现接口）。这一行把命中链留下来，两者一眼可分 ——
                // 只在判成"不接"时记，一次拖拽进入窗口只来一发，不会刷屏。
                _logger.Information("[拖放通道] 这一点不接（DIP=({X:F1},{Y:F1})）；命中链={Chain}",
                    point.X, point.Y, DescribeHits(root, point));
                return ClearActive(attachment);
            }

            if (!surface.CanAcceptDropAt(point))
                return ClearActive(attachment);

            // 换了个落点：旧的先收，新的再亮（成对，见 Attachment.ActiveSurface 的说明）
            if (!ReferenceEquals(attachment.ActiveSurface, surface))
            {
                attachment.ActiveSurface?.OnExternalDragLeave();
                attachment.ActiveSurface = surface;
                surface.OnExternalDragEnter();
            }

            return new DropDecision(true, surface.DragCaption);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[拖放通道] 判断能不能落时出错，按不接处理");
            return ClearActive(attachment);
        }
    }

    /// <summary>把当前亮着的落点收掉并答复"不接"。收的时候整段包 try —— 页面那边抛出来一样会穿 COM 边界。</summary>
    private DropDecision ClearActive(Attachment attachment)
    {
        try
        {
            attachment.ActiveSurface?.OnExternalDragLeave();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "[拖放通道] 收起落点提示时出错");
        }
        finally
        {
            attachment.ActiveSurface = null;
        }

        return new DropDecision(false, null);
    }

    /// <summary>这一轮拖拽结束了（拖走 / 放下）：把还亮着的提示收掉。同样整段包 try。</summary>
    private void OnDragEnded(Attachment attachment)
    {
        if (attachment.ActiveSurface is null)
            return;

        ClearActive(attachment);
    }

    /// <summary>
    /// 落下：把路径交给命中测试出来的那个页面，由它走既有的安装流程。
    ///
    /// <para>
    /// <b>必须先递回 UI 线程</b>：本方法是系统的拖拽回调调起来的，那个线程上
    /// <c>SynchronizationContext</c> 是空的，直接往下 await 会让续体落到线程池，
    /// 最后炸在「非 UI 线程上 new XAML 控件」（<c>RPC_E_WRONG_THREAD</c>，实测）。
    /// </para>
    /// </summary>
    private Task HandleDropAsync(Attachment attachment, Func<UIElement?> rootProvider,
        IReadOnlyList<string> paths, int screenX, int screenY)
    {
        if (_uiContext is { } context)
        {
            context.Post(_ => _ = HandleDropCoreAsync(attachment, rootProvider, paths, screenX, screenY), null);
            return Task.CompletedTask;
        }

        // 抓不到上下文（理论上不该发生，挂载一定在 UI 线程）也不丢功能，只是少一层保险
        return HandleDropCoreAsync(attachment, rootProvider, paths, screenX, screenY);
    }

    /// <summary>落下处理的实体。**必须在 UI 线程上跑**（见 <see cref="HandleDropAsync"/>）。</summary>
    private async Task HandleDropCoreAsync(Attachment attachment, Func<UIElement?> rootProvider,
        IReadOnlyList<string> paths, int screenX, int screenY)
    {
        try
        {
            var (root, point) = ResolvePoint(attachment, rootProvider, screenX, screenY);
            if (root is null)
            {
                _logger.Warning("[拖放通道] 落下时拿不到 XAML 根元素，放弃处理（{Count} 个文件）", paths.Count);
                return;
            }

            var surface = ResolveSurface(root, point, attachment.OwnerSurface);
            if (surface is null)
            {
                // 落下点不在任何愿意接住的页面上（比如落在导航栏、标题栏）。**这不是错误**：
                // 用户在那些地方松手本来就该什么都不发生。
                _logger.Information("[拖放通道] 落下点没有页面接住（位置=({X},{Y})），忽略", point.X, point.Y);
                return;
            }

            _logger.Information("[拖放通道] 把 {Count} 个文件交给「{Surface}」（位置=({X},{Y})）",
                paths.Count, surface.DropSurfaceName, point.X, point.Y);

            if (_dryRun)
            {
                // 命中链是自测的重点：坐标换算要有一处不对，这里立刻看得出来 ——
                // 鼠标压在角色卡片上、链上却没有卡片那一层，就是 DIP 换算偏了。
                _logger.Warning("[拖放通道] 强开自测：到此为止，不调用页面的处理。命中链={Chain}",
                    DescribeHits(root, point));
                return;
            }

            await surface.HandleExternalDropAsync(paths, point);
        }
        catch (Exception ex)
        {
            // 这里已经不在 COM 边界上（是 Post 回来的），但页面那侧抛出来同样要留住，
            // 不能变成一条无人处理的 Task 异常
            _logger.Error(ex, "[拖放通道] 处理落下的文件时出错");
        }
    }

    /// <summary>
    /// 屏幕物理像素 → 该窗口的 XAML DIP 坐标。
    ///
    /// <para>
    /// 两道换算缺一不可：<c>ScreenToClient</c> 去掉窗口在屏幕上的位置（落点给的坐标是屏幕坐标系），
    /// 再除以 <c>RasterizationScale</c> 把物理像素变成 XAML 的 DIP —— 高分屏上不除这一下，
    /// 命中测试会整体偏到右下角去（鼠标在左边，判出来却是右边的元素）。
    /// </para>
    /// </summary>
    private (UIElement? Root, Point Point) ResolvePoint(Attachment attachment, Func<UIElement?> rootProvider,
        int screenX, int screenY)
    {
        var root = rootProvider();
        if (root is null)
            return (null, default);

        var clientPoint = new DropTargetInterop.PointL { X = screenX, Y = screenY };

        // 以 GetCursorPos 为准，OLE 传来的点只当线索记着（见 DropTargetInterop.GetCursorPos 的说明：
        // 拿 OLE 的 pt 换算出来的点会全贴在窗口左边缘，那是错的）。
        if (DropTargetInterop.GetCursorPos(out var cursor))
        {
            if (cursor.X != screenX || cursor.Y != screenY)
            {
                // 特意用 Information：这一行是"坐标来源到底差多少"的直接证据，
                // 排查期要看得到。确认稳定之后可以降成 Debug。
                _logger.Information("[拖放通道] 坐标来源不一致：OLE=({OleX},{OleY}) 光标=({CurX},{CurY})，以光标为准",
                    screenX, screenY, cursor.X, cursor.Y);
            }

            clientPoint = cursor;
        }

        if (!DropTargetInterop.ScreenToClient(attachment.Window, ref clientPoint))
        {
            _logger.Warning("[拖放通道] ScreenToClient 失败（错误码={ErrorCode}）", Marshal.GetLastWin32Error());
            return (null, default);
        }

        var scale = root.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0)
            scale = 1.0;

        return (root, new Point(clientPoint.X / scale, clientPoint.Y / scale));
    }

    /// <summary>
    /// 某个点上愿意接住拖放的页面：从命中测试得到的元素**往上走**，第一个实现
    /// <see cref="IExternalDropSurface"/> 的祖先就是它（页面本身就是那个祖先）。
    ///
    /// <para>
    /// 命中测试可能返回多个元素（叠在一起的），所以逐个往上找，**第一个能接的说了算** ——
    /// 与 XAML 事件从最内层往外冒的顺序一致。
    /// </para>
    /// </summary>
    /// <summary>
    /// 找出这一点上愿意接住的落点面。**三层兜底，顺序不能反**。
    ///
    /// <para>
    /// <b>第一层（按坐标找）只负责"准"</b>：它能区分用户压在哪张角色卡片上。
    /// 但它**不能决定收不收** —— 真机实测那个坐标根本不可靠：同一次运行里反推出的客户区原点
    /// 有三个不同的值，而且算出来的点**永远贴在窗口最左边的导航栏上**（DIP x 恒为 0～70），
    /// 用户明明拖在窗口中间。拿它当门槛的结果就是「光标一路禁止符、怎么拖都装不了」。
    /// </para>
    ///
    /// <para>
    /// <b>第二层（在视觉树里找当前显示的落点面）才是收不收的判据</b>：坐标不可信时，
    /// 「当前这一页愿不愿意接」这件事仍然答得出来。用户要的是"拖进 JASM 就能装"，
    /// 精确到卡片只是锦上添花 —— 认不出卡片就走自动识别，那正是拖在空白处的既有行为。
    /// </para>
    ///
    /// <para>
    /// <b>第三层（窗口自己的面）对浮窗是必需的</b>：`Window` **不是**其内容元素的视觉父级，
    /// 前两层都找不到它 —— 实测现象就是浮窗一直显示禁止符。
    /// </para>
    /// </summary>
    private static IExternalDropSurface? ResolveSurface(UIElement root, Point point,
        IExternalDropSurface? ownerSurface)
    {
        foreach (var hit in VisualTreeHelper.FindElementsInHostCoordinates(point, root))
        {
            for (DependencyObject? current = hit; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is IExternalDropSurface surface)
                    return surface;
            }
        }

        var displayed = FindSurfaceInTree(root);
        if (displayed is not null)
            return displayed;

        if (ownerSurface is not null && IsPointInside(root, point))
            return ownerSurface;

        return null;
    }

    /// <summary>
    /// 在视觉树里找**当前显示**的落点面（深度优先，第一个命中的说话）。
    ///
    /// <para>
    /// 为什么这样能得到"当前这一页"：<c>Frame</c> 只把**当前页**挂在树上，切页时旧页会被摘掉。
    /// 所以树上存在哪个实现了接口的页面，哪个就是现在显示的。
    /// </para>
    /// </summary>
    private static IExternalDropSurface? FindSurfaceInTree(UIElement root)
    {
        if (root is IExternalDropSurface surface)
            return surface;

        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is not UIElement child)
                continue;

            var found = FindSurfaceInTree(child);
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>点（根元素坐标系）是不是落在根元素范围内。用来给「本窗口兜底的面」划边界。</summary>
    private static bool IsPointInside(UIElement root, Point point)
    {
        if (root is not FrameworkElement element)
            return true; // 拿不到尺寸就不划边界，宁可多接也不要漏掉整个窗口

        return point.X >= 0 && point.Y >= 0
               && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
    }

    /// <summary>
    /// 遍历并记录整个窗口树，每个窗口标注它有没有 OLE 落点。
    /// 出问题时这是唯一能一眼看出「落点挂在哪一层、有没有被别人顶掉」的证据。
    /// </summary>
    private void LogWindowTree(nint window, string owner)
    {
        if (!_logger.IsEnabled(Serilog.Events.LogEventLevel.Debug))
            return;

        var rows = DropTargetInterop.DescribeWindowTree(window);
        _logger.Debug("[拖放通道] {Owner} 窗口树共 {Count} 个：\n{Tree}", owner, rows.Count, string.Join("\n", rows));
    }

    /// <summary>把一个点上的命中元素链说成人话（只用于强开自测）。</summary>
    private static string DescribeHits(UIElement root, Point point)
    {
        try
        {
            var hits = VisualTreeHelper.FindElementsInHostCoordinates(point, root);
            var parts = new List<string>();

            foreach (var hit in hits.Take(6))
            {
                var dataContext = (hit as FrameworkElement)?.DataContext?.GetType().Name ?? "-";
                parts.Add($"{hit.GetType().Name}({dataContext})");
            }

            return parts.Count == 0 ? "<空>" : string.Join(" > ", parts);
        }
        catch (Exception ex)
        {
            return $"<命中测试抛异常: {ex.GetType().Name}>";
        }
    }

    private static string DescribeOwnIntegrity() => $"0x{WindowProcessQuery.OwnIntegrityLevelRid:X4}";

    private static string DescribeRegisterResult(int result) => result switch
    {
        SOk => "注册成功（这个窗口原本没有落点）",
        DropTargetInterop.DragDropAlreadyRegistered => "窗口已有落点，未接管",
        unchecked((int)0x80070005) => "E_ACCESSDENIED 权限被拒",
        unchecked((int)0x800401F0) => "CO_E_NOTINITIALIZED 当前线程没初始化 OLE",
        _ => "未知"
    };

    /// <summary>
    /// 只注销**我们自己成功注册的**落点，然后还掉 COM 引用。
    /// 绝不 <c>Revoke</c> 别人的 —— 那会把 WinUI 自己的拖放一并废掉。
    /// </summary>
    public void Dispose()
    {
        foreach (var attachment in _attachments)
        {
            if (attachment.Registered && attachment.Window != 0)
            {
                var result = DropTargetInterop.RevokeDragDrop(attachment.Window);
                _logger.Information("[拖放通道] RevokeDragDrop 结果=0x{Result:X8}", result);
                attachment.Registered = false;
            }

            if (attachment.TargetPointer != 0)
            {
                // GetComInterfaceForObject 给的是我们持有的一次引用，用完必须还 —— 不还会让 CCW 永远活着，
                // 拖拽时系统仍会回调到一个"名义上已释放"的对象
                Marshal.Release(attachment.TargetPointer);
                attachment.TargetPointer = 0;
            }

            attachment.Target = null;
        }

        _attachments.Clear();
    }

    /// <summary>一个窗口上的落点。分开成类是因为字段的生存期要求不同（见各自注释）。</summary>
    private sealed class Attachment(nint window)
    {
        internal nint Window { get; } = window;

        /// <summary>
        /// 落点实例的**强引用**。必须留着：<c>RegisterDragDrop</c> 之后系统持有的是这对象的
        /// COM 可调用包装（CCW），底层只有一个原生指针；托管对象一旦被 GC 回收，CCW 跟着失效，
        /// 之后系统的每次回调都会打在已释放的对象上 —— 那种访问违例从日志里看不出任何前因。
        /// </summary>
        internal NativeDropTarget? Target { get; set; }

        /// <summary>交给系统的 <c>IDropTarget*</c>；非 0 时由我们持有一次引用，释放时要还。</summary>
        internal nint TargetPointer { get; set; }

        /// <summary>是否由我们注册成功（决定 Dispose 时要不要 Revoke）。</summary>
        internal bool Registered { get; set; }

        /// <summary>
        /// 本窗口自己的落点面（可能为 <c>null</c>）。视觉树里找不到页面时用它兜底 ——
        /// 浮窗必须靠这个，因为 <c>Window</c> 不在其内容的视觉父链上（见 <see cref="ResolveSurface"/>）。
        /// </summary>
        internal IExternalDropSurface? OwnerSurface { get; set; }

        /// <summary>
        /// 当前这一轮拖拽"落在了谁身上"——<c>null</c> 表示没有落点亮着。
        ///
        /// <para>
        /// 存它是为了**成对**：进了谁就要由谁来收（<c>OnExternalDragLeave</c>），
        /// 否则概览页那层毛玻璃会一直挂在屏幕上 —— 拖拽走了不会再有事件来通知它收起来。
        /// </para>
        /// </summary>
        internal IExternalDropSurface? ActiveSurface { get; set; }
    }
}