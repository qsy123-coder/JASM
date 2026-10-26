using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 释放决策：CLI 认版本 + SHA256，配置认版本 + 本地是否仍合法。
/// </summary>
public class WuwaModFixerProvisioningTests
{
    private const string Version = "3.7.0";
    private const string Sha = "737c71507a8bc43a0794cace0aba07bccee6967de95cfb29291c9700cc85a370";

    [Fact]
    public void DoesNotProvisionWhenEmbeddedResourceMissing()
    {
        Assert.False(WuwaModFixerProvisioning.ShouldProvision(false, true, null, Version, Sha, Sha));
    }

    [Fact]
    public void ProvisionsWhenExtractedCopyMissing()
    {
        Assert.True(WuwaModFixerProvisioning.ShouldProvision(true, false, null, Version, Sha, null));
    }

    [Fact]
    public void ProvisionsOnHashMismatch()
    {
        Assert.True(WuwaModFixerProvisioning.ShouldProvision(true, true, null, Version, Sha,
            new string('0', 64)));
    }

    [Fact]
    public void ReusesOnlyWhenHashAndMarkerMatch()
    {
        var marker = WuwaModFixerProtocol.BuildMarker(Version, Sha);

        Assert.False(WuwaModFixerProvisioning.ShouldProvision(true, true, marker, Version, Sha, Sha));
    }

    [Theory]
    [InlineData("3.6.9")]
    [InlineData("3.7.0|deadbeef")]
    public void ProvisionsWhenMarkerDoesNotMatch(string marker)
    {
        var normalized = marker.Contains('|') ? marker : WuwaModFixerProtocol.BuildMarker(marker, Sha);

        Assert.True(WuwaModFixerProvisioning.ShouldProvision(true, true, normalized, Version, Sha, Sha));
    }

    [Theory]
    [InlineData("3.7.1", "3.7.1", true, false)]
    [InlineData("3.7.0", "3.7.1", true, true)]
    [InlineData("3.7.2", "3.7.1", true, false)]
    [InlineData("3.7.1", "3.7.1", false, true)]
    [InlineData("bad", "3.7.1", true, true)]
    public void ConfigReplacementFollowsVersionAndValidity(
        string localVersion, string embeddedVersion, bool localValid, bool expected)
    {
        Assert.Equal(expected,
            WuwaModFixerProvisioning.ShouldReplaceConfig(localVersion, embeddedVersion, localValid));
    }

    [Fact]
    public void ReplacesConfigWhenLocalIsMissing()
    {
        Assert.True(WuwaModFixerProvisioning.ShouldReplaceConfig(null, "3.7.1", false));
    }
}