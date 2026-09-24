using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="OverlayRefreshOutcomeProtocol"/> — 把刷新结局翻译成浮窗状态行上那句给人看的话。
///
/// 这条文案是用户失败后**唯一**的线索（浮窗很小、不会展开日志），所以它不能只是"刷新失败" ——
/// 必须说清发生了什么、以及下一步能做什么。
///
/// 结局词表跟着**送键**那条路走（<c>GameKeySender</c>）：助手到底回了什么、为什么拒发，都是随
/// <c>GameKeySendResult.Detail</c> 上来的动态原因，由状态行原文显示。所以这里覆盖的是
/// "没有动态原因时"的那几句兜底话，不覆盖任何助手回执 token 的翻译（那层在
/// <c>ElevatorKeySendProtocol</c>，写两份就会失配）。
/// </summary>
public class OverlayRefreshOutcomeProtocolTests
{
    [Fact]
    public void PointsAtElevationAsTheCauseWhenTheGameRunsAsAdministrator()
    {
        var text = OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.NeedsElevation);

        Assert.Contains("管理员", text);

        // 助手是内嵌在主 exe 里、版本跟着主程序走的，所以"更新 JASM"才是能真正改变结果的那一步；
        // 而"直接以管理员身份运行 JASM"是绕开整条提权通道的第二条路，也得留着。
        Assert.Contains("更新", text);
        Assert.Contains("管理员身份运行 JASM", text);
    }

    [Fact]
    public void CallsOutSyntheticInputAsTheLikelyCauseWhenTheKeyNeverLanded()
    {
        var text = OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.SendInputFailed);

        // 这一支最可能的成因是反作弊拦下合成输入、或没能把游戏切到前台 —— 用户看得懂的两个词都该在
        Assert.Contains("反作弊", text);
        Assert.Contains("前台", text);
    }

    [Fact]
    public void ExplainsHowToRecoverWhenTheGameWindowIsMissing()
    {
        var text = OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.TargetNotFound);

        Assert.Contains("游戏", text);
        Assert.Contains("一键配置", text);
    }

    [Fact]
    public void PointsAtTheLogForOutcomesWithoutASpecificRemedy()
    {
        Assert.Contains("日志", OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.Failed));
    }

    [Fact]
    public void ReportsSuccessInThePastTenseSoTheStatusLineReadsAsAStatus()
        => Assert.Equal("已刷新", OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.Refreshed));
}