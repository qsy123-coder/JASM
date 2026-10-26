using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 结果判定：只有「退出码 0 + finish.success=true」才是确定的成功；
/// 没有 finish 但备份确实变了也算成功，避免「文件改了却报失败」。
/// </summary>
public class WuwaFixOutcomeClassifierTests
{
    private static WuwaModCliEvent Finish(bool success, string? message = "msg") =>
        new(WuwaModCliEventKind.Finish, Message: message, Success: success);

    [Fact]
    public void ExitZeroAndFinishSuccessIsSuccess()
    {
        var classification = WuwaFixOutcomeClassifier.Classify(0, Finish(true), true, false, false);

        Assert.Equal(WuwaFixOutcome.Success, classification.Outcome);
        Assert.Equal("msg", classification.Message);
    }

    [Fact]
    public void FinishFailureIsFailureEvenWithExitZero()
    {
        var classification = WuwaFixOutcomeClassifier.Classify(0, Finish(false), true, false, false);

        Assert.Equal(WuwaFixOutcome.Failed, classification.Outcome);
    }

    [Fact]
    public void ExitZeroWithoutChangesIsSkipped()
    {
        var classification = WuwaFixOutcomeClassifier.Classify(0, null, false, false, false);

        Assert.Equal(WuwaFixOutcome.Skipped, classification.Outcome);
        Assert.Equal("未发现需要修复的内容", classification.Message);
    }

    [Fact]
    public void FinishSuccessWithoutChangesIsSkipped()
    {
        // 实测：CLI 对「未匹配角色」也回 success=true，但一个 .BAK 都不会产生
        var classification = WuwaFixOutcomeClassifier.Classify(
            0, Finish(true, "共处理了 0 个文件，跳过 0 个文件"), false, false, false);

        Assert.Equal(WuwaFixOutcome.Skipped, classification.Outcome);
        Assert.Equal("共处理了 0 个文件，跳过 0 个文件", classification.Message);
    }

    [Fact]
    public void BackupChangeWithoutFinishCountsAsSuccess()
    {
        var classification = WuwaFixOutcomeClassifier.Classify(0, null, true, false, false);

        Assert.Equal(WuwaFixOutcome.Success, classification.Outcome);
    }

    [Fact]
    public void NonZeroExitIsFailureWithExitCode()
    {
        var classification = WuwaFixOutcomeClassifier.Classify(3, null, false, false, false);

        Assert.Equal(WuwaFixOutcome.Failed, classification.Outcome);
        Assert.Contains("3", classification.Message);
    }

    [Fact]
    public void CancelledWinsOverEverything()
    {
        var classification = WuwaFixOutcomeClassifier.Classify(0, Finish(true), true, true, true);

        Assert.Equal(WuwaFixOutcome.Cancelled, classification.Outcome);
    }

    [Fact]
    public void TimeoutIsFailure()
    {
        var classification = WuwaFixOutcomeClassifier.Classify(-1, null, false, true, false);

        Assert.Equal(WuwaFixOutcome.Failed, classification.Outcome);
        Assert.Equal("执行超时", classification.Message);
    }
}