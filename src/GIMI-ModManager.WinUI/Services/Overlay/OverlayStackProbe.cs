using System.Text;
using GIMI_ModManager.WinUI.Services.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>
/// 浮窗「明明置顶了却被盖住」的现场取证：把窗口此刻的层级关系读成一行日志。
///
/// 为什么不能只看 <c>WS_EX_TOPMOST</c> 那一位：它只说明"我在置顶带里"，
/// 既不说明"我在置顶带的最前面"，也不说明"我还好好地待在屏幕上"。三种完全不同的故障
/// 都表现为「游戏进到世界之后浮窗就没了」，而它们的修法毫无交集，日志必须能分清：
///
/// <list type="number">
/// <item><b>置顶带内被压</b>：游戏窗口自己也带着置顶位、且排在浮窗前面 ——
/// 用 <c>SetWindowPos(HWND_TOPMOST)</c> 重申一次就能把最前面夺回来（见 <c>OverlayWindow.EnsureTopMost</c>）。</item>
/// <item><b>被合成器绕开</b>：枚举顺序里浮窗在游戏前面（甚至游戏窗口压根没带置顶位），
/// 画面却被游戏盖着 —— 那是无边框全屏被 DWM 提升成了独立翻转平面（「全屏优化」），
/// 盖住画面的是合成器而不是窗口，进程内任何 Z 序操作都改不了，只能从显示模式 / 兼容性设置下手。</item>
/// <item><b>浮窗被最小化 / 藏起来了</b>：可见位与置顶位都正常，但它其实已经最小化 ——
/// 最小化的窗口 <c>IsWindowVisible</c> 照样答"可见"，不单独看这一位就会把它当成"被盖住"。</item>
/// <item><b>看得见、点不到</b>：进游戏后要按住 Alt 再左键才点得动浮窗 —— 浮窗画得好好的，
/// 只是这一下点击没轮到它。那是**输入归属**问题（谁抓着鼠标 / 光标被裁在哪 / 点击先给谁），
/// 与 Z 序毫无关系，见 <see cref="AppendClickGate"/> 记的那三个开关。</item>
/// </list>
///
/// 本类**只读**：不设样式、不动 Z 序、不抢前台；读不到的一律照实记「?」，不抛异常。
/// </summary>
internal static unsafe class OverlayStackProbe
{
    /// <summary>最多列出几个压在浮窗上面的窗口。再多对定位没有帮助，只会把日志撑长。</summary>
    private const int MaxWindowsAbove = 6;

    /// <summary>把浮窗此刻的层级现场写成一行（调用方直接当一条日志记掉）。</summary>
    internal static string Describe(HWND overlay)
    {
        if (overlay.IsNull)
            return "[浮窗诊断] 窗口句柄无效";

        var ownStyle = OverlayWindowStyles.ReadExStyle(overlay);

        var description = new StringBuilder("[浮窗诊断] 我=")
            .Append(WindowProcessQuery.FormatWindow(overlay))
            .Append(" 可见=").Append(PInvoke.IsWindowVisible(overlay) != 0 ? "真" : "假")
            .Append(" 最小化=").Append(PInvoke.IsIconic(overlay) != 0 ? "是" : "否")
            .Append(" 置顶位=").Append(OverlayWindowStyles.HasTopMost(ownStyle) ? "有" : "无")
            .Append(" 前台=").Append(OverlayWindowStyles.IsOwnWindowForeground(overlay) ? "我" : "别人");

        AppendWindowsAbove(description, overlay);

        AppendClickGate(description, overlay);

        return description.ToString();
    }

    /// <summary>
    /// 记下「这一下点击能不能落到浮窗上」的三个开关。
    ///
    /// 用户报的形态是**看得见、点不到**：进游戏之后要按住 Alt 再左键才点得动浮窗上的 Mod
    /// （加载界面正常）。那不是 Z 序问题 —— 浮窗就在最前面、也画得出来 —— 而是**输入归属**问题：
    /// 点击先给谁、谁抓着鼠标、光标能不能移过来，三者任一变了，浮窗就点不动。
    ///
    /// 只记事实、不下结论。真正的用法是**同一场景测两次**（不按 Alt 待十几秒、再按住 Alt 待十几秒），
    /// 两条日志一对比，Alt 到底放开了哪个开关就一目了然。
    /// </summary>
    private static void AppendClickGate(StringBuilder description, HWND overlay)
    {
        description.Append(" 点击门槛=");

        // ① 光标现在压在谁身上：不是我们自己进程的窗口，就说明这一下根本不轮到我们。
        if (PInvoke.GetCursorPos(out var cursor))
        {
            var underCursor = PInvoke.WindowFromPoint(cursor);
            description.Append("光标下=")
                .Append(underCursor.IsNull ? "无" : WindowProcessQuery.DescribeWindow(underCursor))
                .Append(SameProcessMark(underCursor, overlay));
        }
        else
        {
            description.Append("光标下=?（读光标位置失败）");
        }

        // ② 谁抓着鼠标：抓鼠标的窗口会把**全部**鼠标消息拿走，与光标位置无关 ——
        //    这是「按住 Alt 才点得到」最可能的出处（游戏在游戏里锁鼠标，Alt 放开了它）。
        //    GetCapture 只答本线程，跨进程要看前台线程的信息，因此用 GetGUIThreadInfo(0, …)
        //    （idThread=0 = 前台窗口所在线程）。cbSize 必须先填，否则调用会失败。
        var threadInfo = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        if (PInvoke.GetGUIThreadInfo(0, &threadInfo))
        {
            description.Append("；鼠标被=")
                .Append(threadInfo.hwndCapture.IsNull
                    ? "无"
                    : WindowProcessQuery.DescribeWindow(threadInfo.hwndCapture));
        }
        else
        {
            // 失败原因不写死：实测它与「光标下=?」会在同一拍一起失败、同一进程稍后又都能读到 ——
            // 那是桌面/显示模式正在切换，不是完整性级别挡的（别照着猜错的方向去修）。
            description.Append("；鼠标被=?（读前台线程信息失败；与「光标下=?」同时出现时多半是显示模式正在切换）");
        }

        // ③ 光标被裁在哪块区域：游戏把光标锁死在自己窗口里时，物理上就移不到浮窗上 ——
        //    那种情况连「点上去」这个动作都做不出来，与谁抓着鼠标无关。
        //    没裁剪时回读到的是整屏，所以读法是「这块区域比屏幕小」才叫被裁。
        if (PInvoke.GetClipCursor(out var clip))
        {
            description.Append("；光标活动区=(")
                .Append(clip.left).Append(',').Append(clip.top).Append(")-(")
                .Append(clip.right).Append(',').Append(clip.bottom).Append(')');
        }
    }

    /// <summary>
    /// 数出「真正排在浮窗前面」的顶层窗口，并把最前几个的名字记下来。
    ///
    /// 走的是**真正的 Z 序链**：<c>GetTopWindow</c> 取 Z 序最前的那个，再一路
    /// <c>GetWindow(GW_HWNDNEXT)</c> 往下走到浮窗。**不能拿 <c>EnumWindows</c> 的枚举位置当 Z 序**：
    /// 2026-09-24 实测，同一时刻枚举数出来"我上面 5 个"，真链上是 11 个 ——
    /// IME（<c>MSCTFIME UI</c> / <c>IME</c>）那类窗口不在枚举里，而它们恰恰就在最前面那一块。
    ///
    /// 那一块本来就该在最前面，而且**抢不走**：实测 <c>HWND_TOPMOST</c>、<c>HWND_TOP</c>、
    /// 先撤置顶再置顶三种写法都返回成功、位置一动不动。所以这里要看的不是"上面有几个"，
    /// 而是**游戏窗口在不在这一块里**：在（且带置顶位）→ 置顶带里被游戏压住；
    /// 不在 → 层级上根本没输，盖住画面的只能是合成器。
    /// </summary>
    /// <summary>
    /// 这个窗口是不是我们自己进程的（浮窗所在进程），供「点击落没落到浮窗上」按进程判。
    ///
    /// **为什么不能按句柄或类名判**：实测光标压在浮窗上时，<c>WindowFromPoint</c> 回的并不是那个
    /// 顶层窗口（<c>WinUIDesktopWin32WindowClass</c>），而是 WinUI 挂在它下面的子窗口
    /// （<c>Microsoft.UI.Content.DesktopChildSiteBridge</c> / <c>PopupWindowSiteBridge</c>）。
    /// 按句柄判会得出「点击没落在浮窗上」的相反结论 —— 这条线索正好会被读反。
    /// </summary>
    private static string SameProcessMark(HWND candidate, HWND overlay)
    {
        var candidatePid = WindowProcessQuery.GetWindowProcessId(candidate);
        if (candidatePid == 0)
            return string.Empty;

        return candidatePid == WindowProcessQuery.GetWindowProcessId(overlay)
            ? "（本进程=是）"
            : "（本进程=否）";
    }

    private static void AppendWindowsAbove(StringBuilder description, HWND overlay)
    {
        var above = new List<HWND>();
        var aboveCount = 0;
        var foundSelf = false;

        for (var window = PInvoke.GetTopWindow(HWND.Null);
             !window.IsNull;
             window = PInvoke.GetWindow(window, GET_WINDOW_CMD.GW_HWNDNEXT))
        {
            if (window.Value == overlay.Value)
            {
                foundSelf = true;
                break;
            }

            aboveCount++;
            if (above.Count < MaxWindowsAbove)
                above.Add(window);
        }

        description.Append(" 我上面=").Append(aboveCount).Append(" 个顶层窗口");

        // 枚举里没有自己（句柄已失效 / 不属于本桌面）时，"上面几个"是把全屏的窗口都数了一遍，不可信
        if (!foundSelf)
        {
            description.Append("（清单里没有自己，此数不可信）");
            return;
        }

        if (above.Count == 0)
            return;

        description.Append('：');
        for (var index = 0; index < above.Count; index++)
        {
            if (index > 0)
                description.Append('；');

            // 带上被压窗口自己的置顶位：游戏窗口"有置顶位且排在我前面"就是「置顶带内被压」的签名
            description.Append(WindowProcessQuery.DescribeWindow(above[index]))
                .Append(" 置顶位=")
                .Append(OverlayWindowStyles.HasTopMost(OverlayWindowStyles.ReadExStyle(above[index])) ? "有" : "无");
        }

        if (aboveCount > above.Count)
            description.Append("；…另有 ").Append(aboveCount - above.Count).Append(" 个未列出");
    }

}