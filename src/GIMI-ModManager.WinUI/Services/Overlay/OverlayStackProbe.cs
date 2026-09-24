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
    /// 数出「排在浮窗前面」的顶层窗口，并把最前几个的名字记下来。
    /// <c>EnumWindows</c> 是按 Z 序从最前到最后枚举的，所以碰到浮窗的位置就是它的层级：
    /// 在它之前枚举到的全是压着它的窗口。前几个就够定位 —— 真凶必然在最前面那几个里。
    /// </summary>
    private static void AppendWindowsAbove(StringBuilder description, HWND overlay)
    {
        var above = new List<HWND>();
        var aboveCount = 0;
        var foundSelf = false;

        PInvoke.EnumWindows((window, _) =>
        {
            // 已经过了浮窗：再往下的都是"在它后面"的窗口，不用数了
            if (foundSelf)
                return new BOOL(1);

            if (window.Value == overlay.Value)
            {
                foundSelf = true;
                return new BOOL(1);
            }

            aboveCount++;
            if (above.Count < MaxWindowsAbove)
                above.Add(window);

            return new BOOL(1);
        }, default);

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