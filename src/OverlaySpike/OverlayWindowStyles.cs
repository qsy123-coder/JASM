using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace JASM.OverlaySpike;

/// <summary>
/// Phase 0 的关键实验对象：<c>WS_EX_NOACTIVATE</c>。
///
/// 「窗口能浮在游戏上」只是第一关。第二关更要命：鼠标一旦点它，Windows 默认会把前台焦点
/// 转移给浮窗，游戏随即失去前台 —— 在独占全屏下这通常意味着掉出全屏或直接被最小化，
/// 体验上比 Alt+Tab 还糟。加上 <c>WS_EX_NOACTIVATE</c> 后，点击不改变前台窗口，
/// 游戏自始至终保持在最前（Discord / 各类游戏浮层用的就是这条思路）。
///
/// 这里刻意把开关做成可反复调用的（<see cref="SetNoActivate"/>），而不是一次性设置：
/// 原型需要在实机里 A/B 对比「开着 vs 关掉」两种状态下的点击行为，
/// 才能确认现象确实由这个样式引起，而不是别的什么在捣鬼。
///
/// 需要实测确认的假设：WinUI 3 的输入管线在 <c>WS_EX_NOACTIVATE</c> 下是否仍能正常
/// 收到鼠标点击与指针捕获 —— 这是非标准用法，仓库里此前没有任何先例。
/// </summary>
internal static unsafe class OverlayWindowStyles
{
    /// <summary><c>GWL_EXSTYLE</c> 的索引。CsWin32 的枚举名太长，收一个短别名方便阅读。</summary>
    private const WINDOW_LONG_PTR_INDEX ExStyleIndex = WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE;

    /// <summary><c>WS_EX_NOACTIVATE</c>：点击或程序化唤起都不会把窗口变成前台窗口。</summary>
    private const nint WsExNoActivate = 0x08000000;

    /// <summary>
    /// <c>WS_EX_TOPMOST</c>：窗口常驻最上层。
    ///
    /// 为什么要单独查这一位：实测同一个 exe 连续两次启动，一次拿到 `0x08000108`（含 TOPMOST），
    /// 另一次只有 `0x08000100`（**不含** TOPMOST）—— 而托管属性 <c>OverlappedPresenter.IsAlwaysOnTop</c>
    /// 两次都读回 true。不置顶的后果不是"小瑕疵"：窗口被别的窗口盖住，看起来就是"浮窗自己消失了"。
    /// 跟 NOACTIVATE 一个道理 —— 托管属性会骗人，扩展样式不会。
    /// </summary>
    private const nint WsExTopMost = 0x00000008;

    /// <summary>
    /// 读取当前扩展窗口样式。
    /// 面板上以十六进制显示，用来确认样式**真的**写进了窗口 ——
    /// WinUI 自己也可能改动样式，光靠"我调过 API 了"不足为凭。
    /// </summary>
    internal static nint ReadExStyle(HWND window) => PInvoke.GetWindowLongPtr(window, ExStyleIndex);

    /// <summary>
    /// 开关 <c>WS_EX_NOACTIVATE</c>，返回改前 / 改后的样式值。
    ///
    /// 改后那个值是**回读**出来的，不是把计算值直接返回：<c>SetWindowLongPtr</c> 失败时
    /// 不抛异常、返回值还有歧义（成功且原值为 0 时同样返回 0），只有回读才能证伪。
    /// </summary>
    internal static (nint Before, nint After) SetNoActivate(HWND window, bool enabled)
    {
        var before = ReadExStyle(window);
        var desired = enabled ? before | WsExNoActivate : before & ~WsExNoActivate;

        if (desired != before)
            PInvoke.SetWindowLongPtr(window, ExStyleIndex, desired);

        return (before, ReadExStyle(window));
    }

    /// <summary>
    /// 给定的扩展样式里是否带着 <c>WS_EX_NOACTIVATE</c>。
    /// 面板上显示的是这个回读结果，而不是"本程序打算设成什么"——
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
    internal static nint SetTopMost(HWND window, bool onTop)
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
        SpikeLog.Write($"SetWindowPos(置顶={onTop}) 返回={ok} 错误码={Marshal.GetLastWin32Error()} 扩展样式=0x{(long)after:X16}");

        return after;
    }

    /// <summary>
    /// 当前前台窗口是不是我们自己。
    /// 这是判定「点击浮窗有没有把焦点从游戏抢走」的直接证据 —— 面板每秒刷新它，
    /// 点一下浮窗就能立刻看出 NOACTIVATE 到底起没起作用。
    /// </summary>
    internal static bool IsOwnWindowForeground(HWND window)
        => (nint)PInvoke.GetForegroundWindow().Value == (nint)window.Value;
}