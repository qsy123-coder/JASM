using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Settings;

/// <summary>
/// 油光功能的全局设置。
///
/// 强度对应 KatFXMatcap 的 <c>$\katfxmatcap\oilstrength</c>：本体只有一份实例，
/// 同一时间所有启用了油光的 Mod 共用这个值，所以这里存的是全局设置，而不是每个 Mod 一份。
/// </summary>
public class OilGlossSettings
{
    [JsonIgnore] public const string Key = "OilGlossSettings";

    /// <summary>油光强度 0.0–2.0（默认 0.7，与本体 ini 的默认值一致）。</summary>
    public double Strength { get; set; } = 0.7;
}