using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>回滚页的一行还原点。只做展示，执行时用 <see cref="TimestampKey"/> 回查分组。</summary>
internal sealed class OverlayRestorePointItem
{
    public OverlayRestorePointItem(WuwaBackupGroup group)
    {
        TimestampKey = group.TimestampKey;
        DisplayTime = group.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
        FileCount = group.Files.Count;
        ModCount = group.ModCount;
        TotalBytes = group.TotalBytes;
    }

    public string TimestampKey { get; }
    public string DisplayTime { get; }
    public int FileCount { get; }
    public int ModCount { get; }
    public long TotalBytes { get; }

    public string SummaryText => $"{FileCount} 个文件 · {ModCount} 个 Mod · {FormatSize(TotalBytes)}";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";

        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}