using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.GamesService.Models;

namespace JASM.Tests;

/// <summary>
/// Validates that the per-game "ModEnv" section in game.json maps correctly onto
/// <see cref="GameInfo.ModEnv"/>, which drives the one-click Mod environment setup feature.
/// </summary>
public class ModEnvConfigTests
{
    private const string GameAssetsPath = @"..\..\..\..\GIMI-ModManager.WinUI\Assets\Games";

    private static void StageGameJson(string gameName)
    {
        var source = Path.Combine(GameAssetsPath, gameName, "game.json");
        if (!File.Exists(source))
            throw new FileNotFoundException("game.json not found.", source);

        var targetDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Games", gameName);
        Directory.CreateDirectory(targetDir);
        File.Copy(source, Path.Combine(targetDir, "game.json"), overwrite: true);
    }

    [Fact]
    public async Task WuWa_GameJson_HasModEnv_AndMapsToGameInfo()
    {
        StageGameJson(nameof(SupportedGames.WuWa));
        var gameInfo = await GameService.GetGameInfoAsync(SupportedGames.WuWa);

        Assert.NotNull(gameInfo);
        Assert.NotNull(gameInfo.ModEnv);
        Assert.Equal("wwmi", gameInfo.ModEnv.PackageId);
        Assert.Equal("XXMI", gameInfo.ModEnv.RootDirName);
        Assert.Equal("WWMI", gameInfo.ModEnv.SubDirName);
    }

    /// <summary>Games without a ModEnv section should map to null (feature is opt-in per game).</summary>
    [Theory]
    [MemberData(nameof(GetGameFolderNamesWithoutModEnv))]
    public async Task Games_WithoutModEnv_MapToNull(string gameName)
    {
        StageGameJson(gameName);

        var gameInfo = await GameService.GetGameInfoAsync(Enum.Parse<SupportedGames>(gameName));

        Assert.NotNull(gameInfo);
        Assert.Null(gameInfo.ModEnv);
    }

    public static IEnumerable<object[]> GetGameFolderNamesWithoutModEnv
    {
        get
        {
            foreach (var gameDir in new DirectoryInfo(GameAssetsPath).EnumerateDirectories())
            {
                var gameJsonPath = Path.Combine(gameDir.FullName, "game.json");
                if (!File.Exists(gameJsonPath)) continue;

                var json = File.ReadAllText(gameJsonPath);
                if (!json.Contains("\"ModEnv\""))
                    yield return [gameDir.Name];
            }
        }
    }
}
