using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Entities.Mods.FileModels;
using GIMI_ModManager.Core.Entities.Mods.Helpers;
using GIMI_ModManager.Core.Helpers;

namespace GIMI_ModManager.Core.Entities.Mods.Contract;

public record ModSettings
{
    public ModSettings(Guid id, string? customName = null, string? author = null, string? version = null,
        Uri? modUrl = null, Uri? imagePath = null, string? characterSkinOverride = null, string? description = null,
        DateTime? dateAdded = null,
        DateTime? lastChecked = null, Uri? mergedIniPath = null, bool ignoreMergedIni = false,
        Dictionary<string, string>? preferences = null, DateTime? seenAt = null)
    {
        Id = id;
        CustomName = customName;
        Author = author;
        Version = version;
        ModUrl = modUrl;
        ImagePath = imagePath;
        CharacterSkinOverride = characterSkinOverride;
        Description = description;
        DateAdded = dateAdded;
        LastChecked = lastChecked;
        MergedIniPath = mergedIniPath;
        IgnoreMergedIni = ignoreMergedIni;
        _preferences = preferences;
        SeenAt = seenAt;
    }

    public ModSettings DeepCopyWithProperties(NewValue<string?>? customName = null,
        NewValue<string?>? characterSkinOverride = null,
        NewValue<DateTime?>? newLastChecked = null, NewValue<Uri?>? mergedIniPath = null,
        NewValue<bool>? ignoreMergedIni = null,
        NewValue<string?>? author = null, NewValue<Uri?>? modUrl = null, NewValue<Uri?>? imagePath = null,
        NewValue<string?>? description = null, NewValue<DateTime?>? seenAt = null
    )
    {
        return new ModSettings(
            Id,
            customName ?? CustomName,
            author ?? Author,
            Version,
            modUrl ?? ModUrl,
            imagePath ?? ImagePath,
            characterSkinOverride ?? CharacterSkinOverride,
            description ?? Description,
            DateAdded,
            newLastChecked ?? LastChecked,
            mergedIniPath ?? MergedIniPath,
            ignoreMergedIni ?? IgnoreMergedIni,
            _preferences is null ? null : new Dictionary<string, string>(_preferences),
            seenAt ?? SeenAt
        );
    }

    internal ModSettings()
    {
    }

    public Guid Id { get; internal set; }

    public string? CustomName { get; internal set; }

    public string? Author { get; internal set; }

    public string? Version { get; internal set; }

    public Uri? ModUrl { get; internal set; }

    public Uri? ImagePath { get; internal set; }

    public Uri? MergedIniPath { get; internal set; }

    public bool IgnoreMergedIni { get; internal set; }

    public string? CharacterSkinOverride { get; internal set; }
    public string? Description { get; internal set; }

    public DateTime? DateAdded { get; internal set; }

    public DateTime? LastChecked { get; internal set; }

    /// <summary>
    /// 浮窗中键标记的「已看过」时间；<c>null</c> = 没看过。
    ///
    /// 刻意存在 Mod 自己的设置文件里（而不是浮窗的全局名单）：这个状态跟着 Mod 走，
    /// 画廊卡片据此显示角标，换机 / 导出 / 重装也还在。
    /// </summary>
    public DateTime? SeenAt { get; internal set; }

    private Dictionary<string, string>? _preferences;
    public IReadOnlyDictionary<string, string> Preferences => _preferences ??= new Dictionary<string, string>();


    internal static ModSettings FromJsonSkinSettings(ISkinMod? skinMod, JsonModSettings settings)
    {
        return new ModSettings
        {
            Id = SkinModHelpers.StringToGuid(settings.Id),
            CustomName = settings.CustomName,
            Author = settings.Author,
            Version = settings.Version,
            ModUrl = SkinModHelpers.StringUrlToUri(settings.ModUrl),
            ImagePath = skinMod is not null
                ? SkinModHelpers.RelativeModPathToAbsPath(skinMod.FullPath, settings.ImagePath)
                : null,
            CharacterSkinOverride = settings.CharacterSkinOverride,
            Description = settings.Description,
            DateAdded = DateTime.TryParse(settings.DateAdded, out var dateAdded) ? dateAdded : null,
            LastChecked = DateTime.TryParse(settings.LastChecked, out var lastChecked) ? lastChecked : null,
            SeenAt = DateTime.TryParse(settings.SeenAt, out var seenAt) ? seenAt : null,
            MergedIniPath = skinMod is not null
                ? SkinModHelpers.RelativeModPathToAbsPath(skinMod.FullPath, settings.MergedIniPath)
                : null,
            IgnoreMergedIni = settings.MergedIniPath == string.Empty,
            _preferences = settings.Preferences is null
                ? null
                : new Dictionary<string, string>(settings.Preferences)
        };
    }

    internal JsonModSettings ToJsonSkinSettings(ISkinMod skinMod)
    {
        return new JsonModSettings
        {
            Id = Id.ToString(),
            CustomName = CustomName,
            Author = Author,
            Version = Version,
            ModUrl = ModUrl?.ToString(),
            ImagePath = SkinModHelpers.UriPathToModRelativePath(skinMod, ImagePath?.LocalPath),
            CharacterSkinOverride = CharacterSkinOverride,
            Description = Description,
            DateAdded = DateAdded?.ToString(),
            LastChecked = LastChecked?.ToString(),
            SeenAt = SeenAt?.ToString(),
            MergedIniPath = IgnoreMergedIni
                ? ""
                : SkinModHelpers.UriPathToModRelativePath(skinMod, MergedIniPath?.LocalPath),
            Preferences = Preferences.Count == 0 ? null : new Dictionary<string, string>(Preferences)
        };
    }


    public bool SettingsEquals(ModSettings? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (ReferenceEquals(other, null)) return false;

        if (Id != other.Id) return false;
        if (CustomName != other.CustomName) return false;
        if (Author != other.Author) return false;
        if (Version != other.Version) return false;
        if (ModUrl != other.ModUrl) return false;
        if (ImagePath != other.ImagePath) return false;
        if (CharacterSkinOverride != other.CharacterSkinOverride) return false;
        if (MergedIniPath != other.MergedIniPath) return false;
        if (IgnoreMergedIni != other.IgnoreMergedIni) return false;
        if (SeenAt != other.SeenAt) return false;

        return true;
    }

    internal void SetPreferences(Dictionary<string, string>? preferences)
    {
        _preferences = preferences;
    }
}