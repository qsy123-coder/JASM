using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class WuwaResourceTierTests
{
    [Theory]
    [InlineData(WuwaResourceTier.Auto, null)]
    [InlineData(WuwaResourceTier.Sd, "-krqlv=SD")]
    [InlineData(WuwaResourceTier.Hd, "-krqlv=HD")]
    [InlineData(WuwaResourceTier.Uhd, "-krqlv=UHD")]
    public void MapsTierToLaunchArgument(WuwaResourceTier tier, string? expected)
        => Assert.Equal(expected, tier.ToLaunchArgument());

    [Fact]
    public void IndexRoundTrips()
    {
        foreach (var tier in Enum.GetValues<WuwaResourceTier>())
            Assert.Equal(tier, WuwaResourceTierExtensions.FromIndex(tier.ToIndex()));
    }

    [Theory]
    [InlineData(-1, WuwaResourceTier.Auto)]
    [InlineData(4, WuwaResourceTier.Auto)]
    [InlineData(99, WuwaResourceTier.Auto)]
    public void OutOfRangeIndexFallsBackToAuto(int index, WuwaResourceTier expected)
        => Assert.Equal(expected, WuwaResourceTierExtensions.FromIndex(index));
}
