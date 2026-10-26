using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 静默检查节流：同一会话只查一次；跨会话 12 小时；失败也记时间（由调用方负责写入）。
/// </summary>
public class WuwaConfigCheckPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ChecksWhenNeverChecked()
    {
        Assert.True(WuwaConfigCheckPolicy.ShouldCheck(null, Now, false));
    }

    [Fact]
    public void SkipsWhenAlreadyCheckedThisSession()
    {
        Assert.False(WuwaConfigCheckPolicy.ShouldCheck(null, Now, true));
    }

    [Fact]
    public void SkipsWithinTwelveHours()
    {
        Assert.False(WuwaConfigCheckPolicy.ShouldCheck(Now.AddHours(-11), Now, false));
    }

    [Fact]
    public void ChecksAfterTwelveHours()
    {
        Assert.True(WuwaConfigCheckPolicy.ShouldCheck(Now.AddHours(-13), Now, false));
    }

    [Fact]
    public void ChecksWhenLastCheckIsInTheFuture()
    {
        Assert.True(WuwaConfigCheckPolicy.ShouldCheck(Now.AddHours(1), Now, false));
    }
}