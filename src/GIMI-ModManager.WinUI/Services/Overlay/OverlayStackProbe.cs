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

        return description.ToString();
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