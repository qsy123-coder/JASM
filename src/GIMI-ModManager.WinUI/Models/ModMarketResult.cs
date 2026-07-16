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

    public string DebugSummary =>
        $"URL: {RequestUrl ?? "?"}\n" +
        $"Content-Range: {ContentRange ?? "missing"}\n" +
        $"Raw resp rows: {RawResponseCount}  Deserialized: {Mods.Count}  Total(count=exact): {TotalCount}";
}
