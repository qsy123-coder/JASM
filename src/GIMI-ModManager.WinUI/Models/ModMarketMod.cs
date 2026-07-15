using System.Text.Json.Serialization;

namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// Represents a mod listing from the Supabase mods table.
/// Maps to the "mods" table in the remote Supabase database.
/// </summary>
public class ModMarketMod
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("character")]
    public string Character { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("game_version")]
    public string GameVersion { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    [JsonPropertyName("video_url")]
    public string? VideoUrl { get; set; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("nsfw")]
    public bool Nsfw { get; set; }

    [JsonPropertyName("mod_author_url")]
    public string? ModAuthorUrl { get; set; }

    [JsonPropertyName("xxmi_install_guide")]
    public string? XxmiInstallGuide { get; set; }

    [JsonPropertyName("views")]
    public int Views { get; set; }

    [JsonPropertyName("favorites_count")]
    public int FavoritesCount { get; set; }

    [JsonPropertyName("likes_count")]
    public int LikesCount { get; set; }

    [JsonPropertyName("comments_count")]
    public int CommentsCount { get; set; }

    [JsonPropertyName("is_published")]
    public bool IsPublished { get; set; }

    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("created_by")]
    public Guid CreatedBy { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("rating_count")]
    public int RatingCount { get; set; }

    [JsonPropertyName("rating_average")]
    public double RatingAverage { get; set; }

    [JsonPropertyName("downloads_count")]
    public int DownloadsCount { get; set; }

    [JsonPropertyName("game_key")]
    public string GameKey { get; set; } = string.Empty;

    [JsonPropertyName("drive_links")]
    public List<DriveLinkEntry>? DriveLinks { get; set; }

    /// <summary>
    /// Gets the first preview image URL, or null if no images are available.
    /// </summary>
    [JsonIgnore]
    public string? PreviewImageUrl => Images is { Count: > 0 } ? Images[0] : null;

    /// <summary>
    /// Gets a display-friendly relative time string (e.g., "3天前", "2 hours ago").
    /// This is computed on the client side based on CreatedAt.
    /// </summary>
    [JsonIgnore]
    public string RelativeTime => GetRelativeTime(CreatedAt);

    private static string GetRelativeTime(DateTime dateTime)
    {
        var span = DateTime.UtcNow - dateTime;

        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}小时前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}天前";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}个月前";
        return $"{(int)(span.TotalDays / 365)}年前";
    }
}

/// <summary>
/// Represents a single drive link entry in the drive_links JSON array.
/// </summary>
public class DriveLinkEntry
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}
