using GIMI_ModManager.Core.GamesService.JsonModels;

namespace GIMI_ModManager.Core.GamesService.Models;

public record GameInfo
{
    internal GameInfo(JsonGame jsonGame, DirectoryInfo assetsDirectoryInfo)
    {
        GameName = jsonGame.GameName?.Trim() ?? "";
        GameShortName = jsonGame.GameShortName?.Trim() ?? "";
        GameIcon = Path.Combine(assetsDirectoryInfo.FullName, "Images", jsonGame.GameIcon?.Trim() ?? "Start_Game.png");
        GameBananaUrl = Uri.TryCreate(jsonGame.GameBananaUrl, UriKind.Absolute, out var gameBananaUrl)
            ? gameBananaUrl
            : new Uri("https://gamebanana.com/");
        GameModelImporterUrl =
            Uri.TryCreate(jsonGame.GameModelImporterUrl, UriKind.Absolute, out var gameModelImporterUrl)
                ? gameModelImporterUrl
                : new Uri("https://github.com/SilentNightSound");

        GameModelImporterName = jsonGame.GameModelImporterName ?? "";
        GameModelImporterShortName = jsonGame.GameModelImporterShortName ?? "";
        GameModelImporterExeNames = jsonGame.GameModelImporterExeName.ToList() ?? [];

        ModEnv = jsonGame.ModEnv is null
            ? null
            : new ModEnvInfo(jsonGame.ModEnv.PackageId?.Trim() ?? "", jsonGame.ModEnv.RootDirName?.Trim() ?? "XXMI",
                jsonGame.ModEnv.SubDirName?.Trim() ?? "");
    }

    public string GameName { get; }
    public string GameShortName { get; }
    public string GameIcon { get; }
    public Uri GameBananaUrl { get; }
    public Uri GameModelImporterUrl { get; }
    public string GameModelImporterName { get; }
    public string GameModelImporterShortName { get; }

    public IReadOnlyList<string> GameModelImporterExeNames { get; }

    /// <summary>Optional per-game configuration for the one-click Mod environment setup feature.</summary>
    public ModEnvInfo? ModEnv { get; }
}

/// <summary>
/// Immutable per-game configuration for the Mod environment setup feature,
/// mirrors <see cref="GIMI_ModManager.Core.GamesService.JsonModels.JsonModEnv"/>.
/// </summary>
public record ModEnvInfo(string PackageId, string RootDirName, string SubDirName);