namespace GIMI_ModManager.Core.GamesService.JsonModels;

internal class JsonGame
{
    public string GameName { get; set; } = string.Empty;
    public string GameShortName { get; set; } = string.Empty;
    public string GameIcon { get; set; } = string.Empty;
    public string RarityName { get; set; } = string.Empty;
    public string GameBananaUrl { get; set; } = string.Empty;
    public string GameModelImporterUrl { get; set; } = string.Empty;
    public string GameModelImporterName { get; set; } = string.Empty;

    public string GameModelImporterShortName { get; set; } = string.Empty;

    public string[] GameModelImporterExeName { get; set; } = [];

    /// <summary>
    /// Optional per-game configuration for the one-click Mod environment setup feature.
    /// When present, the app can auto-download &amp; install the game's Model Importer package.
    /// </summary>
    public JsonModEnv? ModEnv { get; set; }
}

/// <summary>
/// Describes how a game's Model Importer package maps to the shared Mod environment layout.
/// </summary>
internal class JsonModEnv
{
    /// <summary>Id of the package inside the remote version manifest (e.g. "wwmi").</summary>
    public string PackageId { get; set; } = string.Empty;

    /// <summary>Name of the root folder created at the drive root (e.g. "XXMI").</summary>
    public string RootDirName { get; set; } = "XXMI";

    /// <summary>Name of the sub-folder under the root where the game package installs (e.g. "WWMI").</summary>
    public string SubDirName { get; set; } = string.Empty;
}