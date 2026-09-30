using GIMI_ModManager.Core.ModStore;

namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// 详情抽屉里文件列表的一行，由 Core 的 <see cref="ModStoreFile"/> 映射而来。
///
/// 只做「显示」这一件事：把字节数、时间戳、下载数变成给人看的短文案。
/// 数值本身（<see cref="FileSize"/> 等）仍然留着 —— 后面「一键部署」要按大小估算耗时。
/// </summary>
public sealed class ModStoreFileItem
{
    /// <summary>GameBanana 文件 id，下载时要用（<c>GbModFileId</c> 就是它）。</summary>
    public string FileId { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    /// <summary>文件级版本号，缺失时已经在 Core 退回 mod 级；两处都没有则为 null。</summary>
    public string? Version { get; init; }

    /// <summary>作者写的文件说明（如「这是更新包」「这是无图版」），已洗成纯文本。</summary>
    public string? Description { get; init; }

    public bool IsArchived { get; init; }

    public string? Md5Checksum { get; init; }

    public long? FileSize { get; init; }

    public int? DownloadCount { get; init; }

    public DateTimeOffset? DateAdded { get; init; }

    /// <summary>
    /// 文件行下面那行小字：<c>v1.2 · 385.3 MB · 2026-03-11 · 799 次下载</c>。
    ///
    /// 拼成**一个**字符串而不是几列分开绑：接口这几项都可能缺（尤其版本号和大小），
    /// 分开绑会在行里留下「空的一格 + 间隔」，看着像加载失败。缺的项在这里就被筛掉了。
    /// </summary>
    public string MetaText => string.Join(" · ",
        new[] { VersionText, SizeText, DateText, DownloadsText }.Where(part => part.Length > 0));

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    private string SizeText => FileSize is { } size ? FormatSize(size) : string.Empty;

    private string VersionText => string.IsNullOrWhiteSpace(Version) ? string.Empty : $"v{Version}";

    /// <summary>用日期而不是相对时间：文件之间经常只差几小时，「1天前/1天前」分不出先后。</summary>
    private string DateText => DateAdded?.ToLocalTime().ToString("yyyy-MM-dd") ?? string.Empty;

    private string DownloadsText => DownloadCount is { } count ? $"{count} 次下载" : string.Empty;

    public static ModStoreFileItem FromFile(ModStoreFile file)
    {
        return new ModStoreFileItem
        {
            FileId = file.FileId.ToString(),
            FileName = file.FileName,
            Version = file.Version,
            Description = file.Description,
            IsArchived = file.IsArchived,
            Md5Checksum = file.Md5Checksum,
            FileSize = file.FileSize,
            DownloadCount = file.DownloadCount,
            DateAdded = file.DateAdded
        };
    }

    /// <summary>
    /// 二进制单位（1 KB = 1024 B）—— 与 Windows 资源管理器同一口径，
    /// 免得用户对着「你以为的 1 GB」和实际下载量反复对账。
    /// </summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 0 => string.Empty,
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}