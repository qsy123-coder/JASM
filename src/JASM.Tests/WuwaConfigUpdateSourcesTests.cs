using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 更新源只有两个白名单主机；重定向也必须落回白名单，不能「先跟过去再说」。
/// </summary>
public class WuwaConfigUpdateSourcesTests
{
    [Fact]
    public void TriesGiteeBeforeGitHub()
    {
        Assert.Equal(2, WuwaConfigUpdateSources.Sources.Count);
        Assert.Contains("gitee.com", WuwaConfigUpdateSources.Sources[0].Host);
        Assert.Contains("githubusercontent.com", WuwaConfigUpdateSources.Sources[1].Host);
    }

    [Theory]
    [InlineData("https://gitee.com/moonholder/Wuwa_Mod_Fixer/raw/main/config.json")]
    [InlineData("https://raw.githubusercontent.com/Moonholder/Wuwa_Mod_Fixer/main/config.json")]
    [InlineData("https://github.com/Moonholder/Wuwa_Mod_Fixer")]
    [InlineData("https://api.github.com/repos/Moonholder/Wuwa_Mod_Fixer")]
    public void AllowsWhitelistedHosts(string url)
    {
        Assert.True(WuwaConfigUpdateSources.IsAllowed(new Uri(url)));
    }

    [Theory]
    [InlineData("http://gitee.com/x")]
    [InlineData("https://evil.example.com/config.json")]
    [InlineData("https://gitee.com.evil.example/config.json")]
    [InlineData("/relative")]
    public void RejectsNonWhitelistedOrNonHttps(string url)
    {
        Assert.False(WuwaConfigUpdateSources.IsAllowed(new Uri(url, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.False(WuwaConfigUpdateSources.IsAllowed(null));
    }

    [Fact]
    public void ResolvesRelativeRedirectWithinWhitelist()
    {
        var current = new Uri("https://gitee.com/moonholder/Wuwa_Mod_Fixer/raw/main/config.json");

        Assert.True(WuwaConfigUpdateSources.TryResolveRedirect(current, "config2.json", out var next));
        Assert.Equal("https://gitee.com/moonholder/Wuwa_Mod_Fixer/raw/main/config2.json", next.ToString());
    }

    [Fact]
    public void RefusesRedirectOutsideWhitelist()
    {
        var current = new Uri("https://gitee.com/moonholder/Wuwa_Mod_Fixer/raw/main/config.json");

        Assert.False(WuwaConfigUpdateSources.TryResolveRedirect(
            current, "https://evil.example.com/config.json", out _));
    }

    [Fact]
    public void RefusesEmptyLocation()
    {
        var current = new Uri("https://gitee.com/x");

        Assert.False(WuwaConfigUpdateSources.TryResolveRedirect(current, null, out _));
    }
}