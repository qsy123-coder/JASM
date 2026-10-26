using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 版本比较只服务一件事：配置要求的最低程序版本是否高于内置 CLI。
/// 3.10 必须大于 3.9（字符串比较会反过来），所以不能用裸字符串。
/// </summary>
public class WuwaVersionTests
{
    [Fact]
    public void ThreeSevenEqualsThreeSevenZero()
    {
        Assert.Equal(0, WuwaVersion.Compare("3.7", "3.7.0"));
    }

    [Fact]
    public void ComparesNumericallyNotLexically()
    {
        Assert.True(WuwaVersion.Compare("3.10", "3.9") > 0);
        Assert.True(WuwaVersion.Compare("3.7.1", "3.7.0") > 0);
    }

    [Theory]
    [InlineData("v3.7.1.0")]
    [InlineData("3.7.1")]
    [InlineData("V3.7.1")]
    [InlineData("3.7.1-beta")]
    public void AcceptsCommonFormats(string text)
    {
        Assert.True(WuwaVersion.TryParse(text, out var version));
        Assert.Equal(new Version(3, 7, 1, 0), version);
    }

    [Theory]
    [InlineData("3")]
    [InlineData("")]
    [InlineData("beta")]
    [InlineData("3.beta")]
    [InlineData(null)]
    public void RejectsInvalidVersions(string? text)
    {
        Assert.False(WuwaVersion.TryParse(text, out _));
    }

    [Fact]
    public void CompareTreatsUnparsableAsZero()
    {
        Assert.Equal(0, WuwaVersion.Compare("invalid", "also-invalid"));
        Assert.True(WuwaVersion.Compare("3.7.0", "invalid") > 0);
    }
}