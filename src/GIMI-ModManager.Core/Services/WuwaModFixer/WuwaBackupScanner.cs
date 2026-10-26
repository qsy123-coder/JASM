using System.Globalization;
using System.Text.RegularExpressions;

namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>从一个 <c>.BAK</c> 文件名解析出的信息。</summary>
public sealed record WuwaBackupName(string OriginalFileName, string TimestampKey, DateTime Timestamp);

/// <summary>
/// 磁盘上的一个备份文件。
/// <see cref="IsMarker"/> 为 true 表示这是上游用来标记「修复时新增了这个文件」的 0 字节备份，
/// 回滚时要删掉原始文件而不是恢复内容。
/// </summary>
public sealed record WuwaBackupFile(
    string BackupPath,
    string OriginalPath,
    string TimestampKey,
    DateTime Timestamp,
    long SizeBytes,
    bool IsMarker,
    int ModIndex);

/// <summary>同一个时间戳（同一次修复）产生的备份集合。</summary>
public sealed record WuwaBackupGroup(
    string TimestampKey,
    DateTime Timestamp,
    IReadOnlyList<WuwaBackupFile> Files,
    int ModCount,
    long TotalBytes);

/// <summary>扫描结果；<see cref="Warnings"/> 是跳过项（越界、不可访问），用于界面提示而不中断流程。</summary>
public sealed record WuwaBackupScanResult(
    IReadOnlyList<WuwaBackupGroup> Groups,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 扫描当前角色 Mod 目录下的上游备份文件。
///
/// 命名规则来自上游 <c>rollback.rs</c> 的 <c>BAK_RE</c>：
/// <c>&lt;原文件名含扩展名&gt;_&lt;yyyy-MM-dd HH-mm-ss[.fff]&gt;.BAK</c>。
/// 原文件名本身可以带下划线、空格、中文 —— 正则用贪婪前缀，取的是**最后一个**能拼出时间戳的下划线。
/// </summary>
public static partial class WuwaBackupScanner
{
    private const string TimestampFormat = "yyyy-MM-dd HH-mm-ss";
    private const string TimestampFormatWithMilliseconds = "yyyy-MM-dd HH-mm-ss.fff";

    private static readonly string[] TimestampFormats =
    [
        TimestampFormat,
        TimestampFormatWithMilliseconds
    ];

    /// <summary>
    /// 上游的备份正则。大小写不敏感（<c>.bak</c> 也认），其余字符逐字匹配。
    /// </summary>
    [GeneratedRegex(@"^(.*)_(\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}(?:\.\d{3})?)\.BAK$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BackupNameRegex();

    /// <summary>解析文件名；不是备份、或时间戳不合法时返回 false。</summary>
    public static bool TryParseName(string? fileName, out WuwaBackupName name)
    {
        name = new WuwaBackupName(string.Empty, string.Empty, default);

        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var match = BackupNameRegex().Match(fileName);
        if (!match.Success)
            return false;

        var originalFileName = match.Groups[1].Value;
        var timestampText = match.Groups[2].Value;

        if (originalFileName.Length == 0)
            return false;

        if (!DateTime.TryParseExact(timestampText, TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp))
            return false;

        // 统一成带毫秒的键：分组/排序只认这个形状，避免 "…-03" 与 "…-03.000" 被拆成两组。
        var key = timestamp.ToString(TimestampFormatWithMilliseconds, CultureInfo.InvariantCulture);
        name = new WuwaBackupName(originalFileName, key, timestamp);
        return true;
    }

    /// <summary>
    /// 递归扫描各 Mod 根目录。只读元数据；越界/不可访问的项记 warning 后跳过。
    /// </summary>
    public static WuwaBackupScanResult Scan(
        IReadOnlyList<string> modRoots,
        string allowedRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);

        var warnings = new List<string>();
        var files = new List<WuwaBackupFile>();
        var roots = new List<string>();

        for (var index = 0; index < modRoots.Count; index++)
        {
            var root = modRoots[index];
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            if (!WuwaPathGuard.IsSafeTarget(root, allowedRoot))
            {
                warnings.Add($"跳过 Mod（不在角色目录内或包含链接）：{Path.GetFileName(root)}");
                continue;
            }

            roots.Add(root);
        }

        for (var index = 0; index < roots.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = roots[index];

            string[] paths;
            try
            {
                paths = Directory.GetFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
                });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"无法读取 Mod 目录：{Path.GetFileName(root)}（{e.GetType().Name}）");
                continue;
            }

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryParseName(Path.GetFileName(path), out var name))
                    continue;

                if (!WuwaPathGuard.IsSafeTarget(path, allowedRoot))
                {
                    warnings.Add($"跳过越界备份：{Path.GetFileName(path)}");
                    continue;
                }

                long rawLength;
                try
                {
                    rawLength = new FileInfo(path).Length;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"无法读取备份：{Path.GetFileName(path)}");
                    continue;
                }

                var isMarker = rawLength == 0;
                var displaySize = rawLength;

                if (isMarker)
                {
                    var originalPath = Path.Combine(Path.GetDirectoryName(path)!, name.OriginalFileName);
                    try
                    {
                        if (File.Exists(originalPath))
                            displaySize = new FileInfo(originalPath).Length;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        displaySize = 0;
                    }
                }

                var directory = Path.GetDirectoryName(path)!;
                files.Add(new WuwaBackupFile(
                    path,
                    Path.Combine(directory, name.OriginalFileName),
                    name.TimestampKey,
                    name.Timestamp,
                    displaySize,
                    isMarker,
                    index));
            }
        }

        var groups = files
            .GroupBy(file => file.TimestampKey, StringComparer.Ordinal)
            .Select(group => new WuwaBackupGroup(
                group.Key,
                group.First().Timestamp,
                group.OrderByDescending(file => file.BackupPath, StringComparer.OrdinalIgnoreCase).ToList(),
                group.Select(file => file.ModIndex).Distinct().Count(),
                group.Sum(file => file.SizeBytes)))
            .OrderByDescending(group => group.Timestamp)
            .ThenByDescending(group => group.TimestampKey, StringComparer.Ordinal)
            .ToList();

        return new WuwaBackupScanResult(groups, warnings);
    }

    /// <summary>
    /// 把任意形态的时间戳文本归一化成分组键（带毫秒）。回滚计划的目标键来自 UI，
    /// 这里仍接受一次人工输入，避免毫秒有无导致「找不到还原点」。
    /// </summary>
    public static bool TryNormalizeTimestampKey(string? value, out string timestampKey, out DateTime timestamp)
    {
        timestampKey = string.Empty;
        timestamp = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        if (!DateTime.TryParseExact(text, TimestampFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out timestamp))
            return false;

        timestampKey = timestamp.ToString(TimestampFormatWithMilliseconds, CultureInfo.InvariantCulture);
        return true;
    }
}