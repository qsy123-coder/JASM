using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="DragDropSelfCheck"/> — 拖拽自检的判定。
///
/// 这里钉的是**三种「拖不进」的来路在屏幕上长得一模一样**这件事：禁止光标 + 松手没反应、
/// 不报任何错。所以判定只能靠「完整性级别」与「事件到底进没进进程」两半证据，
/// 而这两半的每一种组合都必须在测试里有一条确定的结论 —— 否则自检报告会给出错的出路，
/// 把用户支到一件与他无关的事情上去折腾。
/// </summary>
public class DragDropSelfCheckTests
{
    private const uint LowRid = 0x1000;
    private const uint MediumRid = 0x2000;
    private const uint HighRid = 0x3000;
    private const uint SystemRid = 0x4000;

    // ── 三档关系 ────────────────────────────────────────────────

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

    // ── 结论分类 ────────────────────────────────────────────────

    /// <summary>事件到了就是通了 —— 实测优先于推断，级别再不匹配也不该说「被权限挡了」。</summary>
    [Theory]
    [InlineData(IntegrityRelation.Same)]
    [InlineData(IntegrityRelation.OwnHigher)]
    [InlineData(IntegrityRelation.OwnLower)]
    [InlineData(IntegrityRelation.Unknown)]
    public void ReceivedEventAlwaysMeansTheChannelWorks(IntegrityRelation relation)
        => Assert.Equal(DragDropSelfCheckVerdict.EventsArrived, DragDropSelfCheck.Classify(true, relation));

    [Fact]
    public void NoEventWithHigherOwnLevelPointsAtElevation()
        => Assert.Equal(DragDropSelfCheckVerdict.BlockedByOwnHigherIntegrity,
            DragDropSelfCheck.Classify(false, IntegrityRelation.OwnHigher));

    [Fact]
    public void NoEventWithLowerOwnLevelPointsAtTheReversedDirection()
        => Assert.Equal(DragDropSelfCheckVerdict.BlockedByOwnLowerIntegrity,
            DragDropSelfCheck.Classify(false, IntegrityRelation.OwnLower));

    /// <summary>同级却收不到 ⇒ 权限这一档是干净的，矛头指向整机通道（网维 / 无盘 / 安全软件）。</summary>
    [Fact]
    public void NoEventAtTheSameLevelPointsAtTheMachineWideChannel()
        => Assert.Equal(DragDropSelfCheckVerdict.BlockedByMachineChannel,
            DragDropSelfCheck.Classify(false, IntegrityRelation.Same));

    [Fact]
    public void NoEventWithUnreadableShellLevelIsUndetermined()
        => Assert.Equal(DragDropSelfCheckVerdict.Undetermined,
            DragDropSelfCheck.Classify(false, IntegrityRelation.Unknown));

    // ── 档位文案 ────────────────────────────────────────────────

    [Theory]
    [InlineData(null, "未知")]
    [InlineData(0x0000u, "不受信任(0x0000)")]
    [InlineData(LowRid, "低(0x1000)")]
    [InlineData(MediumRid, "中(0x2000)")]
    [InlineData(HighRid, "高(0x3000)")]
    [InlineData(SystemRid, "系统(0x4000)")]
    public void IntegrityLevelsHaveStableNames(uint? rid, string expected)
        => Assert.Equal(expected, DragDropSelfCheck.DescribeIntegrityLevel(rid));

    /// <summary>没见过的档位值照原样报十六进制 —— 将来 Windows 加一档，日志里也不该变成空白。</summary>
    [Fact]
    public void UnknownIntegrityRidIsReportedInHex()
        => Assert.Equal("0x1234", DragDropSelfCheck.DescribeIntegrityLevel(0x1234));

    [Fact]
    public void RelationsHaveStableNames()
    {
        Assert.Equal("同级", DragDropSelfCheck.DescribeRelation(IntegrityRelation.Same));
        Assert.Equal("本进程更高", DragDropSelfCheck.DescribeRelation(IntegrityRelation.OwnHigher));
        Assert.Equal("本进程更低", DragDropSelfCheck.DescribeRelation(IntegrityRelation.OwnLower));
        Assert.Equal("未知", DragDropSelfCheck.DescribeRelation(IntegrityRelation.Unknown));
    }

    // ── 网维 / 无盘软件名单 ─────────────────────────────────────

    [Fact]
    public void KnownBlockersAreMatchedRegardlessOfCaseAndExtension()
    {
        var found = DragDropSelfCheck.FindKnownDragBlockers(["explorer", "nbmsclient.exe", "YLHOST"]);

        Assert.Equal(["NBMSClient", "ylhost"], found);
    }

    /// <summary>顺序跟名单走，不跟输入走 —— 同一台机器的报告每次读起来要一样。</summary>
    [Fact]
    public void KnownBlockersComeBackInListOrderNotInputOrder()
    {
        var found = DragDropSelfCheck.FindKnownDragBlockers(["ylhost", "NBMSClient"]);

        Assert.Equal(["NBMSClient", "ylhost"], found);
    }

    [Fact]
    public void UnknownProcessesAreNotReported()
        => Assert.Empty(DragDropSelfCheck.FindKnownDragBlockers(["explorer", "chrome", "JASM - Just Another Skin Manager"]));

    [Fact]
    public void BlankNamesAreSkipped()
        => Assert.Empty(DragDropSelfCheck.FindKnownDragBlockers(["", "   ", "\t"]));

    [Fact]
    public void SameProcessListedTwiceIsReportedOnce()
        => Assert.Equal(["ylhost"], DragDropSelfCheck.FindKnownDragBlockers(["ylhost", "YLHOST.exe"]));

    /// <summary>名单本身要干净：重复项会让报告点名两次。</summary>
    [Fact]
    public void TheBlockerListHasNoDuplicates()
        => Assert.Equal(DragDropSelfCheck.KnownDragBlockerProcessNames.Count,
            DragDropSelfCheck.KnownDragBlockerProcessNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
}