using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>
/// 浮窗的扩展窗口样式（<c>WS_EX_*</c>）读写：置顶位、不夺焦点位，以及前台归属判定。
///
/// 这些能力全部来自 Phase 0 原型（<c>src/OverlaySpike</c>，实测结论归档在
/// <c>docs/mod-env-hand-test.md</c> 第 17 节），移植时**刻意保留了原型的原始推理**，
/// 因为这里踩到的坑都是「win32 不报错、托管属性还说谎」这类只能靠实测识别的：
///
///   1. <c>WS_EX_NOACTIVATE</c>：鼠标一旦点浮窗，Windows 默认会把前台焦点转移给它，
///      游戏随即失去前台 —— 独占全屏下这通常意味着掉出全屏或直接被最小化，比 Alt+Tab 还糟。
///      实测加上这一位后点击不再改变前台窗口。
///   2. <c>WS_EX_TOPMOST</c>：<c>OverlappedPresenter.IsAlwaysOnTop</c> 和 WinUIEx 的
///      <c>WindowEx.IsAlwaysOnTop</c> **都不写这一位**，而两者都读回 <c>true</c>。
///
/// 所以本类一律以**回读扩展样式**为准，绝不以托管属性或"我调过 API 了"为准。
/// </summary>
// 整个类标 unsafe：CsWin32 生成的句柄类型（HWND 等）内部是 void* 字段，
// 读 .Value 本身就要求 unsafe 上下文（IsOwnWindowForeground / SetTopMost 都用到）。
internal sealed unsafe class OverlayWindowStyles
{
    /// <summary><c>GWL_EXSTYLE</c> 的索引。CsWin32 的枚举名太长，收一个短别名方便阅读。</summary>
    private const WINDOW_LONG_PTR_INDEX ExStyleIndex = WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE;

    /// <summary><c>WS_EX_NOACTIVATE</c>：点击或程序化唤起都不会把窗口变成前台窗口。</summary>
    private const nint WsExNoActivate = 0x08000000;

    /// <summary>
    /// <c>WS_EX_TOPMOST</c>：窗口常驻最上层。
    ///
    /// 为什么要单独查这一位：Phase 0 实测同一个 exe 连续两次启动，一次拿到 <c>0x08000108</c>（含 TOPMOST），
    /// 另一次只有 <c>0x08000100</c>（**不含** TOPMOST）—— 而托管属性 <c>OverlappedPresenter.IsAlwaysOnTop</c>
    /// 两次都读回 true。不置顶的后果不是"小瑕疵"：窗口被别的窗口盖住，看起来就是"浮窗自己消失了"，
    /// 而且热键只切显隐、修不了样式，唤出也白搭。跟 NOACTIVATE 一个道理 —— 托管属性会骗人，扩展样式不会。
    /// </summary>
    private const nint WsExTopMost = 0x00000008;

    private readonly ILogger _logger;

    internal OverlayWindowStyles(ILogger logger) => _logger = logger;

    /// <summary>
    /// 读取当前扩展窗口样式。
    /// 日志里以十六进制显示，用来确认样式**真的**写进了窗口 ——
    /// WinUI 自己也可能改动样式，光靠"我调过 API 了"不足为凭。
    ///
    /// **为什么不用 <c>GetWindowLongPtr</c>**：本工程默认按 AnyCPU 构建，而 CsWin32 对
    /// <c>GetWindowLongPtr</c> 会直接报
    /// <c>PInvoke005: This API is only available when targeting a specific CPU architecture</c>
    /// 并拒绝生成（原型当初能编译是因为显式指定了 x64 平台）。而 <c>GWL_EXSTYLE</c> 的取值
    /// 本来就是 32 位（所有 <c>WS_EX_*</c> 都远小于 2³¹），用 32 位变体既正确又不挑架构。
    ///
    /// <c>NativeMethods.txt</c> 里写的是 <c>GetWindowLongW</c>，但 CsWin32 会把 A/W 后缀归一，
    /// 实际生成的方法是**不带后缀**的 <c>GetWindowLong</c>。
    /// </summary>
    internal static nint ReadExStyle(HWND window) => PInvoke.GetWindowLong(window, ExStyleIndex);

    /// <summary>
    /// 开关 <c>WS_EX_NOACTIVATE</c>，返回改前 / 改后的样式值。
    ///
    /// 改后那个值是**回读**出来的，不是把计算值直接返回：<c>SetWindowLongPtr</c> 失败时
    /// 不抛异常、返回值还有歧义（成功且原值为 0 时同样返回 0），只有回读才能证伪。
    /// </summary>
    internal (nint Before, nint After) SetNoActivate(HWND window, bool enabled)
    {
        var before = ReadExStyle(window);
        var desired = enabled ? before | WsExNoActivate : before & ~WsExNoActivate;

        if (desired != before)
            PInvoke.SetWindowLong(window, ExStyleIndex, (int)desired);

        var after = ReadExStyle(window);

        _logger.Debug("浮窗 NOACTIVATE={Enabled}: 扩展样式 0x{Before:X16} -> 0x{After:X16}（回读值），该位实际={Actually}",
            enabled ? "开" : "关", (long)before, (long)after, HasNoActivate(after) ? "已设置" : "未设置");

        return (before, after);
    }

    /// <summary>
    /// 给定的扩展样式里是否带着 <c>WS_EX_NOACTIVATE</c>。
    /// 判断的是这个回读结果，而不是"本程序打算设成什么"——
    /// 两者不一致就说明设置被谁（很可能是 WinUI 自己）改回去了。
    /// </summary>
    internal static bool HasNoActivate(nint exStyle) => (exStyle & WsExNoActivate) != 0;

    /// <summary>
    /// 给定的扩展样式里是否带着 <c>WS_EX_TOPMOST</c>。
    /// 以这个为准判断"到底置顶了没有"，不要以 <c>IsAlwaysOnTop</c> 为准 —— 它会骗人。
    /// </summary>
    internal static bool HasTopMost(nint exStyle) => (exStyle & WsExTopMost) != 0;

    /// <summary>
    /// 直接用 <c>SetWindowPos</c> 设置置顶，返回改后的扩展样式（回读，不信返回值）。
    ///
    /// **为什么要落到这么底层**：实测 <c>OverlappedPresenter.IsAlwaysOnTop</c> 和
    /// WinUIEx 的 <c>WindowEx.IsAlwaysOnTop</c> **都不会**往窗口样式里写 <c>WS_EX_TOPMOST</c>
    /// （连续 3 次启动读回的都是 <c>0x08000100</c>，中间那个 8 就是没有），
    /// 而托管属性两次都读回 true。不置顶的窗口会被一个普通的最大化 Chrome 窗口盖住，
    /// 在用户眼里这正是"浮窗自己消失了"。所以只好用最原始的 API 兜底。
    ///
    /// 三个 <c>SWP</c> 标志缺一不可：<c>NOMOVE</c> / <c>NOSIZE</c> 保证不动窗口几何，
    /// <c>NOACTIVATE</c> 保证不激活窗口 —— 否则一唤出就把游戏的前台挤掉，白费了 NOACTIVATE 样式。
    /// </summary>
    internal unsafe nint SetTopMost(HWND window, bool onTop)
    {
        // HWND_TOPMOST = (HWND)-1、HWND_NOTOPMOST = (HWND)-2，
        // 这两个是 SetWindowPos 约定的哨兵值，不是真句柄
        var insertAfter = onTop ? (void*)(-1) : (void*)(-2);

        var ok = PInvoke.SetWindowPos(
            window,
            new HWND(insertAfter),
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        var after = ReadExStyle(window);

        // 连返回值一起记：这样能区分两种完全不同的失败 ——
        // "调用本身没成功"（ok=False，看错误码）和"调用成功了但被别人改回去"（ok=True 而样式里没这一位）。
        _logger.Debug("浮窗 SetWindowPos(置顶={OnTop}) 返回={Ok} 错误码={ErrorCode} 扩展样式=0x{ExStyle:X16}",
            onTop, ok, Marshal.GetLastWin32Error(), (long)after);

        return after;
    }

    /// <summary>
    /// 当前前台窗口是不是我们自己。
    /// 这是判定「点击浮窗有没有把焦点从游戏抢走」的直接证据 ——
    /// 显隐日志里带上它，点一下浮窗就能立刻看出 NOACTIVATE 到底起没起作用。
    /// </summary>
    internal static bool IsOwnWindowForeground(HWND window)
        => (nint)PInvoke.GetForegroundWindow().Value == (nint)window.Value;
}