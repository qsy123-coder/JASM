using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="RefreshSendPacer"/> — 两次 F10 之间至少隔 <see cref="RefreshSendPacer.MinimumInterval"/>。
///
/// 两个方向都有代价，都要钉：放得太松 → 后一发 F10 砸进游戏还没跑完的重载窗口，白送（这才是
/// 用户在报的那个 bug）；收得太紧（连单次勾选也拖）→ 把"点了就有反应"的手感一起治没了。
/// </summary>
public class RefreshSendPacerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 19, 0, 0, TimeSpan.Zero);

    private static TimeSpan Interval => RefreshSendPacer.MinimumInterval;

    [Fact]
    public void TheFirstSendIsNeverDelayed()
    {
        var pacer = new RefreshSendPacer();

        // 单次勾选必须立刻生效：闸门只该拦"离上一发太近"的那一发，不该给每一发都垫一段等待
        Assert.Equal(TimeSpan.Zero, pacer.GetWaitBeforeSend(T0));
    }

    [Fact]
    public void ASendRightAfterThePreviousOneWaitsOutTheRemainderOfTheInterval()
    {
        var pacer = new RefreshSendPacer();
        pacer.MarkSent(T0);

        Assert.Equal(Interval - TimeSpan.FromMilliseconds(400), pacer.GetWaitBeforeSend(T0 + TimeSpan.FromMilliseconds(400)));
    }

    [Fact]
    public void ASendExactlyOnTheIntervalGoesStraightThrough()
    {
        var pacer = new RefreshSendPacer();
        pacer.MarkSent(T0);

        // 边界取"够即放行"：等够了就不该再多等哪怕一毫秒（坐标是 MarkSent 时刻 + 阈值）
        Assert.Equal(TimeSpan.Zero, pacer.GetWaitBeforeSend(T0 + Interval));
    }

    [Fact]
    public void ASendWellAfterTheIntervalGoesStraightThrough()
    {
        var pacer = new RefreshSendPacer();
        pacer.MarkSent(T0);

        Assert.Equal(TimeSpan.Zero, pacer.GetWaitBeforeSend(T0 + Interval + Interval));
    }

    [Fact]
    public void TheIntervalIsMeasuredFromTheEndOfThePreviousSendNotFromItsStart()
    {
        var pacer = new RefreshSendPacer();

        // MarkSent 收的是"送键结束"的时刻：发起后立刻记一次，等待必须是完整一个阈值，
        // 而不是把送键本身花掉的时间（切前台 + 管道往返 + 按住 80ms）算进间隔里
        pacer.MarkSent(T0);

        Assert.Equal(Interval, pacer.GetWaitBeforeSend(T0));
    }

    [Fact]
    public void EverySendIsolatedByTheIntervalEvenInABurst()
    {
        var pacer = new RefreshSendPacer();

        // 连点：第一发立刻发（T0 记一笔），0.4s 后的补发被推到 T0+Interval，
        // 再下一发又往后顺延 —— 一串之间两两都正好隔一个阈值，不会越挤越近
        Assert.Equal(TimeSpan.Zero, pacer.GetWaitBeforeSend(T0));
        pacer.MarkSent(T0);

        var secondClickAt = T0 + TimeSpan.FromMilliseconds(400);
        var secondSendAt = secondClickAt + pacer.GetWaitBeforeSend(secondClickAt);
        Assert.Equal(T0 + Interval, secondSendAt);
        pacer.MarkSent(secondSendAt);

        Assert.Equal(Interval, pacer.GetWaitBeforeSend(secondSendAt));

        var thirdSendAt = secondSendAt + Interval;
        pacer.MarkSent(thirdSendAt);

        Assert.Equal(Interval, pacer.GetWaitBeforeSend(thirdSendAt));
    }

    [Fact]
    public void TheCoalescedSubstituteSendIsPushedPastTheReloadWindow()
    {
        // 用户报的那个 bug 的最小复现（时间线）：第一发 F10 在 T0 送完，游戏开始重载；
        // 0.4s 后用户点下第二件 Mod，请求被 RefreshCoalescer 合并，补发**立刻**就要来 ——
        // 那时重载还没吃完，这一发白送（"勾上了，但游戏里没切过去"）。闸门要把它推到一个阈值之后。
        var pacer = new RefreshSendPacer();
        var firstSendCompletedAt = T0;
        var secondClickAt = T0 + TimeSpan.FromMilliseconds(400);
        pacer.MarkSent(firstSendCompletedAt);

        var wait = pacer.GetWaitBeforeSend(secondClickAt);

        Assert.True(wait > TimeSpan.Zero);
        Assert.Equal(Interval - TimeSpan.FromMilliseconds(400), wait);
    }
}