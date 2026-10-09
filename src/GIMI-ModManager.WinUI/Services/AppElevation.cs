using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.Input;

namespace GIMI_ModManager.WinUI.Services;

/// <summary>
/// 「这一份 JASM 进程的权限状态」的唯一判定。
///
/// 启动日志、管理员警告弹窗、主窗口的常驻提示、浮窗的状态行都问这里 ——
/// 四处各自判断就会出现「日志说提权了、横幅却不弹」这类不报错的偏差。
/// </summary>
internal static class AppElevation
{
    /// <summary>本进程是否以管理员（高完整性）身份运行。</summary>
    /// <remarks>
    /// 判据用完整性级别而不是 <c>WindowsPrincipal.IsInRole(Administrator)</c>：两者在正常机器上一致，
    /// 但**真正决定行为的是完整性级别**（UIPI 只认它），诊断与提示都该照它说。
    /// </remarks>
    internal static bool IsElevated() => WindowProcessQuery.IsOwnProcessElevated();

    private static readonly Lazy<bool> DragDropBlockedLazy = new(ComputeDragDropBlocked);

    /// <summary>
    /// 拖拽安装在这份进程里还能不能用。**只有它为真时才该说「拖拽不可用」。**
    /// </summary>
    /// <remarks>
    /// <para>
    /// 判据是**我们的完整性级别比 shell（explorer）高**，不是「是不是管理员」：
    /// Windows 的 UIPI 只挡「低完整性往高完整性」的拖放，同级一律放行。
    /// 两者的差别在**关掉了 UAC 的机器**上就显出来了 —— 那时 explorer 自己也是高完整性，
    /// 拖拽本来是好的，拿「是不是管理员」去判就会弹一条假警报，把能用的用户也赶去折腾权限。
    /// </para>
    /// <para>
    /// 结果缓存：本进程的完整性级别在生命周期内不变，shell 的也不会变（explorer 重启后仍是同一档）。
    /// </para>
    /// </remarks>
    internal static bool IsDragDropBlocked() => DragDropBlockedLazy.Value;

    /// <summary>
    /// 本进程与 shell 的完整性级别关系（三档），**每次现读**、不缓存。
    ///
    /// <para>
    /// 与 <see cref="IsDragDropBlocked"/> 的分工：那个是给提示条用的布尔（只关心「我们更高」这一档，
    /// 且带「shell 读不到就保守说明」的兜底），这个是给 <c>DragDropSelfCheckService</c> 用的完整三档 ——
    /// 自检必须能说出**反方向**那一档（我们比 shell 低，整机提权的机器上就是这个形态），
    /// 而那个布尔按设计答不了。
    /// </para>
    /// </summary>
    internal static IntegrityRelation CompareWithShell()
    {
        var shellProcessId = WindowProcessQuery.TryGetShellProcessId();

        var shell = shellProcessId is { } processId
            ? WindowProcessQuery.TryReadIntegrityLevelRid(processId)
            : null;

        return DragDropSelfCheck.Compare(WindowProcessQuery.OwnIntegrityLevelRid, shell);
    }

    private static bool ComputeDragDropBlocked()
    {
        var relation = CompareWithShell();

        return relation switch
        {
            // 拿不到 shell 的进程 / 完整性级别时退回「提权了就算不可用」：
            // 宁可多说一句（用户看一眼就知道自己是不是提权了），也别让他对着禁止光标猜
            IntegrityRelation.Unknown => IsElevated(),
            IntegrityRelation.OwnHigher => true,

            // 「我们比 shell 低」也拖不进，但**不在这里**亮提示条：这条横幅的文案与那个按钮都是
            // 「切回普通权限」的出路，方向正好反了，摆在这里会把用户支到更糟的一档去。
            // 这一档由拖拽自检报告给说法（见 DragDropSelfCheckVerdict.BlockedByOwnLowerIntegrity）。
            _ => false
        };
    }
}