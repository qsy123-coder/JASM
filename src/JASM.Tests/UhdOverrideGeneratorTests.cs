using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class UhdOverrideGeneratorTests
{
    private const string SampleIni = """
                                      [TextureOverrideTexture0]
                                      hash = 1998df83
                                      match_priority = 0
                                      if $object_detected
                                          this = ResourceDiffuse
                                      endif

                                      [TextureOverrideComponent0]
                                      hash = 7e400733
                                      match_first_index = 0
                                      """;

    [Fact]
    public void AddsAnUhdSectionCopyingTheOriginalBinding()
    {
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1998df83"] = ["dd1f9bc8"]
        };

        var result = UhdOverrideGenerator.Generate(SampleIni, map);

        Assert.True(result.HasChanges);
        Assert.Contains("[TextureOverrideTexture0_UHD_dd1f9bc8]", result.Text);
        Assert.Contains("hash = dd1f9bc8", result.Text);
        Assert.Contains("this = ResourceDiffuse", result.Text);
        Assert.DoesNotContain("TextureOverrideComponent0_UHD", result.Text);
    }

    [Fact]
    public void IsIdempotent()
    {
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1998df83"] = ["dd1f9bc8"]
        };

        var first = UhdOverrideGenerator.Generate(SampleIni, map);
        var second = UhdOverrideGenerator.Generate(first.Text, map);

        Assert.False(second.HasChanges);
        Assert.Equal(first.Text, second.Text);
    }

    [Fact]
    public void SkipsUhdHashesAlreadyPresentInTheFile()
    {
        const string ini = """
                           [TextureOverrideTexture0]
                           hash = 1998df83
                           this = ResourceDiffuse

                           [TextureOverrideTexture9]
                           hash = dd1f9bc8
                           this = ResourceDiffuse
                           """;

        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1998df83"] = ["dd1f9bc8"]
        };

        var result = UhdOverrideGenerator.Generate(ini, map);

        Assert.False(result.HasChanges);
    }

    [Fact]
    public void AddsAllUhdVariants()
    {
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1998df83"] = ["dd1f9bc8", "4d8edb5e"]
        };

        var result = UhdOverrideGenerator.Generate(SampleIni, map);

        Assert.Equal(2, result.AddedSections.Count);
        Assert.Contains("hash = dd1f9bc8", result.Text);
        Assert.Contains("hash = 4d8edb5e", result.Text);
    }

    [Fact]
    public void LeavesUnknownHashesUntouched()
    {
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["deadbeef"] = ["cafebabe"]
        };

        var result = UhdOverrideGenerator.Generate(SampleIni, map);

        Assert.False(result.HasChanges);
        Assert.Equal(SampleIni, result.Text);
    }

    [Theory]
    [InlineData("hash = 1998df83", "1998df83")]
    [InlineData("  hash=DD1F9BC8", "dd1f9bc8")]
    [InlineData("# hash = 1998df83", null)]
    [InlineData("match_priority = 0", null)]
    [InlineData("hash = 00000000", null)]
    public void ReadsOnlyValidHashLines(string line, string? expected)
        => Assert.Equal(expected, UhdOverrideGenerator.TryReadHash(line));
}
