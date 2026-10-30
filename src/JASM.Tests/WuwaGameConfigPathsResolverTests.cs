using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class WuwaGameConfigPathsResolverTests
{
    [Fact]
    public void ResolvesFromTheOfficialClientLayout()
    {
        using var root = TempDirectory.Create();
        var configDir = root.Combine("Client", "Saved", "Config", "WindowsNoEditor");
        Directory.CreateDirectory(configDir);
        var exe = Path.Combine(root.Path, "Client", "Binaries", "Win64", "Wuthering Waves.exe");

        var paths = WuwaGameConfigPathsResolver.ResolveFromGameExecutable(exe);

        Assert.NotNull(paths);
        Assert.Equal(Path.Combine(configDir, "Engine.ini"), paths.EngineIniPath);
        Assert.Equal(Path.Combine(configDir, "GameUserSettings.ini"), paths.GameUserSettingsIniPath);
    }

    [Fact]
    public void ResolvesWhenTheExeSitsUnderAGameRootWithClientSubdirectory()
    {
        using var root = TempDirectory.Create();
        var configDir = root.Combine("Wuthering Waves Game", "Client", "Saved", "Config", "WindowsNoEditor");
        Directory.CreateDirectory(configDir);
        var exe = Path.Combine(root.Path, "Wuthering Waves Game", "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe");

        var paths = WuwaGameConfigPathsResolver.ResolveFromGameExecutable(exe);

        Assert.NotNull(paths);
        Assert.StartsWith(configDir, paths.EngineIniPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturnsNullWhenNoClientConfigDirectoryExists()
    {
        using var root = TempDirectory.Create();

        Assert.Null(WuwaGameConfigPathsResolver.ResolveFromGameExecutable(
            Path.Combine(root.Path, "Binaries", "game.exe")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNullForMissingInput(string? exe)
        => Assert.Null(WuwaGameConfigPathsResolver.ResolveFromGameExecutable(exe));

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("jasm-uhd-paths-").FullName;

        public string Combine(params string[] parts)
        {
            var all = new string[parts.Length + 1];
            all[0] = Path;
            parts.CopyTo(all, 1);
            return System.IO.Path.Combine(all);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }

        public static TempDirectory Create() => new();
    }
}
