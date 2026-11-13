using System.IO.Compression;
using System.Security.Cryptography;
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

/// <summary>同步进度：检查清单时不确定，下载时按字节百分比。</summary>
public sealed record GameDataSyncProgress(bool IsIndeterminate, double Percentage, string? Message = null);

public class GameDataSyncService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly IGameService _gameService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly NotificationManager _notificationManager;

    // 同一时间只允许一个同步流程：并发 ReinitializeAsync 会把 GameService 弄成重复内部名
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    // 游戏数据包与清单都放腾讯云 COS（国内直连，不走 GitHub）
    private const string CosBaseUrl = "https://jasm-modenv-1327973389.cos.ap-guangzhou.myqcloud.com";
    private const string DataManifestUrl = CosBaseUrl + "/gamedata/manifest.json";
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
    /// 检查 COS 上的游戏数据清单，有新版本就下载并安装。
    /// </summary>
    /// <param name="game">The game to sync data for.</param>
    /// <param name="isManual">If true, show Toast notifications on success/failure. If false, fail silently.</param>
    /// <param name="progress">下载进度回调（百分比）。</param>
    public async Task<SyncResult> CheckAndSyncAsync(SupportedGames game, bool isManual = false,
        IProgress<GameDataSyncProgress>? progress = null)
    {
        await _syncLock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await CheckAndSyncCoreAsync(game, isManual, progress).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<SyncResult> CheckAndSyncCoreAsync(SupportedGames game, bool isManual,
        IProgress<GameDataSyncProgress>? progress)
    {
        try
        {
            _logger.Information("Starting game data sync check for {Game}", game);
            progress?.Report(new GameDataSyncProgress(true, 0, "检查更新..."));

            // 1. 拉 COS 清单（带时间戳避免 CDN/浏览器缓存）
            var manifest = await DownloadManifestAsync().ConfigureAwait(false);
            if (manifest is null)
            {
                _logger.Warning("No data manifest found for {Game}", game);
                return SyncResult.NoReleaseFound;
            }

            var gameName = game.ToString();
            if (!manifest.Games.TryGetValue(gameName, out var entry) || entry is null ||
                string.IsNullOrWhiteSpace(entry.Url))
            {
                _logger.Information("Manifest has no data entry for {Game}", game);
                return SyncResult.NoReleaseFound;
            }

            // 2. 版本比较
            var currentVersion = GetCurrentDataVersion(game);
            if (currentVersion is not null && new Version(currentVersion) >= new Version(entry.Version))
            {
                _logger.Information("Game data for {Game} is already up to date (local: {Local}, remote: {Remote})",
                    game, currentVersion, entry.Version);
                progress?.Report(new GameDataSyncProgress(false, 100, "已是最新版本"));
                return SyncResult.AlreadyUpToDate;
            }

            // 3. 下载 ZIP（流式读取，实时上报进度到进度条）
            _logger.Information("Downloading game data ZIP for {Game} v{Version} from {Url}",
                game, entry.Version, entry.Url);
            var tempZipPath = Path.Combine(Path.GetTempPath(), $"jasm_data_{game}_{Guid.NewGuid()}.zip");
            await DownloadFileWithProgressAsync(entry, tempZipPath, progress).ConfigureAwait(false);

            // 4. 目标目录
            var targetDir = Path.Combine(App.ASSET_DIR, "Games", gameName);

            // 5. 备份现有数据
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

            // 6. 解压并覆盖（Zip Slip 防护）
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"jasm_extract_{game}_{Guid.NewGuid()}");
            try
            {
                progress?.Report(new GameDataSyncProgress(true, 100, "正在解压..."));
                ExtractZipSafe(tempZipPath, tempExtractDir);
                CopyExtractedToTarget(tempExtractDir, targetDir);

                // 7. 写版本号
                var versionFilePath = Path.Combine(targetDir, DataVersionFileName);
                await File.WriteAllTextAsync(versionFilePath, entry.Version).ConfigureAwait(false);

                // 8. 更新设置
                var settings = await _localSettingsService
                    .ReadOrCreateSettingAsync<GameDataSyncSettings>(GameDataSyncSettings.Key).ConfigureAwait(false);
                settings.LastSyncTimeUtc = DateTime.UtcNow;
                settings.CurrentDataVersion = entry.Version;
                await _localSettingsService
                    .SaveSettingAsync(GameDataSyncSettings.Key, settings).ConfigureAwait(false);

                // 9. 重新加载 GameService
                await _gameService.ReinitializeAsync().ConfigureAwait(false);

                // 10. 通知 ViewModel
                WeakReferenceMessenger.Default.Send(new GameDataSyncCompletedMessage(this));

                if (isManual)
                {
                    _notificationManager.ShowNotification(
                        "游戏数据同步",
                        $"游戏数据已更新至 v{entry.Version}",
                        TimeSpan.FromSeconds(4));
                }

                _logger.Information("Game data sync completed for {Game} v{Version}", game, entry.Version);
                progress?.Report(new GameDataSyncProgress(false, 100, "同步完成"));
                return SyncResult.Success;
            }
            finally
            {
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

            progress?.Report(new GameDataSyncProgress(false, 0, "同步失败"));
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
    /// 从 COS 拉游戏数据清单；带上时间戳查询参数，避免拿到缓存。
    /// </summary>
    private async Task<DataManifest?> DownloadManifestAsync()
    {
        var client = _httpClientFactory.CreateClient("GameDataSync");
        var url = $"{DataManifestUrl}?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var json = await client.GetStringAsync(url).ConfigureAwait(false);
        var manifest = JsonConvert.DeserializeObject<DataManifest>(json);

        if (manifest?.Games is null || manifest.Games.Count == 0)
        {
            _logger.Warning("Game data manifest is empty");
            return null;
        }

        _logger.Information("Downloaded game data manifest (schema {Schema}) with {Count} games",
            manifest.SchemaVersion, manifest.Games.Count);
        return manifest;
    }

    /// <summary>
    /// 流式下载到临时文件，按字节上报进度，并校验 SHA-256。
    /// </summary>
    private async Task DownloadFileWithProgressAsync(GameDataEntry entry, string destinationPath,
        IProgress<GameDataSyncProgress>? progress)
    {
        var client = _httpClientFactory.CreateClient("GameDataSync");
        using var response = await client
            .GetAsync(entry.Url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = entry.SizeBytes > 0
            ? entry.SizeBytes
            : response.Content.Headers.ContentLength ?? 0;

        progress?.Report(new GameDataSyncProgress(false, 0, "开始下载..."));

        await using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
        await using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[81920];
            long readTotal = 0;
            int read;
            var lastReported = -1;

            while ((read = await input.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                sha256.AppendData(buffer, 0, read);
                readTotal += read;

                if (totalBytes > 0)
                {
                    var percent = Math.Clamp((int)(readTotal * 100 / totalBytes), 0, 100);
                    if (percent != lastReported)
                    {
                        lastReported = percent;
                        progress?.Report(new GameDataSyncProgress(false, percent, "下载中..."));
                    }
                }
            }

            var actualHash = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(entry.Sha256) &&
                !string.Equals(actualHash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"数据包校验失败：期望 {entry.Sha256}，实际 {actualHash}");
            }

            _logger.Information("Downloaded {Bytes} bytes, sha256={Sha256}", readTotal, actualHash);
        }

        progress?.Report(new GameDataSyncProgress(false, 100, "下载完成"));
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

            // game.json 是应用的静态配置(游戏名/图标/模型导入器/ModEnv 元数据),由 exe 内置自带,
            // 一旦被数据同步覆盖,会导致「一键配置 Mod 环境」按钮消失(见 .dataversion 版本比较)。
            // .dataversion 也由同步服务自身在下方写入,不应当从数据包复制进来。
            if (relativePath.Equals("game.json", StringComparison.OrdinalIgnoreCase) ||
                relativePath.Equals(DataVersionFileName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Information("Skipping protected config file during game data sync: {RelativePath}", relativePath);
                continue;
            }

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
    private class DataManifest
    {
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("games")] public Dictionary<string, GameDataEntry?> Games { get; set; } = new();
    }

    private class GameDataEntry
    {
        [JsonProperty("version")] public string Version { get; set; } = string.Empty;
        [JsonProperty("url")] public string Url { get; set; } = string.Empty;
        [JsonProperty("sizeBytes")] public long SizeBytes { get; set; }
        [JsonProperty("sha256")] public string? Sha256 { get; set; }
    }
    // ReSharper restore ClassNeverInstantiated.Local
}
