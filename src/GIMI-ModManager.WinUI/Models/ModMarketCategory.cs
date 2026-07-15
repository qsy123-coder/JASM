using CommunityToolkit.Mvvm.ComponentModel;

namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// Represents a character entry in the mod market sidebar.
/// Each category represents a distinct character from the Supabase mods table.
/// </summary>
public partial class ModMarketCategory : ObservableObject
{
    /// <summary>
    /// Character name (used as key for filtering).
    /// "all" is reserved for the "全部" entry.
    /// </summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Display name for this character.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Number of mods for this character. Updated after data fetch.
    /// </summary>
    [ObservableProperty]
    private int _modCount;

    public ModMarketCategory(string key, string name, int modCount = 0)
    {
        Key = key;
        Name = name;
        _modCount = modCount;
    }

    public static ModMarketCategory CreateAll(int modCount = 0)
        => new("all", "全部", modCount);
}
