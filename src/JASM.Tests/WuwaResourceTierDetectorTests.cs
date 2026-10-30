using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class WuwaResourceTierDetectorTests
{
    [Fact]
    public void DetectsASingleInstalledTier()
    {
        using var game = TestGame.Create();
        game.AddTierFile("UHD", "Paks/chunk0.pak");
        var exe = game.ExePath();

        var result = WuwaResourceTierDetector.DetectFromGameExecutable(exe);

        Assert.Equal(WuwaTierDetectionKind.Detected, result.Kind);
        Assert.Equal(WuwaResourceTier.Uhd, result.Tier);
        Assert.Equal([WuwaResourceTier.Uhd], result.Installed);
    }

    [Fact]
    public void ReportsAmbiguousWhenSeveralTiersAreInstalled()
    {
        using var game = TestGame.Create();
        game.AddTierFile("HD", "Paks/hd.pak");
        game.AddTierFile("UHD", "Paks/uhd.pak");

        var result = WuwaResourceTierDetector.DetectFromGameExecutable(game.ExePath());

        Assert.Equal(WuwaTierDetectionKind.Ambiguous, result.Kind);
        Assert.Null(result.Tier);
        Assert.Equal([WuwaResourceTier.Hd, WuwaResourceTier.Uhd], result.Installed);
    }

    [Fact]
    public void ReportsNotInstalledWhenContentExistsButTierFoldersAreEmpty()
    {
        using var game = TestGame.Create();
        Directory.CreateDirectory(game.Combine("Client", "Content", "UHD"));

        var result = WuwaResourceTierDetector.DetectFromGameExecutable(game.ExePath());

        Assert.Equal(WuwaTierDetectionKind.NotInstalled, result.Kind);
        Assert.Empty(result.Installed);
    }

    [Fact]
    public void ReportsNotFoundWhenThereIsNoContentDirectory()
    {
        using var game = TestGame.Create();

        var result = WuwaResourceTierDetector.DetectFromGameExecutable(game.ExePath());

        Assert.Equal(WuwaTierDetectionKind.NotFound, result.Kind);
    }

    private sealed class TestGame : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("jasm-uhd-tier-").FullName;

        public string Combine(params string[] parts)
        {
            var all = new string[parts.Length + 1];
            all[0] = _root;
            parts.CopyTo(all, 1);
            return System.IO.Path.Combine(all);
        }

        public string ExePath() => Combine("Client", "Binaries", "Win64", "Wuthering Waves.exe");

        public void AddTierFile(string tier, string relativePath)
        {
            var full = Combine("Client", "Content", tier, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, [0x50, 0x41, 0x4B]);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        public static TestGame Create() => new();
    }
}
