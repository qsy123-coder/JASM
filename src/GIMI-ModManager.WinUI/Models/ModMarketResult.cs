namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// Result from ModMarketService.GetModsAsync, carrying both the mod list
/// and debug diagnostics for the on-screen overlay.
/// </summary>
public class ModMarketResult
{
    public IReadOnlyList<ModMarketMod> Mods { get; init; } = [];
    public int TotalCount { get; init; }
    public int RawResponseCount { get; init; }
    public string? ContentRange { get; init; }
    public string? RequestUrl { get; init; }
    public string[] DroppedEntries { get; init; } = [];
    public bool UsedCountFallback { get; init; }

    /// <summary>
    /// true = 这批数据来自 COS 兜底快照（Supabase 网关不可用时的降级路径），不是实时数据。
    /// UI 据此显示「离线快照模式」横幅。
    /// </summary>
    public bool IsFromSnapshot { get; init; }

    /// <summary>快照的生成时间（取 COS 的 Last-Modified）。仅 <see cref="IsFromSnapshot"/> 时有意义。</summary>
    public DateTimeOffset? SnapshotGeneratedAt { get; init; }

    /// <summary>
    /// 实时查询与快照都失败时的原因；任一路径成功（含成功降级）时为 null。
    /// 有了它，UI 才能把「加载失败」和「没有找到 Mod」区分开 —— 之前两者都是整片空白。
    /// </summary>
    public string? ErrorMessage { get; init; }
}
