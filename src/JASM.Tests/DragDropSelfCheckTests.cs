using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="DragDropSelfCheck"/> —— 本进程与 shell 的完整性级别关系。
///
/// 这里钉的是那个**反直觉、而且被实测纠正过**的结论：高来源拖中目标同样被拒，所以
/// 「本进程比 shell 低」也必须能判出来（UAC 关闭 / 整机提权的机器上 explorer 自己就是高）。
/// 判错它，提权提示条与降权策略就会朝反方向走 —— 把用户支到更糟的那一档去。
/// </summary>
public class DragDropSelfCheckTests
{
    private const uint MediumRid = 0x2000;
    private const uint HighRid = 0x3000;

    [Fact]
    public void SameLevelIsSame()
        => Assert.Equal(IntegrityRelation.Same, DragDropSelfCheck.Compare(MediumRid, MediumRid));

    [Fact]
    public void OwnHigherWhenWeAreElevated()
        => Assert.Equal(IntegrityRelation.OwnHigher, DragDropSelfCheck.Compare(HighRid, MediumRid));

    /// <summary>
    /// 这一条是本项目实测纠正过的那个反直觉结论：**高来源拖中目标同样被拒**，
    /// 所以「我们比 shell 低」也必须能判出来（UAC 关闭 / 整机提权的机器上就是这个形态）。
    /// </summary>
    [Fact]
    public void OwnLowerWhenTheShellItselfIsHigh()
        => Assert.Equal(IntegrityRelation.OwnLower, DragDropSelfCheck.Compare(MediumRid, HighRid));

    /// <summary>读不到 shell 时**不能**退化成「照管理员身份判」—— 那种机器上会给出正好相反的结论。</summary>
    [Fact]
    public void UnreadableShellLevelIsUnknown()
        => Assert.Equal(IntegrityRelation.Unknown, DragDropSelfCheck.Compare(HighRid, null));
}