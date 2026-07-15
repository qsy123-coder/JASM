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
}
