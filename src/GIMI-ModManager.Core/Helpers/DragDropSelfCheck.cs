namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 「这一份进程与 shell 的完整性级别谁高」的三档关系。拖拽能不能用**只**取决于它，
/// 与「是不是管理员」无关（UAC 关掉的机器上 explorer 自己就是高，拿管理员身份去判会弹假警报）。
/// </summary>
public enum IntegrityRelation
{
    /// <summary>读不到 shell 的完整性级别（explorer 正重启、跨会话、权限不够）。</summary>
    Unknown,

    /// <summary>两边同级 —— 拖拽在权限这一档上没有问题。</summary>
    Same,

    /// <summary>本进程更高（用户勾了「以管理员身份运行」）。实测：中→高被 UIPI 拒。</summary>
    OwnHigher,

    /// <summary>本进程更低（整机提权/UAC 关闭的机器上 explorer 是 High，而本进程被普通权限拉起）。</summary>
    OwnLower
}

/// <summary>
/// 完整性级别的比较 —— **纯逻辑**，不碰 win32。读级别那一半在 WinUI 的 <c>WindowProcessQuery</c> /
/// <c>AppElevation</c>，这里只负责把两个数字换成一个结论。
///
/// <para>
/// <b>为什么值得单独成类</b>：它表达的那个结论是反直觉、而且是本项目实测纠正过的 ——
/// 高来源拖中目标**同样**被拒，所以「本进程比 shell 低」也必须能判出来（UAC 关闭 / 整机提权的机器上
/// explorer 自己就是高，正好落在这一档）。拿「是不是管理员」当判据会给出**正好相反**的结论。
/// </para>
///
/// <para>
/// 用它的地方：<c>AppElevation.CompareWithShell</c>（提权提示条与降权策略）、
/// <c>IntegrityDowngrade</c>（换份之后的实测）。
/// </para>
///
/// <para>
/// <b>名字是旧的</b>：本类原来还有「拖拽自检」那半 —— 把这层关系与「用户真的拖了一次、事件有没有
/// 进到进程」合起来出一个结论档位。那半连同设置页上那段自检界面已于 2026-10-09 全部删掉
/// （它的结论本来就是错的：把 WinUI 3 在关 UAC 机器上收不到拖放说成「被网维软件接管」，
/// 真正的病因与修法是 <c>ExternalDropChannel</c> 那条线），只剩下面这一个比较函数。
/// 类名与文件名因此显得旧，改名要一并动两处调用点（<c>AppElevation</c> 与单测），就先留着。
/// </para>
/// </summary>
public static class DragDropSelfCheck
{
    /// <summary>
    /// 比较两个完整性级别。<paramref name="shellIntegrityRid"/> 为 <c>null</c>（读不到）时返回
    /// <see cref="IntegrityRelation.Unknown"/> —— 拿不到 shell 就下不了结论，这时**不能**退化成
    /// 「照管理员身份判」：UAC 关掉的机器上那样会给出正好相反的结论。
    /// </summary>
    public static IntegrityRelation Compare(uint ownIntegrityRid, uint? shellIntegrityRid)
    {
        if (shellIntegrityRid is not { } shell)
            return IntegrityRelation.Unknown;

        if (ownIntegrityRid == shell)
            return IntegrityRelation.Same;

        return ownIntegrityRid > shell ? IntegrityRelation.OwnHigher : IntegrityRelation.OwnLower;
    }
}