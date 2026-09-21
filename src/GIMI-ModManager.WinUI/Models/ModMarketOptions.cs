namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// Configuration options for the Supabase connection used by ModMarketService.
/// Bound from the "Supabase" section in appsettings.json.
/// </summary>
public class ModMarketOptions
{
    public const string SectionName = "Supabase";

    public string Url { get; set; } = string.Empty;
    public string AnonKey { get; set; } = string.Empty;

    /// <summary>
    /// COS 上的兜底快照（gzip 的 JSON 数组），与 WaveMod 站点用的是同一个对象。
    /// Supabase 出口配额超限时，网关会对 REST / Auth / Storage 一律回 402，
    /// 而 COS 不受影响 —— 这条路径是那时唯一还能拿到市场数据的地方。
    /// </summary>
    public string SnapshotUrl { get; set; } = string.Empty;
}
