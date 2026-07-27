using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Services.GameDataSync;

public class GameDataSyncSettings
{
    [JsonIgnore] public const string Key = "GameDataSyncSettings";

    public bool AutoSyncOnStartup { get; set; } = true;
    public DateTime? LastSyncTimeUtc { get; set; }
    public string? CurrentDataVersion { get; set; }
}
