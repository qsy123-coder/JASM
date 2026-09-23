using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="RefreshCoalescer"/> — 把连点勾选产生的一串刷新请求合并成「在跑的一次 ＋ 最多一次补发」。
///
/// 合并错了的两个方向都有代价：合并得太狠 → 最后一次勾选不生效（用户以为没勾上）；
/// 合并得太松 → 游戏被重载 N 遍（又慢又像卡死）。所以两个方向都要钉。
/// </summary>
public class RefreshCoalescerTests
{
    [Fact]
    public void FirstRequestGoesStraightThrough()
    {
        var coalescer = new RefreshCoalescer();

        Assert.True(coalescer.Request());
        Assert.True(coalescer.IsInFlight);
    }

    [Fact]
    public void RequestsArrivingDuringAnInFlightRefreshAreMergedIntoExactlyOne()
    {
        var coalescer = new RefreshCoalescer();
        Assert.True(coalescer.Request());

        // 连着勾 4 个 Mod：只有第一次真的发，其余都并进「待补发」这一笔
        Assert.False(coalescer.Request());
        Assert.False(coalescer.Request());
        Assert.False(coalescer.Request());
        Assert.False(coalescer.Request());

        // 跑完只补发**一次** —— 不是 4 次
        Assert.True(coalescer.Complete());
        Assert.False(coalescer.Complete());
    }

    [Fact]
    public void ARequestArrivingAfterCompletionStartsANewRefresh()
    {
        var coalescer = new RefreshCoalescer();
        coalescer.Request();
        Assert.False(coalescer.Complete());

        Assert.True(coalescer.Request());
    }

    [Fact]
    public void StaysInFlightWhileTheSubstituteRefreshRunsSoItCannotBeOverlapped()
    {
        var coalescer = new RefreshCoalescer();
        coalescer.Request();
        coalescer.Request(); // 攒下一笔待补发
        Assert.True(coalescer.Complete());

        // 补发期间来的新请求仍要被合并，而不是并发出去 —— 并发会让两次刷新抢前台的顺序乱掉
        Assert.True(coalescer.IsInFlight);
        Assert.False(coalescer.Request());
        Assert.True(coalescer.Complete());
        Assert.False(coalescer.Complete());
    }

    [Fact]
    public void AFailedRefreshStillReopensTheGateAsLongAsTheCallerCompletesInFinally()
    {
        var coalescer = new RefreshCoalescer();
        Assert.True(coalescer.Request());

        // 刷新中途炸了，调用方在 finally 里补上 Complete —— 异常路径也必须把闸门放开，
        // 否则浮窗从此再也刷不动，而且不报错，只是「点了没反应」
        try
        {
            throw new InvalidOperationException("管道断了");
        }
        catch (InvalidOperationException)
        {
            Assert.False(coalescer.Complete());
        }

        Assert.False(coalescer.IsInFlight);
        Assert.True(coalescer.Request());
    }
}