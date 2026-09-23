using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="OverlayRefreshOutcomeProtocol"/> — 把刷新结局翻译成浮窗状态行上那句给人看的话。
///
/// 这条文案是用户失败后**唯一**的线索（浮窗很小、不会展开日志），所以它不能只是"刷新失败" ——
/// 必须说清发生了什么、以及下一步能做什么。
/// </summary>
public class OverlayRefreshOutcomeProtocolTests
{
    [Theory]
    [InlineData(ElevatorRefreshReply.Ok, OverlayRefreshOutcome.Refreshed)]
    [InlineData(ElevatorRefreshReply.Failure, OverlayRefreshOutcome.Rejected)]
    [InlineData(ElevatorRefreshReply.None, OverlayRefreshOutcome.NoReply)]
    public void ClassifiesTheElevatorReply(ElevatorRefreshReply reply, OverlayRefreshOutcome expected)
        => Assert.Equal(expected, OverlayRefreshOutcomeProtocol.FromReply(reply));

    [Fact]
    public void TellsTheUserWhereToStartTheElevatorAndWarnsAboutTheFullscreenInterruption()
    {
        var text = OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.ElevatorNotRunning);

        Assert.Contains("助手", text);
        Assert.Contains("设置页", text);

        // UAC 弹窗会打断独占全屏 —— 这条提醒是有价值的，不该在精简文案时被删掉
        Assert.Contains("进游戏前", text);
    }

    [Fact]
    public void ExplainsHowToRecoverWhenTheGameWindowIsMissing()
    {
        var text = OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.TargetNotFound);

        Assert.Contains("游戏", text);
        Assert.Contains("一键配置", text);
    }

    [Fact]
    public void NeverSuggestsClickingTheGameAgainAfterAForegroundFailure()
    {
        var text = OverlayRefreshOutcomeProtocol.Describe(
            OverlayRefreshOutcome.Rejected, ElevatorRefreshProtocol.ReasonNotForeground);

        // 「先点一下游戏画面再试」是条死路（再点一下前台就回到游戏，JASM 那边依旧切不动）——
        // 送键那条路上已经删掉过这个建议一次，别在浮窗这边又写回来
        Assert.DoesNotContain("点一下游戏", text);

        // 给出的是真能改变结果的办法：把游戏切成窗口化/无边框
        Assert.Contains("窗口化", text);
    }

    [Fact]
    public void ExplainsAStaleWindowHandleAsTheGameHavingBeenClosed()
    {
        var text = OverlayRefreshOutcomeProtocol.Describe(
            OverlayRefreshOutcome.Rejected, ElevatorRefreshProtocol.ReasonBadPayload);

        Assert.Contains("句柄", text);
    }

    [Fact]
    public void SaysSoWhenTheElevatorRejectedWithoutAGivenReason()
    {
        var text = OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.Rejected, reasonToken: null);

        Assert.Contains("没有说明原因", text);
    }

    [Fact]
    public void PassesAnUnknownReasonTokenThroughInsteadOfSwallowingIt()
    {
        // 助手将来加了新原因，用户至少还能看到原文，而不是一句没有信息量的「刷新失败」
        var text = OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.Rejected, "brand-new-reason");

        Assert.Contains("brand-new-reason", text);
    }

    [Fact]
    public void PointsAtTheLogForOutcomesWithoutASpecificRemedy()
    {
        Assert.Contains("日志", OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.Failed));
    }

    [Fact]
    public void ReportsSuccessInThePastTenseSoTheStatusLineReadsAsAStatus()
        => Assert.Equal("已刷新", OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.Refreshed));

    [Fact]
    public void TellsTheUserToRestartTheElevatorWhenItStayedSilent()
    {
        var text = OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.NoReply);

        Assert.Contains("重启", text);
    }
}