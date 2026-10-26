using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 资源名 / 文件名 / 标记格式的契约。名字对不上时运行期只会得到 null，
/// 因此在这里锁死，避免 csproj、Core 常量、发布脚本三处漂移。
/// </summary>
public class WuwaModFixerProtocolTests
{
    [Fact]
    public void EmbeddedResourceNamesAreStable()
    {
        Assert.Equal("JASM.WuwaModFixer.wuwa-mod-cli.exe", WuwaModFixerProtocol.CliEmbeddedResourceName);
        Assert.Equal("JASM.WuwaModFixer.config.json", WuwaModFixerProtocol.ConfigEmbeddedResourceName);
        Assert.Equal("wuwa-mod-cli.exe", WuwaModFixerProtocol.CliFileName);
        Assert.Equal("config.json", WuwaModFixerProtocol.ConfigFileName);
    }

    [Fact]
    public void MarkerRoundTrips()
    {
        var marker = WuwaModFixerProtocol.BuildMarker("3.7.0", new string('a', 64));

        Assert.True(WuwaModFixerProtocol.TryParseMarker(marker, out var version, out var sha));
        Assert.Equal("3.7.0", version);
        Assert.Equal(new string('a', 64), sha);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3.7.0")]
    [InlineData("3.7.0|short")]
    [InlineData("|" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void RejectsMalformedMarkers(string? marker)
    {
        Assert.False(WuwaModFixerProtocol.TryParseMarker(marker, out _, out _));
    }

    [Fact]
    public void PinnedCliVersionAndHashArePresent()
    {
        Assert.Equal("3.7.0", WuwaModFixerProtocol.CliVersion);
        Assert.Equal(64, WuwaModFixerProtocol.CliSha256.Length);
    }
}