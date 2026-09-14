using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Options;

public class ModManagerOptions
{
    [JsonIgnore] public const string Section = "ModManagerOptions";
    public string? GimiRootFolderPath { get; set; }
    public string? ModsFolderPath { get; set; }
    public string? UnloadedModsFolderPath { get; set; }

    /// <summary>
    /// XXMI root picked on the startup page (null = "&lt;game drive&gt;\XXMI"). Kept so a later one-click run
    /// targets the same place instead of installing a second copy at the default location.
    /// </summary>
    public string? XxmiRootFolderPath { get; set; }
    public bool CharacterSkinsAsCharacters { get; set; }
}