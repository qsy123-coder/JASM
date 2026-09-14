using System.Globalization;
using GIMI_ModManager.WinUI.Models.Options;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModEnv;

/// <summary>
/// A snapshot of the package files that were in place before a version switch, so the user can go back.
/// </summary>
/// <remarks>
/// A snapshot folder holds ONLY the package files — no index or manifest of its own. That is deliberate:
/// it makes the folder directly copyable back onto the XXMI root by the installer's existing copy routine
/// (which knows about the elevation fallback), with no bookkeeping file to filter out first. The version
/// and timestamp live in the folder name instead.
/// </remarks>
public record ModEnvBackupInfo
{
    /// <summary>Version recorded in the folder name, or <see cref="UnknownVersion"/>.</summary>
    public string Version { get; init; } = UnknownVersion;

    /// <summary>Absolute path of the snapshot folder.</summary>
    public string Folder { get; init; } = string.Empty;

    /// <summary>Local time the snapshot was taken.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>File names captured in this snapshot.</summary>
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    /// <summary>Recorded when the installed version was unknown at snapshot time.</summary>
    public const string UnknownVersion = "unknown";

    /// <summary>Label for the restore picker, e.g. "v1.1.7（备份于 2026-09-14 16:08）".</summary>
    public string DisplayName => Version == UnknownVersion
        ? $"未知版本（备份于 {CreatedAt:yyyy-MM-dd HH:mm}）"
        : $"v{Version}（备份于 {CreatedAt:yyyy-MM-dd HH:mm}）";
}

/// <summary>
/// Stores and prunes per-version snapshots of the base package files under
/// <c>%LOCALAPPDATA%\JASM\ModEnvBackups\</c>.
/// </summary>
public class ModEnvBackupService
{
    private const string FolderPrefix = "xxmi";
    private const string TimestampFormat = "yyyyMMdd-HHmmss";

    private readonly IOptions<ModEnvSetupOptions> _options;
    private readonly ILogger _logger;

    public ModEnvBackupService(IOptions<ModEnvSetupOptions> options, ILogger logger)
    {
        _options = options;
        _logger = logger.ForContext<ModEnvBackupService>();
    }

    /// <summary>Root of the snapshot store, under the per-user JASM data folder.</summary>
    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JASM", "ModEnvBackups");

    /// <summary>
    /// Snapshots <paramref name="fileNames"/> from <paramref name="rootFolder"/> into a new folder.
    /// </summary>
    /// <returns>The snapshot, or null when no source file was present (a fresh install — nothing to keep).</returns>
    /// <remarks>
    /// Throws on IO failure on purpose. Callers must abort the version switch rather than overwrite files
    /// with no way back, so "could not back up" is a hard stop, not a warning.
    /// </remarks>
    public async Task<ModEnvBackupInfo?> BackupAsync(string rootFolder, string? version,
        IReadOnlyCollection<string> fileNames, CancellationToken ct = default)
    {
        var present = fileNames.Where(name => File.Exists(Path.Combine(rootFolder, name))).ToList();
        if (present.Count == 0)
        {
            _logger.Information("No base package files present in {Root}; nothing to snapshot", rootFolder);
            return null;
        }

        var createdAt = DateTime.Now;
        var normalizedVersion = string.IsNullOrWhiteSpace(version)
            ? ModEnvBackupInfo.UnknownVersion
            : version.Trim();

        var folder = Path.Combine(BackupRoot,
            $"{FolderPrefix}-{normalizedVersion}-{createdAt.ToString(TimestampFormat, CultureInfo.InvariantCulture)}");

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                foreach (var name in present)
                    File.Copy(Path.Combine(rootFolder, name), Path.Combine(folder, name), overwrite: true);
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // A half-written snapshot must never be listed as restorable, so remove it before bubbling up.
            TryDelete(folder);
            throw;
        }

        _logger.Information("Snapshot of {Count} file(s) for version {Version} written to {Folder}",
            present.Count, normalizedVersion, folder);

        Prune();

        return new ModEnvBackupInfo
        {
            Folder = folder,
            Version = normalizedVersion,
            CreatedAt = createdAt,
            Files = present
        };
    }

    /// <summary>All snapshots, newest first. Folders that don't match the naming scheme are ignored.</summary>
    public IReadOnlyList<ModEnvBackupInfo> List()
    {
        if (!Directory.Exists(BackupRoot))
            return Array.Empty<ModEnvBackupInfo>();

        var result = new List<ModEnvBackupInfo>();
        foreach (var folder in Directory.EnumerateDirectories(BackupRoot))
        {
            if (TryParseFolder(folder, out var info))
                result.Add(info);
            else
                _logger.Warning("Ignoring unrecognised ModEnv backup folder {Folder}", folder);
        }

        return result.OrderByDescending(info => info.CreatedAt).ToList();
    }

    /// <summary>Deletes the oldest snapshots beyond the configured keep-count. Best-effort.</summary>
    public void Prune()
    {
        var keepCount = _options.Value.KeepBackupCount;
        if (keepCount <= 0)
            return;

        foreach (var stale in List().Skip(keepCount))
        {
            _logger.Information("Pruning old ModEnv backup {Folder}", stale.Folder);
            TryDelete(stale.Folder);
        }
    }

    /// <summary>
    /// Parses <c>xxmi-&lt;version&gt;-&lt;yyyyMMdd&gt;-&lt;HHmmss&gt;</c>. A version never contains a dash, so the
    /// segment count is unambiguous and a plain split is safe.
    /// </summary>
    private static bool TryParseFolder(string folder, out ModEnvBackupInfo info)
    {
        info = null!;

        var parts = Path.GetFileName(folder).Split('-');
        if (parts.Length != 4 ||
            !string.Equals(parts[0], FolderPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!DateTime.TryParseExact(parts[2] + parts[3], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var createdAt))
            return false;

        info = new ModEnvBackupInfo
        {
            Folder = folder,
            Version = parts[1],
            CreatedAt = createdAt,
            Files = Directory.EnumerateFiles(folder)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToList()
        };

        return true;
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to delete ModEnv backup folder {Folder}", folder);
        }
    }
}
