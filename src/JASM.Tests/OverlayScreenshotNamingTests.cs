using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="OverlayScreenshotNaming"/> — 截图文件名的清洗与重名回避。
///
/// Mod 的显示名是用户随手写的，直接当文件名会在 <c>File.Create</c> 那一步才炸，
/// 而界面上只说得出「截图失败」。这些规则必须在落盘之前就钉死。
/// </summary>
public class OverlayScreenshotNamingTests
{
    [Fact]
    public void SanitizeReplacesInvalidFileNameCharacters()
    {
        var sanitized = OverlayScreenshotNaming.SanitizeBaseName("a/b\\c:d*e?f\"g<h>i|j");

        Assert.Equal("a_b_c_d_e_f_g_h_i_j", sanitized);
    }

    [Fact]
    public void SanitizeRemovesControlCharactersToo()
    {
        var sanitized = OverlayScreenshotNaming.SanitizeBaseName("ab" + (char)1 + "cd");

        Assert.Equal("ab_cd", sanitized);
    }

    [Fact]
    public void SanitizeTrimsWhitespaceAndTrailingDots()
    {
        // Windows 会静默吃掉结尾的点：留着只会让实际文件名与界面显示的对不上
        Assert.Equal("长离 泳装", OverlayScreenshotNaming.SanitizeBaseName("  长离 泳装...  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void SanitizeFallsBackWhenNothingUsableRemains(string? input)
    {
        Assert.Equal(OverlayScreenshotNaming.FallbackName, OverlayScreenshotNaming.SanitizeBaseName(input));
    }

    [Fact]
    public void SanitizeTruncatesToTheDocumentedLimit()
    {
        var sanitized = OverlayScreenshotNaming.SanitizeBaseName(new string('a', 200));

        Assert.Equal(OverlayScreenshotNaming.MaxBaseNameLength, sanitized.Length);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("LPT1")]
    public void SanitizeEscapesWindowsReservedDeviceNames(string input)
    {
        // CON.png 与 CON 同样打不开：保留名判的是主干
        Assert.Equal("_" + input, OverlayScreenshotNaming.SanitizeBaseName(input));
    }

    [Fact]
    public void ResolveUniqueFilePathUsesThePlainNameWhenNothingExists()
    {
        var path = OverlayScreenshotNaming.ResolveUniqueFilePath(
            @"C:\Shots", "长离", ".png", _ => false);

        Assert.Equal(@"C:\Shots\长离.png", path);
    }

    [Fact]
    public void ResolveUniqueFilePathAppendsANumberInsteadOfOverwriting()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\Shots\长离.png",
            @"C:\Shots\长离-2.png"
        };

        var path = OverlayScreenshotNaming.ResolveUniqueFilePath(
            @"C:\Shots", "长离", ".png", existing.Contains);

        Assert.Equal(@"C:\Shots\长离-3.png", path);
    }
}
