using System.Runtime.InteropServices;
using GIMI_ModManager.WinUI.Services.Input;

using Serilog;
using Windows.Win32.Foundation;

namespace GIMI_ModManager.WinUI.Services.DragDrop;

/// <summary>
/// 「这台机器上，我们自己实现的落点到底收不收得到拖放」的探针。
///
/// <para>
/// <b>为什么先探针、不直接改</b>：缺陷触发的条件（关掉 UAC / 全员高完整性）在开发机上构造不出来 ——
/// 开发机三个进程全是中完整性，本机能跑通**不构成证据**。唯一能回答的是那台真机，
/// 而真机往返一次很贵，所以第一趟只问一个问题：<b>自有落点能不能收到 <c>DragEnter</c>、光标对不对</b>。
/// 答案决定后面是接着做落点接线，还是换形态（见 <c>docs/dragdrop-uac-off-prd.md</c> 的 Phase 0 / Phase 2）。
/// </para>
///
/// <para>
/// <b>为什么零风险</b>：本类**只做「尝试注册 + 读返回值 + 记日志」**，绝不 <c>Revoke</c> 别人的落点。
/// 所以有三种结果，每种都有用且都不破坏现状：
/// </para>
/// <list type="bullet">
///   <item><c>S_OK</c>：这个 HWND 原本没有落点，现在我们注册上了。看后续回调到不到，即可判定自有落点是否可用。</item>
///   <item><c>DRAGDROP_E_ALREADYREGISTERED</c>（<c>0x80040101</c>）：**WinUI 自己已经在这个 HWND 上注册过了** ——
///     这本身就是一条关键情报（说明故障点在 WinUI 的落点实现，而不是"这个窗口压根没有落点"）。</item>
///   <item>其它错误码：注册失败，照原样记下来。</item>
/// </list>
///
/// <para>
/// <b>浮窗一个字都没碰</b>：本类只挂在传进来的那个窗口上。浮窗的显隐、置顶、前台归属都不经过这里
/// （那些在 <c>OverlayWindowStyles</c> / <c>OverlayHotkeyRegistrar</c> 那条线上），所以不会影响
/// 浮窗的开关与前台优先级。
/// </para>
/// </remarks>
// 整个类标 unsafe：CsWin32 生成的句柄类型（HWND）内部是 void* 字段，读 .Value 就要求 unsafe 上下文
// （与 OverlayWindowStyles 同款，见那边的类注释）。
internal sealed unsafe class DropTargetProbe : IDisposable
{
    /// <summary>我们成功注册时得到的注册结果。</summary>
    private const int SOk = 0;

    private readonly ILogger _logger;

    /// <summary>
    /// 落点实例的**强引用**。
    ///
    /// 必须留着：<c>RegisterDragDrop</c> 之后系统持有的是这对象的 COM 可调用包装（CCW），
    /// 底层只有一个原生指针；托管对象一旦被 GC 回收，CCW 跟着失效，之后系统的每次回调
    /// 都会打在已释放的对象上 —— 那种崩溃（访问违例）从日志里看不出任何前因。
    /// </summary>
    private NativeDropTarget? _target;

    /// <summary>交给系统的 <c>IDropTarget*</c>；非 0 时由我们持有一次引用，释放时要还。</summary>
    private nint _targetPointer;

    private nint _window;
    private bool _registered;

    /// <summary>「8 秒后复看窗口树」的一次性计时器（见 <see cref="Attach"/> 里的说明）。</summary>
    private Timer? _recheck;

    /// <summary>
    /// ⚠️ <b>必须是 <c>public</c>，哪怕是 <c>internal</c> 的类</b>：DI 的 <c>ActivatorUtilities</c>
    /// 只考虑 public 构造函数。写成 <c>internal</c> 时**编译照样通过**，直到运行期取服务的那一刻才抛
    /// <c>InvalidOperationException: A suitable constructor ... could not be located</c>
    /// （本仓库在拖拽自检上已经栽过一次，那次异常抛在拖拽回调里、直接把进程打挂）。
    /// 形状与 <c>OverlayWindowService</c> 一致。
    /// </summary>
    public DropTargetProbe(ILogger logger) => _logger = logger.ForContext<DropTargetProbe>();

    /// <summary>
    /// 在指定窗口上尝试挂自有落点。<paramref name="owner"/> 只用于日志里区分是哪个窗口。
    /// </summary>
    internal void Attach(HWND window, string owner)
    {
        _window = (nint)window.Value;

        // RegisterDragDrop 的前提是当前线程初始化过 OLE。UI 线程本来就是 STA（WinUI 建的），
        // 所以这一步多半只是确认；返回值如实记下来，因为"没初始化"是注册失败的常见原因之一。
        var oleResult = DropTargetInterop.OleInitialize(0);

        // ⚠️ **刻意不调 OleUninitialize**：返回值 S_FALSE 表示"本线程早就初始化过"，
        // 那是 WinUI 自己初始化来跑拖放的。我们在 Dispose 里减一次引用，
        // 就可能把 WinUI 还要用的 OLE 拆掉。进程活得比探针久，这点引用留着无害。
        _logger.Information(
            "[拖放通道] 挂载 {Owner}：OleInitialize=0x{OleResult:X8} 本进程={OwnIntegrity} shell={ShellRelation}",
            owner, oleResult,
            WindowProcessQuery.OwnIntegrityLevelRid.ToString("X4"),
            AppElevation.CompareWithShell());

        _target = new NativeDropTarget(owner, _window, _logger);

        // 把托管对象换成一个原生 COM 指针交给系统。这一步之后，系统的回调会直接进 NativeDropTarget。
        _targetPointer = Marshal.GetComInterfaceForObject(_target, typeof(DropTargetInterop.IDropTarget));

        var registerResult = DropTargetInterop.RegisterDragDrop(_window, _targetPointer);

        _registered = registerResult == SOk;

        _logger.Information(
            "[拖放通道] {Owner} RegisterDragDrop 结果=0x{Result:X8}（{Meaning}）；落点句柄=0x{Window:X}",
            owner, registerResult, DescribeRegisterResult(registerResult), _window);

        if (registerResult == DropTargetInterop.DragDropAlreadyRegistered)
        {
            // 走到这里说明 WinUI 在同一个 HWND 上已经有落点了。我们的注册没生效（系统保留先注册的那个），
            // 所以**不能**指望后续回调进到我们这儿 —— 这也正是"光标仍然是禁止符"的可能解释之一：
            // 真正在应答系统的是 WinUI 那份落点。如实记下来，别把它当成"探针失效"。
            _logger.Warning(
                "[拖放通道] {Owner} 该窗口已被别的落点占用（多半是 WinUI 自己），本次未接管；"
                + "后续若看到 DragEnter 不进来，属于预期", owner);
        }

        // 注册之后立刻把窗口树连同「谁持有 OLE 落点」记下来。**这一步比看光标有用**：
        // 光标只说明"没接住"，属性说明"接的人在不在这儿、挂在哪一层"。
        LogWindowTree(owner);

        // 再看一眼：启动瞬间不是稳态 —— WinUI 很可能要等内容首次渲染、或首个 AllowDrop 元素
        // 真正命中时才去注册自己的落点。这一眼决定一件要命的事：**我们是不是抢先占了位、
        // 把 WinUI 自己的落点挤掉了**（同一个 HWND 上 RegisterDragDrop 只会成功一次，
        // 后来的那个拿到 DRAGDROP_E_ALREADYREGISTERED 就默默失败了）。
        // 若复看时这个窗口的落点还是我们的，就说明确实存在占位风险，得改挂载时机或换窗口。
        _recheck = new Timer(_ => LogWindowTree(owner + "（8 秒后复看）"), null,
            TimeSpan.FromSeconds(8), Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 遍历并记录整个窗口树，每个窗口标注它有没有 OLE 落点。
    ///
    /// <para>
    /// 要回答的问题：WinUI 3 的落点到底挂在**哪一层**窗口上（顶层？还是内容岛那层子窗口？），
    /// 以及**什么时候**挂上去的。若它挂在盖住整个客户区的子窗口上，光标下就永远是那个子窗口 ——
    /// 我们挂在顶层的落点只会被问一下就顶掉，这条路就得改（见 PRD 的 Phase 0 退出条件）。
    /// </para>
    /// </summary>
    private void LogWindowTree(string owner)
    {
        var rows = DropTargetInterop.DescribeWindowTree(_window);

        _logger.Information("[拖放通道] {Owner} 窗口树共 {Count} 个：\n{Tree}",
            owner, rows.Count, string.Join("\n", rows));
    }

    /// <summary>
    /// 只注销**我们自己成功注册的**那一份，然后还掉 COM 引用。
    /// </summary>
    public void Dispose()
    {
        _recheck?.Dispose();
        _recheck = null;

        if (_registered && _window != 0)
        {
            var result = DropTargetInterop.RevokeDragDrop(_window);
            _logger.Information("[拖放通道] RevokeDragDrop 结果=0x{Result:X8}", result);
            _registered = false;
        }

        if (_targetPointer != 0)
        {
            // GetComInterfaceForObject 给的是我们持有的一次引用，用完必须还 —— 不还会让 CCW 永远活着，
            // 拖拽时系统仍会回调到一个"名义上已 Dispose"的对象
            Marshal.Release(_targetPointer);
            _targetPointer = 0;
        }

        _target = null;
        _window = 0;
    }

    private static string DescribeRegisterResult(int result) => result switch
    {
        SOk => "注册成功（这个窗口原本没有落点）",
        DropTargetInterop.DragDropAlreadyRegistered => "窗口已有落点，未接管",
        unchecked((int)0x80070005) => "E_ACCESSDENIED 权限被拒",
        unchecked((int)0x800401F0) => "CO_E_NOTINITIALIZED 当前线程没初始化 OLE",
        _ => "未知"
    };
}