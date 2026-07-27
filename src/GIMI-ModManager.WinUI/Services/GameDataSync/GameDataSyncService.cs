using System.IO.Compression;
using CommunityToolkit.Mvvm.Messaging;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.ViewModels.Messages;
using Newtonsoft.Json;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.GameDataSync;

public enum SyncResult
{
    Success,
    AlreadyUpToDate,
    Failed,
    NoReleaseFound
}

public class GameDataSyncService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly IGameService _gameService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly NotificationManager _notificationManager;

    private const string GitHubReleasesUrl = "https://api.github.com/repos/Jorixon/JASM/releases";
    private const string DataVersionFileName = ".dataversion";

    public GameDataSyncService(
        IHttpClientFactory httpClientFactory,
        ILogger logger,
        IGameService gameService,
        ILocalSettingsService localSettingsService,
        NotificationManager notificationManager)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _gameService = gameService;
        _localSettingsService = localSettingsService;
        _notificationManager = notificationManager;
    }

    /// <summary>
    /// Check GitHub Releases for new game data and sync if available.
    /// </summary>
    /// <param name="game">The game to sync data for.</param>
    /// <param name="isManual">If true, show Toast notifications on success/failure. If false, fail silently.</param>
    /// <returns>The result of the sync operation.</returns>
    public async Task<SyncResult> CheckAndSyncAsync(SupportedGames game, bool isManual = false)
    {
        try
        {
            _logger.Information("Starting game data sync check for {Game}", game);

            // 1. Get latest release info from GitHub
            var (downloadUrl, newVersion) = await GetLatestDataAssetAsync(game).ConfigureAwait(false);
            if (downloadUrl is null || newVersion is null)
            {
                _logger.Information("No data release found for {Game}", game);
                return SyncResult.NoReleaseFound;
            }

            // 2. Compare with local version
            var currentVersion = GetCurrentDataVersion(game);
            if (currentVersion is not null && new Version(currentVersion) >= new Version(newVersion))
            {
                _logger.Information("Game data for {Game} is already up to date (local: {Local}, remote: {Remote})",
                    game, currentVersion, newVersion);
                return SyncResult.AlreadyUpToDate;
            }

            // 3. Download ZIP
            _logger.Information("Downloading game data ZIP for {Game} v{Version}", game, newVersion);
            var tempZipPath = Path.Combine(Path.GetTempPath(), $"jasm_data_{game}_{Guid.NewGuid()}.zip");
            await DownloadFileAsync(downloadUrl, tempZipPath).ConfigureAwait(false);

            // 4. Determine target directory
            var gameName = game.ToString();
            var targetDir = Path.Combine(App.ASSET_DIR, "Games", gameName);

            // 5. Backup existing data
            var backupDir = Path.Combine(App.ASSET_DIR, "Games", $"{gameName}.backup.{DateTime.Now:yyyyMMddHHmmss}");
            try
            {
                if (Directory.Exists(targetDir))
                    BackupDirectory(targetDir, backupDir);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to backup game data for {Game}, proceeding anyway", game);
            }

            // 6. Extract ZIP (with Zip Slip protection)
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"jasm_extract_{game}_{Guid.NewGuid()}");
            try
            {
                ExtractZipSafe(tempZipPath, tempExtractDir);

                // 7. Copy extracted files to target directory (strip root folder if present)
                CopyExtractedToTarget(tempExtractDir, targetDir);

                // 8. Write new version
                var versionFilePath = Path.Combine(targetDir, DataVersionFileName);
                await File.WriteAllTextAsync(versionFilePath, newVersion).ConfigureAwait(false);

                // 9. Update settings
                var settings = await _localSettingsService
                    .ReadOrCreateSettingAsync<GameDataSyncSettings>(GameDataSyncSettings.Key).ConfigureAwait(false);
                settings.LastSyncTimeUtc = DateTime.UtcNow;
                settings.CurrentDataVersion = newVersion;
                await _localSettingsService
                    .SaveSettingAsync(GameDataSyncSettings.Key, settings).ConfigureAwait(false);

                // 10. Re-initialize GameService to load new data
                await _gameService.ReinitializeAsync().ConfigureAwait(false);

                // 11. Notify ViewModels
                WeakReferenceMessenger.Default.Send(new GameDataSyncCompletedMessage(this));

                // 12. Show success notification (manual mode only)
                if (isManual)
                {
                    _notificationManager.ShowNotification(
                        "游戏数据同步",
                        $"游戏数据已更新至 v{newVersion}",
                        TimeSpan.FromSeconds(4));
                }

                _logger.Information("Game data sync completed for {Game} v{Version}", game, newVersion);
                return SyncResult.Success;
            }
            finally
            {
                // Cleanup temp files
                SafeDeleteDirectory(tempExtractDir);
                SafeDeleteFile(tempZipPath);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Game data sync failed for {Game}", game);

            if (isManual)
            {
                _notificationManager.ShowNotification(
                    "游戏数据同步失败",
                    ex.Message,
                    TimeSpan.FromSeconds(6));
            }

            return SyncResult.Failed;
        }
    }

    public string? GetCurrentDataVersion(SupportedGames game)
    {
        var versionFilePath = Path.Combine(App.ASSET_DIR, "Games", game.ToString(), DataVersionFileName);
        if (!File.Exists(versionFilePath))
            return null;

        return File.ReadAllText(versionFilePath).Trim();
    }

    public DateTime? GetLastSyncTime(SupportedGames game)
    {
        var settings = _localSettingsService
            .ReadSetting<GameDataSyncSettings>(GameDataSyncSettings.Key);
        return settings?.LastSyncTimeUtc;
    }

    /// <summary>
    /// Query GitHub Releases API for the latest data ZIP asset for the given game.
    /// </summary>
    private async Task<(string? downloadUrl, string? version)> GetLatestDataAssetAsync(SupportedGames game)
    {
        var client = _httpClientFactory.CreateClient("GameDataSync");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        var response = await client.GetAsync($"{GitHubReleasesUrl}?per_page=5").ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.Warning("Failed to fetch GitHub releases: {StatusCode} {Reason}",
                response.StatusCode, response.ReasonPhrase);
            return (null, null);
        }

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var releases = JsonConvert.DeserializeObject<GitHubRelease[]>(content) ?? Array.Empty<GitHubRelease>();

        var gameName = game.ToString();
        var dataZipPrefix = $"{gameName}-data-v";

        foreach (var release in releases)
        {
            if (release.prerelease) continue;
            if (release.assets is null) continue;

            foreach (var asset in release.assets)
            {
                if (string.IsNullOrEmpty(asset.name)) continue;
                if (!asset.name.StartsWith(dataZipPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!asset.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                var version = asset.name[dataZipPrefix.Length..];
                if (version.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    version = version[..^4];
                return (asset.browser_download_url, version);
            }
        }

        return (null, null);
    }

    private async Task DownloadFileAsync(string url, string destinationPath)
    {
        var client = _httpClientFactory.CreateClient("GameDataSync");
        using var response = await client.GetAsync(url).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs).ConfigureAwait(false);
    }

    /// <summary>
    /// Extract ZIP with protection against Zip Slip path traversal attacks.
    /// </summary>
    private void ExtractZipSafe(string zipPath, string extractDir)
    {
        Directory.CreateDirectory(extractDir);
        var normalizedExtractDir = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && string.IsNullOrEmpty(entry.FullName))
                continue; // Skip directory-only entries

            var destPath = Path.GetFullPath(Path.Combine(extractDir, entry.FullName));

            // Zip Slip check
            if (!destPath.StartsWith(normalizedExtractDir, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("Skipping potentially malicious ZIP entry: {Entry}", entry.FullName);
                continue;
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                // Directory entry
                Directory.CreateDirectory(destPath);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }
    }

    /// <summary>
    /// Copy extracted files to the target directory. If the ZIP has a single root folder
    /// (e.g., "Genshin-data-v2.1.0/"), strip it and copy contents directly.
    /// </summary>
    private void CopyExtractedToTarget(string extractDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        // Check if the ZIP has a single root directory (common for GitHub archives)
        var entries = Directory.GetFileSystemEntries(extractDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            // Strip the root folder
            extractDir = entries[0];
        }

        // Copy all files and directories
        foreach (var dirPath in Directory.GetDirectories(extractDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(extractDir, dirPath);
            var destDir = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(destDir);
        }

        foreach (var filePath in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(extractDir, filePath);
            var destFile = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(filePath, destFile, overwrite: true);
        }
    }

    private void BackupDirectory(string sourceDir, string backupDir)
    {
        if (Directory.Exists(backupDir))
            Directory.Delete(backupDir, true);

        Directory.CreateDirectory(backupDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            // Skip backup directories themselves
            if (file.Contains(".backup.", StringComparison.OrdinalIgnoreCase))
                continue;

            var relative = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(backupDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    private static void SafeDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch { /* ignore cleanup errors */ }
    }

    private static void SafeDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* ignore cleanup errors */ }
    }

    // ReSharper disable ClassNeverInstantiated.Local
    private class GitHubRelease
    {
        public string? tag_name;
        public bool prerelease;
        public GitHubAsset[]? assets;
    }

    private class GitHubAsset
    {
        public string? name;
        public string? browser_download_url;
    }
    // ReSharper restore ClassNeverInstantiated.Local
}
