using System.Text.Json;
using GIMI_ModManager.Core.Services.WuwaUhd;
using GIMI_ModManager.WinUI.Services.ModEnv;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>一次 UHD 配置修复的结果。<see cref="BackupDirectory"/> 可用于回滚。</summary>
public sealed record WuwaUhdRepairResult(
    bool Success,
    string Message,
    string? BackupDirectory,
    IReadOnlyList<string> ChangedFiles)
{
    public static WuwaUhdRepairResult Fail(string message) => new(false, message, null, []);
}

/// <summary>
/// 鸣潮 UHD 贴图修复的“配置侧”实现：定位游戏配置、备份、写回、回滚。
///
/// 只负责 Rocket 的 XXMI 配置方式（Engine.ini / GameUserSettings.ini），不注入、不改游戏内存。
/// 哈希侧修复仍走现有 <see cref="WuwaModFixerEngine"/>（CLI 的 <c>--derived-hashes</c> /
/// <c>--stable-texture</c>，即 LOD Bias 派生哈希/稳定纹理），两者互补：
/// 配置修的是“贴图流送/光追把 mod 贴图降级或截断”，哈希修的是“贴图/组件 hash 过期”。
/// </summary>
internal sealed class WuwaUhdRepairService
{
    private const string BackupRootName = "UhdRepairBackups";
    private const string ManifestFileName = "manifest.json";

    private readonly GameInstallPathDetector _gameInstallPathDetector;
    private readonly ILogger _logger;

    public WuwaUhdRepairService(GameInstallPathDetector gameInstallPathDetector, ILogger logger)
    {
        _gameInstallPathDetector = gameInstallPathDetector;
        _logger = logger.ForContext<WuwaUhdRepairService>();
    }

    private static string BackupRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JASM", BackupRootName);

    /// <summary>解析当前游戏的两份配置文件路径；解析不出返回 <c>null</c>。</summary>
    public async Task<WuwaGameConfigPaths?> ResolveConfigPathsAsync(CancellationToken cancellationToken = default)
    {
        var exe = await ResolveGameExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(exe))
            return null;

        var paths = WuwaGameConfigPathsResolver.ResolveFromGameExecutable(exe);
        if (paths is null)
        {
            _logger.Warning("[UHD修复] 从 {Exe} 未能定位 Client\\Saved\\Config\\WindowsNoEditor", exe);
        }

        return paths;
    }

    /// <summary>探测已安装的资源档位（SD/HD/UHD）。</summary>
    public async Task<WuwaTierDetectionResult> DetectTierAsync(CancellationToken cancellationToken = default)
    {
        var exe = await ResolveGameExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(exe))
            return new WuwaTierDetectionResult(WuwaTierDetectionKind.NotFound, null, []);

        var result = WuwaResourceTierDetector.DetectFromGameExecutable(exe);
        _logger.Information("[UHD修复] 档位检测: {Kind}, 已安装=[{Installed}]", result.Kind,
            string.Join(",", result.Installed));
        return result;
    }

    private async Task<string?> ResolveGameExecutableAsync(CancellationToken cancellationToken)
    {
        var install = await _gameInstallPathDetector.DetectAsync(cancellationToken).ConfigureAwait(false);
        if (install is null)
        {
            _logger.Warning("[UHD修复] 找不到鸣潮安装位置");
            return null;
        }

        var exe = _gameInstallPathDetector.FindGameExecutable(install.InstallDir);
        if (string.IsNullOrEmpty(exe))
            _logger.Warning("[UHD修复] 在 {Dir} 下找不到游戏可执行文件", install.InstallDir);

        return exe;
    }

    /// <summary>所有修复项是否已经是目标值。</summary>
    public async Task<bool> IsAppliedAsync(CancellationToken cancellationToken = default)
    {
        var paths = await ResolveConfigPathsAsync(cancellationToken).ConfigureAwait(false);
        if (paths is null)
            return false;

        var engine = ReadIfExists(paths.EngineIniPath);
        var userSettings = ReadIfExists(paths.GameUserSettingsIniPath);

        return WuwaUhdConfigRepair.IsApplied(engine, WuwaUhdConfigRepair.EngineSettings)
               && WuwaUhdConfigRepair.IsApplied(userSettings, WuwaUhdConfigRepair.GameUserSettingsSettings);
    }

    /// <summary>
    /// 备份后写入配置。已经全是目标值时直接返回成功且不产生备份。
    /// </summary>
    public async Task<WuwaUhdRepairResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        var paths = await ResolveConfigPathsAsync(cancellationToken).ConfigureAwait(false);
        if (paths is null)
            return WuwaUhdRepairResult.Fail("找不到鸣潮的 Client\\Saved\\Config\\WindowsNoEditor，无法修复配置。");

        var changes = new List<PendingChange>();

        var engineOriginal = ReadIfExists(paths.EngineIniPath);
        var engineUpdated = WuwaUhdConfigRepair.Apply(engineOriginal, WuwaUhdConfigRepair.EngineSettings);
        if (!string.Equals(engineOriginal, engineUpdated, StringComparison.Ordinal))
            changes.Add(new PendingChange(paths.EngineIniPath, engineOriginal, engineUpdated, File.Exists(paths.EngineIniPath)));

        var userOriginal = ReadIfExists(paths.GameUserSettingsIniPath);
        var userUpdated = WuwaUhdConfigRepair.Apply(userOriginal, WuwaUhdConfigRepair.GameUserSettingsSettings);
        if (!string.Equals(userOriginal, userUpdated, StringComparison.Ordinal))
            changes.Add(new PendingChange(paths.GameUserSettingsIniPath, userOriginal, userUpdated,
                File.Exists(paths.GameUserSettingsIniPath)));

        if (changes.Count == 0)
        {
            return new WuwaUhdRepairResult(true, "配置已经是 UHD 修复的目标值，无需修改。", null, []);
        }

        string backupDirectory;
        try
        {
            backupDirectory = CreateBackup(changes);
        }
        catch (Exception e)
        {
            _logger.Error(e, "[UHD修复] 创建备份失败");
            return WuwaUhdRepairResult.Fail("创建配置备份失败，未修改任何文件：" + e.Message);
        }

        try
        {
            foreach (var change in changes)
                WriteAtomic(change.Path, change.Updated);

            // 写完回读校验：写盘成功不代表内容符合预期（编码/权限/杀软改写）
            foreach (var change in changes)
            {
                if (!File.Exists(change.Path) || !string.Equals(ReadIfExists(change.Path), change.Updated, StringComparison.Ordinal))
                    throw new IOException("写入后回读校验失败: " + change.Path);
            }

            _logger.Information("[UHD修复] 已写入 {Count} 份配置，备份在 {Backup}", changes.Count, backupDirectory);
            return new WuwaUhdRepairResult(true,
                $"已修复 {changes.Count} 份配置（备份已保存）。重启游戏后生效。",
                backupDirectory,
                changes.Select(c => c.Path).ToList());
        }
        catch (Exception e)
        {
            _logger.Error(e, "[UHD修复] 写入配置失败，尝试回滚");
            TryRestore(changes, backupDirectory);
            return WuwaUhdRepairResult.Fail("写入配置失败，已尝试回滚：" + e.Message);
        }
    }

    /// <summary>
    /// 回滚最近一次备份。没有备份时返回失败；备份清单损坏时不猜、直接报错。
    /// </summary>
    public Task<WuwaUhdRepairResult> RestoreLatestAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(BackupRoot))
            return Task.FromResult(WuwaUhdRepairResult.Fail("没有找到 UHD 修复的备份。"));

        var latest = new DirectoryInfo(BackupRoot)
            .GetDirectories()
            .OrderByDescending(d => d.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (latest is null)
            return Task.FromResult(WuwaUhdRepairResult.Fail("没有找到 UHD 修复的备份。"));

        try
        {
            var manifestPath = Path.Combine(latest.FullName, ManifestFileName);
            var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath));
            if (manifest is null || manifest.Files.Count == 0)
                return Task.FromResult(WuwaUhdRepairResult.Fail("备份清单为空，未做任何修改。"));

            var restored = new List<string>();
            foreach (var entry in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (entry.Existed)
                {
                    var backupFile = Path.Combine(latest.FullName, entry.BackupFile);
                    if (!File.Exists(backupFile))
                        return Task.FromResult(WuwaUhdRepairResult.Fail("备份文件缺失：" + backupFile));

                    WriteAtomic(entry.OriginalPath, File.ReadAllText(backupFile));
                }
                else if (File.Exists(entry.OriginalPath))
                {
                    File.Delete(entry.OriginalPath);
                }

                restored.Add(entry.OriginalPath);
            }

            _logger.Information("[UHD修复] 已从 {Backup} 恢复 {Count} 份配置", latest.FullName, restored.Count);
            return Task.FromResult(new WuwaUhdRepairResult(true,
                $"已恢复 {restored.Count} 份配置（{latest.Name}）。重启游戏后生效。",
                latest.FullName,
                restored));
        }
        catch (Exception e)
        {
            _logger.Error(e, "[UHD修复] 恢复备份失败");
            return Task.FromResult(WuwaUhdRepairResult.Fail("恢复备份失败：" + e.Message));
        }
    }

    private static string ReadIfExists(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".jasm-uhd.tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private static string CreateBackup(IReadOnlyList<PendingChange> changes)
    {
        var directory = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(directory);

        var manifest = new BackupManifest
        {
            CreatedUtc = DateTime.UtcNow,
            Files = []
        };

        foreach (var change in changes)
        {
            var fileName = Path.GetFileName(change.Path);
            if (change.Existed)
                File.Copy(change.Path, Path.Combine(directory, fileName), overwrite: true);

            manifest.Files.Add(new BackupEntry(change.Path, change.Existed, fileName));
        }

        File.WriteAllText(Path.Combine(directory, ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        return directory;
    }

    private static void TryRestore(IReadOnlyList<PendingChange> changes, string backupDirectory)
    {
        foreach (var change in changes)
        {
            try
            {
                var backupFile = Path.Combine(backupDirectory, Path.GetFileName(change.Path));
                if (change.Existed && File.Exists(backupFile))
                    WriteAtomic(change.Path, File.ReadAllText(backupFile));
                else if (!change.Existed && File.Exists(change.Path))
                    File.Delete(change.Path);
            }
            catch
            {
                // 回滚本身失败时不再抛：调用方已经拿到原始错误，这里只保证尽量不留下半份文件
            }
        }
    }

    private sealed record PendingChange(string Path, string Original, string Updated, bool Existed);

    private sealed record BackupManifest
    {
        public DateTime CreatedUtc { get; set; }
        public List<BackupEntry> Files { get; set; } = [];
    }

    private sealed record BackupEntry(string OriginalPath, bool Existed, string BackupFile);
}
