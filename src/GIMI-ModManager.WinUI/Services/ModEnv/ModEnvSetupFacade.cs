using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.GamesService.Models;
using GIMI_ModManager.Core.Services.CommandService;
using GIMI_ModManager.Core.Services.CommandService.Models;
using GIMI_ModManager.WinUI.Models.ModEnvSetup;
using GIMI_ModManager.WinUI.Models.Options;
using GIMI_ModManager.WinUI.Services;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModEnv;

public enum ModEnvPackageAction
{
    /// <summary>Not installed yet.</summary>
    NotInstalled,

    /// <summary>Installed version matches the manifest; nothing to do.</summary>
    UpToDate,

    /// <summary>An older version is installed; an update is available.</summary>
    UpdateAvailable,

    /// <summary>The selected version is older than the installed one; installing it is a rollback.</summary>
    Rollback,

    /// <summary>Marker says installed but key files are missing; reinstall to repair.</summary>
    NeedsRepair
}

public record ModEnvSetupRequest
{
    /// <summary>Resolved game install directory (auto-detected or manually picked by the user).</summary>
    public string? GameInstallDir { get; init; }

    /// <summary>Optional user override for the XXMI root folder.</summary>
    public string? CustomRootFolder { get; init; }

    /// <summary>
    /// Base-package version picked in the version dropdown, or null to use the version from the main
    /// manifest. Null is the default and reproduces the pre-version-selection behaviour exactly.
    /// </summary>
    public string? SelectedXxmiVersion { get; init; }
}

public record ModEnvPackagePreCheck
{
    public string PackageId { get; init; } = string.Empty;
    public string PackageName { get; init; } = string.Empty;
    public string? ManifestVersion { get; init; }
    public string? InstalledVersion { get; init; }
    public ModEnvPackageAction Action { get; init; }

    /// <summary>Localized display label for <see cref="Action"/>.</summary>
    public string ActionText => Action switch
    {
        ModEnvPackageAction.NotInstalled => "未安装",
        ModEnvPackageAction.UpToDate => "已是最新",
        ModEnvPackageAction.UpdateAvailable => "可更新",
        ModEnvPackageAction.Rollback => "可回退",
        ModEnvPackageAction.NeedsRepair => "需修复",
        _ => "未知"
    };

    /// <summary>Human-readable version summary for the wizard display.</summary>
    public string VersionSummary
    {
        get
        {
            var latest = string.IsNullOrWhiteSpace(ManifestVersion) ? "未知" : ManifestVersion;
            if (string.IsNullOrWhiteSpace(InstalledVersion))
                return $"最新版本：{latest}";

            return Action switch
            {
                ModEnvPackageAction.UpdateAvailable => $"已安装 v{InstalledVersion}，可更新到 v{latest}",
                ModEnvPackageAction.Rollback => $"已安装 v{InstalledVersion}，可回退到 v{latest}",
                _ => $"已安装 v{InstalledVersion}"
            };
        }
    }
}

public record ModEnvPreCheck
{
    public string? RootFolder { get; init; }
    public string? MiFolder { get; init; }
    public string? ModsFolder { get; init; }
    public string? GameVersion { get; init; }
    public List<ModEnvPackagePreCheck> Packages { get; init; } = new();
    public List<string> Issues { get; init; } = new();

    /// <summary>Selectable base-package versions, newest first. Empty when the catalogue is unavailable.</summary>
    public List<ModEnvCatalogVersion> XxmiVersions { get; init; } = new();

    /// <summary>
    /// Version the picker should preselect — the main manifest's own base version, so an untouched
    /// dropdown installs exactly what the pre-version-selection code installed.
    /// </summary>
    public string? DefaultXxmiVersion { get; init; }

    /// <summary>Version currently installed at the XXMI root (per the marker), if one is recorded.</summary>
    public string? InstalledXxmiVersion { get; init; }
}

public record ModEnvSetupResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? MiFolder { get; init; }
    public string? ModsFolder { get; init; }
    public List<string> Issues { get; init; } = new();
}

/// <summary>
/// Orchestrates the one-click Mod environment setup:
/// resolve game drive -> fetch version manifest -> evaluate installed state per package ->
/// download/verify/extract/copy missing or outdated packages -> re-verify -> persist idempotency marker.
/// </summary>
public class ModEnvSetupFacade
{
    private readonly ModEnvManifestService _manifestService;
    private readonly ModEnvVersionCatalogService _catalogService;
    private readonly ModEnvBackupService _backupService;
    private readonly ModEnvInstallerService _installer;
    private readonly GameInstallPathDetector _detector;
    private readonly CommandService _commandService;
    private readonly GenshinProcessManager _genshinProcessManager;
    private readonly ThreeDMigtoProcessManager _threeDMigtoProcessManager;
    private readonly IOptions<ModEnvSetupOptions> _options;
    private readonly ILogger _logger;

    public ModEnvSetupFacade(ModEnvManifestService manifestService,
        ModEnvVersionCatalogService catalogService, ModEnvBackupService backupService,
        ModEnvInstallerService installer, GameInstallPathDetector detector, CommandService commandService,
        GenshinProcessManager genshinProcessManager, ThreeDMigtoProcessManager threeDMigtoProcessManager,
        IOptions<ModEnvSetupOptions> options, ILogger logger)
    {
        _manifestService = manifestService;
        _catalogService = catalogService;
        _backupService = backupService;
        _installer = installer;
        _detector = detector;
        _commandService = commandService;
        _genshinProcessManager = genshinProcessManager;
        _threeDMigtoProcessManager = threeDMigtoProcessManager;
        _options = options;
        _logger = logger.ForContext<ModEnvSetupFacade>();
    }

    /// <summary>
    /// Reads current state without installing anything — used to display the idempotency pre-check
    /// (installed / update available / needs repair / not installed) in the wizard before the user starts.
    /// </summary>
    public async Task<ModEnvPreCheck> PreCheckAsync(ModEnvSetupRequest request, CancellationToken ct = default)
    {
        var gameInfo = await GameService.GetGameInfoAsync(SupportedGames.WuWa);
        if (gameInfo?.ModEnv is null)
            return new ModEnvPreCheck { Issues = { "该游戏暂不支持一键配置 Mod 环境" } };

        var modEnv = gameInfo.ModEnv;
        var (rootFolder, rootError) = await ResolveRootFolderAsync(request, modEnv, ct);
        if (rootFolder is null)
            return new ModEnvPreCheck { Issues = { rootError! } };

        var miFolder = Path.Combine(rootFolder, modEnv.SubDirName);
        var issues = new List<string>();
        var packages = new List<ModEnvPackagePreCheck>();
        var installed = await _installer.ReadInstalledVersionsAsync(rootFolder, ct);

        // Fetched together: the catalogue and the manifest are independent of each other, and failing to
        // load one must not take the other down — each degrades on its own.
        var manifestTask = _manifestService.GetManifestAsync(ct);
        var catalogTask = _catalogService.GetVersionsAsync(ct);
        await Task.WhenAll(manifestTask, catalogTask);
        var manifest = await manifestTask;
        var catalogVersions = await catalogTask;

        ModEnvPackage? basePkg = null;
        if (manifest is null)
        {
            issues.Add("无法获取 Mod 环境版本清单，请检查网络后重试");
        }
        else
        {
            basePkg = manifest.Packages.GetValueOrDefault(_options.Value.BasePackageId);
            var gamePkg = manifest.Packages.GetValueOrDefault(modEnv.PackageId);

            // The pre-check has to describe the version the user picked, not the manifest default, or the
            // status badges would talk about a package the setup run is not actually going to install.
            var effectiveBasePkg = ResolveBasePackage(basePkg, request.SelectedXxmiVersion, catalogVersions);
            if (!string.IsNullOrWhiteSpace(request.SelectedXxmiVersion) && effectiveBasePkg is null)
            {
                issues.Add($"所选 XXMI 版本 {request.SelectedXxmiVersion} 不在当前版本清单中，请重新选择");
            }

            if (basePkg is not null)
            {
                packages.Add(new ModEnvPackagePreCheck
                {
                    PackageId = _options.Value.BasePackageId,
                    PackageName = "XXMI 注入器框架",
                    ManifestVersion = (effectiveBasePkg ?? basePkg).Version,
                    InstalledVersion = installed.GetValueOrDefault(_options.Value.BasePackageId),
                    Action = EvaluateAction(installed, _options.Value.BasePackageId, effectiveBasePkg ?? basePkg,
                        BasePackageConsistent(rootFolder, installed))
                });
            }

            if (gamePkg is not null)
            {
                var (filesOk, modsOk) = _installer.CheckGamePackageFiles(miFolder);
                packages.Add(new ModEnvPackagePreCheck
                {
                    PackageId = modEnv.PackageId,
                    PackageName = "WWMi 鸣潮游戏包",
                    ManifestVersion = gamePkg.Version,
                    InstalledVersion = installed.GetValueOrDefault(modEnv.PackageId),
                    Action = EvaluateAction(installed, modEnv.PackageId, gamePkg, filesOk && modsOk)
                });
            }

            var launcherPkgId = _options.Value.LauncherPackageId;
            if (!string.IsNullOrWhiteSpace(launcherPkgId))
            {
                var launcherPkg = manifest.Packages.GetValueOrDefault(launcherPkgId);
                if (launcherPkg is not null)
                {
                    packages.Add(new ModEnvPackagePreCheck
                    {
                        PackageId = launcherPkgId,
                        PackageName = "XXMI 启动器 (GUI)",
                        ManifestVersion = launcherPkg.Version,
                        InstalledVersion = installed.GetValueOrDefault(launcherPkgId),
                        Action = EvaluateAction(installed, launcherPkgId, launcherPkg, LauncherFilesOk(rootFolder))
                    });
                }
                else
                {
                    issues.Add($"版本清单缺少 XXMI 启动器包 ({launcherPkgId})");
                }
            }

            if (basePkg is null || gamePkg is null)
                issues.Add("版本清单缺少必要的安装包");

            // The launcher reads the version from its own copy of the framework. Something else writing that
            // copy (the official launcher's own update button) leaves it disagreeing with the marker, which
            // is why the base package now reports "需修复" — say so, since the badge alone looks arbitrary.
            var recordedBase = installed.GetValueOrDefault(_options.Value.BasePackageId);
            var launcherBase = ReadLauncherVisibleXxmiVersion(rootFolder);
            if (!string.IsNullOrWhiteSpace(launcherBase) && !string.IsNullOrWhiteSpace(recordedBase)
                && !string.Equals(launcherBase, recordedBase, StringComparison.Ordinal))
            {
                issues.Add(
                    $"XXMI 启动器读到的是 v{launcherBase}，与 JASM 记录的 v{recordedBase} 不一致"
                    + $"（通常是点过官方启动器自己的更新按钮）；点「开始配置」会把两处统一成下拉框所选版本。");
            }
        }

        return new ModEnvPreCheck
        {
            RootFolder = rootFolder,
            MiFolder = miFolder,
            ModsFolder = Path.Combine(miFolder, "Mods"),
            GameVersion = request.GameInstallDir is { Length: > 0 } dir ? _detector.GetGameVersion(dir) : null,
            Packages = packages,
            Issues = issues,
            XxmiVersions = BuildVersionList(catalogVersions, basePkg),
            DefaultXxmiVersion = basePkg?.Version,
            InstalledXxmiVersion = installed.GetValueOrDefault(_options.Value.BasePackageId)
        };
    }

    /// <summary>
    /// Runs the full setup: installs missing/outdated packages, re-verifies and writes the marker file.
    /// Returns the two paths (MI folder + Mods folder) JASM needs, plus non-fatal issues/warnings.
    /// </summary>
    public async Task<ModEnvSetupResult> SetupAsync(ModEnvSetupRequest request, IProgress<string>? progress,
        CancellationToken ct = default)
    {
        try
        {
            var gameInfo = await GameService.GetGameInfoAsync(SupportedGames.WuWa);
            if (gameInfo?.ModEnv is null)
                return Fail("该游戏暂不支持一键配置 Mod 环境");

            var modEnv = gameInfo.ModEnv;
            var (rootFolder, rootError) = await ResolveRootFolderAsync(request, modEnv, ct);
            if (rootFolder is null)
                return Fail(rootError!);

            var miFolder = Path.Combine(rootFolder, modEnv.SubDirName);
            var modsFolder = Path.Combine(miFolder, "Mods");
            var issues = new List<string>();

            var manifest = await _manifestService.GetManifestAsync(ct);
            if (manifest is null)
                return Fail("无法获取 Mod 环境版本清单，请检查网络后重试");

            var gamePkg = manifest.Packages.GetValueOrDefault(modEnv.PackageId);
            if (gamePkg is null)
                return Fail("版本清单缺少必要的安装包");

            // Resolve the base package the user actually picked. With the dropdown untouched this is the
            // manifest's own base version, so the flow stays exactly what it was before version selection.
            var catalogVersions = await _catalogService.GetVersionsAsync(ct);
            var basePkg = ResolveBasePackage(
                manifest.Packages.GetValueOrDefault(_options.Value.BasePackageId),
                request.SelectedXxmiVersion, catalogVersions);
            if (basePkg is null)
            {
                return Fail(string.IsNullOrWhiteSpace(request.SelectedXxmiVersion)
                    ? "版本清单缺少必要的安装包"
                    : $"所选 XXMI 版本 {request.SelectedXxmiVersion} 不在当前版本清单中，请重新选择");
            }

            var installed = await _installer.ReadInstalledVersionsAsync(rootFolder, ct);
            var (filesOk, modsOk) = _installer.CheckGamePackageFiles(miFolder);

            // Base XXMI framework -> both copies the XXMI layout keeps: the XXMI root (what the game loads)
            // and Resources\Packages\XXMI (what the launcher reads its displayed version from).
            var baseAction =
                EvaluateAction(installed, _options.Value.BasePackageId, basePkg,
                    BasePackageConsistent(rootFolder, installed));
            if (baseAction != ModEnvPackageAction.UpToDate)
            {
                // Snapshot before overwriting. This throws on failure by design — continuing would destroy
                // the only copy of a working install, which is precisely what the backup exists to prevent.
                await BackupBaseFilesAsync(rootFolder, installed, progress, ct);

                progress?.Report(baseAction == ModEnvPackageAction.Rollback
                    ? $"回退 XXMI 注入器框架到 {basePkg.Version}..."
                    : $"安装/更新 XXMI 注入器框架 ({basePkg.Version})...");
                await _installer.InstallPackageAsync(basePkg, rootFolder, null, progress, ct,
                    mirrorTargetDirs: new[] { XxmiPackageDir(rootFolder) });
            }
            else
            {
                progress?.Report("XXMI 注入器框架已是最新版本，跳过。");
            }

            // Persist each package as soon as it is installed so a later failure (e.g. a flaky network on
            // the next package) doesn't force re-downloading an already-installed package on re-run.
            installed[_options.Value.BasePackageId] = basePkg.Version;
            await _installer.WriteMarkerAsync(rootFolder, installed, ct);

            // XXMI Launcher GUI -> into the XXMI root itself.
            var launcherPkgId = _options.Value.LauncherPackageId;
            if (!string.IsNullOrWhiteSpace(launcherPkgId))
            {
                var launcherPkg = manifest.Packages.GetValueOrDefault(launcherPkgId);
                if (launcherPkg is not null)
                {
                    var launcherAction = EvaluateAction(installed, launcherPkgId, launcherPkg, LauncherFilesOk(rootFolder));
                    if (launcherAction != ModEnvPackageAction.UpToDate)
                    {
                        progress?.Report($"安装/更新 XXMI 启动器 (GUI) ({launcherPkg.Version})...");
                        await _installer.InstallPackageAsync(launcherPkg, rootFolder, null, progress, ct,
                            preserveExistingFiles: new[] { LauncherConfigFileName });
                    }
                    else
                    {
                        progress?.Report("XXMI 启动器 (GUI) 已是最新版本，跳过。");
                    }

                    installed[launcherPkgId] = launcherPkg.Version;
                    await _installer.WriteMarkerAsync(rootFolder, installed, ct);
                }
            }

            // Ensure the launcher has a desktop shortcut (created on install AND when already up-to-date,
            // so a removed shortcut is restored). Non-fatal: failure is reported as a warning issue.
            EnsureLauncherDesktopShortcut(rootFolder, issues, progress);

            // Per-game package (WWMi) -> into <root>\<subDir>.
            var gameAction = EvaluateAction(installed, modEnv.PackageId, gamePkg, filesOk && modsOk);
            if (gameAction != ModEnvPackageAction.UpToDate)
            {
                progress?.Report($"安装/更新 WWMi 鸣潮游戏包 ({gamePkg.Version})...");
                await _installer.InstallPackageAsync(gamePkg, rootFolder, modEnv.SubDirName, progress, ct);
            }
            else
            {
                progress?.Report("WWMi 鸣潮游戏包已是最新版本，跳过。");
            }

            installed[modEnv.PackageId] = gamePkg.Version;
            await _installer.WriteMarkerAsync(rootFolder, installed, ct);

            // Ensure the Mods folder exists — some game packages may omit the (empty) dir from the zip.
            Directory.CreateDirectory(modsFolder);

            // Pre-fill the launcher GUI's game path + WWMi path so a fresh install opens with them set, and
            // align its stale cached versions so it stops offering a downgrade.
            // Non-fatal; mirrors EnsureLauncherDesktopShortcut.
            await EnsureLauncherConfigPathsAsync(rootFolder, miFolder, request.GameInstallDir, installed, issues,
                progress);

            // Wire up the JASM "start game" / "start model importer" commands to the XXMI launcher so the
            // user can launch the game (with mod injection) straight from the Characters page. Non-fatal.
            await EnsureLaunchCommandsAsync(rootFolder, gameInfo, issues, progress);

            // Re-verify after install.
            (filesOk, modsOk) = _installer.CheckGamePackageFiles(miFolder);
            if (!filesOk)
                issues.Add("安装后校验未通过：未找到注入器文件 (d3d11.dll / d3dx.ini)");
            if (!modsOk)
                issues.Add("安装后校验未通过：未找到 Mods 文件夹");

            // Final flush — packages were already persisted incrementally after each step, so this is an
            // idempotent safety net for any skipped-branch version bookkeeping.
            installed[_options.Value.BasePackageId] = basePkg.Version;
            installed[modEnv.PackageId] = gamePkg.Version;
            if (!string.IsNullOrWhiteSpace(_options.Value.LauncherPackageId))
            {
                var launcherPkg = manifest.Packages.GetValueOrDefault(_options.Value.LauncherPackageId);
                if (launcherPkg is not null)
                    installed[_options.Value.LauncherPackageId] = launcherPkg.Version;
            }
            await _installer.WriteMarkerAsync(rootFolder, installed, ct);

            // Version compatibility warning (non-blocking).
            var gameVersion = request.GameInstallDir is { Length: > 0 } dir ? _detector.GetGameVersion(dir) : null;
            if (!string.IsNullOrWhiteSpace(gameVersion) && !IsCompatible(gameVersion, gamePkg))
                issues.Add(
                    $"检测到游戏版本 {gameVersion}，与当前 WWMi 包（{gamePkg.Version}，适配 {gamePkg.GameVersion ?? "未知"}）可能不兼容，游戏内可能出现异常。");

            return new ModEnvSetupResult
            {
                Success = true,
                MiFolder = miFolder,
                ModsFolder = modsFolder,
                Issues = issues
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ModEnvSetupResult { Success = false, Cancelled = true };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ModEnv setup failed");
            return new ModEnvSetupResult { Success = false, Issues = { ex.Message } };
        }
    }

    /// <summary>
    /// Puts a previously taken snapshot back onto both copies of the XXMI framework, then records it as the
    /// installed version.
    /// </summary>
    /// <remarks>
    /// Snapshots the current files first, so a restore can itself be undone — otherwise "恢复" would be the
    /// one action in this feature with no way back. Only the base package is touched: the launcher and the
    /// per-game package are independent of the injector version.
    /// </remarks>
    public async Task<ModEnvSetupResult> RestoreBackupAsync(ModEnvSetupRequest request, ModEnvBackupInfo backup,
        IProgress<string>? progress, CancellationToken ct = default)
    {
        try
        {
            var gameInfo = await GameService.GetGameInfoAsync(SupportedGames.WuWa);
            if (gameInfo?.ModEnv is null)
                return Fail("该游戏暂不支持一键配置 Mod 环境");

            var (rootFolder, rootError) = await ResolveRootFolderAsync(request, gameInfo.ModEnv, ct);
            if (rootFolder is null)
                return Fail(rootError!);

            if (!Directory.Exists(backup.Folder))
                return Fail("该备份已不存在，可能已被清理，请重新打开向导。");

            var installed = await _installer.ReadInstalledVersionsAsync(rootFolder, ct);

            // Undoable restore: keep what is on disk right now before replacing it.
            await BackupBaseFilesAsync(rootFolder, installed, progress, ct);

            progress?.Report($"正在恢复备份 {backup.DisplayName}...");
            // Both copies, exactly like an install: restoring only the root would leave the launcher
            // displaying the version the user just rolled back from.
            await _installer.CopyToTargetAsync(backup.Folder, rootFolder, progress, ct);
            await _installer.CopyToTargetAsync(backup.Folder, XxmiPackageDir(rootFolder), progress, ct);

            // Snapshots taken before the manifest became part of the package hold only the DLLs, so the
            // launcher's copy keeps whatever manifest it had. Say so rather than let the user find the
            // mismatched version number themselves (a re-run of the setup aligns both copies).
            if (!File.Exists(Path.Combine(backup.Folder, XxmiManifestFileName)))
                progress?.Report(
                    $"注意：该备份不含 {XxmiManifestFileName}（由旧版 JASM 生成），启动器显示的版本可能未同步；再点一次「开始配置」即可对齐。");

            if (backup.Version == ModEnvBackupInfo.UnknownVersion)
            {
                // A snapshot taken when the installed version was unknown must not be recorded as a version,
                // or the next pre-check would claim a precise version we never actually established.
                installed.Remove(_options.Value.BasePackageId);
            }
            else
            {
                installed[_options.Value.BasePackageId] = backup.Version;
            }

            await _installer.WriteMarkerAsync(rootFolder, installed, ct);

            var miFolder = Path.Combine(rootFolder, gameInfo.ModEnv.SubDirName);
            var issues = new List<string>();
            if (!BaseFilesOk(rootFolder))
                issues.Add("恢复后校验未通过：XXMI 基础包文件不完整，请重新配置 Mod 环境。");

            return new ModEnvSetupResult
            {
                Success = issues.Count == 0,
                MiFolder = miFolder,
                ModsFolder = Path.Combine(miFolder, "Mods"),
                Issues = issues
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ModEnvSetupResult { Success = false, Cancelled = true };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ModEnv backup restore failed");
            return new ModEnvSetupResult { Success = false, Issues = { ex.Message } };
        }
    }

    /// <summary>
    /// Snapshots available for restore, newest first. Exposed here so the wizard keeps a single dependency
    /// and does not need to reach into the backup store itself.
    /// </summary>
    public IReadOnlyList<ModEnvBackupInfo> ListBackups() => _backupService.List();

    // ---- Helpers ------------------------------------------------------------

    /// <summary>
    /// The XXMI root a run installs into: the location picked on the startup page when there is one, otherwise
    /// the historical "&lt;game drive&gt;\&lt;RootDirName&gt;". Returns the failure message when neither is usable.
    /// </summary>
    /// <remarks>
    /// An explicit root makes the game drive irrelevant for *locating* the install — game on C:, XXMI on D: is a
    /// normal setup. The game directory is still used for the launcher GUI's game_folder hint, and that fill
    /// falls back to its own detection when the dialog did not supply one.
    /// </remarks>
    private async Task<(string? RootFolder, string? Error)> ResolveRootFolderAsync(
        ModEnvSetupRequest request, ModEnvInfo modEnv, CancellationToken ct)
    {
        if (request.CustomRootFolder is { Length: > 0 } customRoot)
        {
            if (!Path.IsPathFullyQualified(customRoot))
                return (null, "XXMI 安装位置必须是完整路径，例如 D:\\XXMI");

            return (Path.GetFullPath(customRoot), null);
        }

        var driveRoot = await ResolveDriveRootAsync(request, ct);
        return driveRoot is null
            ? (null, "未检测到游戏安装位置，请在向导中选择游戏目录")
            : (Path.Combine(driveRoot, modEnv.RootDirName), null);
    }

    private async Task<string?> ResolveDriveRootAsync(ModEnvSetupRequest request, CancellationToken ct)
    {
        // 1. Explicitly resolved game dir (auto-detected or manually picked by the user).
        if (request.GameInstallDir is { Length: > 0 } gameDir)
        {
            var root = Path.GetPathRoot(gameDir);
            if (!string.IsNullOrWhiteSpace(root))
                return root;
        }

        // 2. Auto-detect.
        var detected = await _detector.DetectAsync(ct);
        return detected?.DriveRoot;
    }

    /// <summary>Files of the shared XXMI base package: the three injector DLLs plus the version manifest.</summary>
    /// <remarks>
    /// The manifest is part of the package rather than an optional extra — the XXMI Launcher derives the
    /// version it displays from it, so switching versions without it swaps the DLLs while the version
    /// number visibly stays put.
    /// </remarks>
    private static readonly string[] BasePackageFiles =
        { "3dmloader.dll", "d3d11.dll", "d3dcompiler_47.dll", XxmiManifestFileName };

    /// <summary>Name of the XXMI package manifest, which also carries the launcher-visible version.</summary>
    private const string XxmiManifestFileName = "Manifest.json";

    /// <summary>
    /// The framework copy the XXMI Launcher reads: a sibling of the per-game packages under
    /// <c>Resources\Packages\</c>. The launcher never looks at the DLLs sitting at the XXMI root.
    /// </summary>
    private static string XxmiPackageDir(string rootFolder) =>
        Path.Combine(rootFolder, "Resources", "Packages", "XXMI");

    /// <summary>
    /// True when the base package is fully deployed to both places the XXMI layout keeps it in.
    /// </summary>
    /// <remarks>
    /// Requiring the manifest as well as the DLLs is what heals installs made by earlier JASM versions:
    /// those wrote only the root DLLs, so they report "需修复" and get brought in line by the next setup
    /// run instead of silently keeping the two copies split.
    /// </remarks>
    /// <summary>
    /// True when the deployed base package is self-consistent: both copies complete AND the copy the
    /// launcher reads reporting the version JASM recorded.
    /// </summary>
    /// <remarks>
    /// The version half matters because something outside JASM can write that copy — the official
    /// launcher's own "update" button does, and it pulls from GitHub. Without this check the marker alone
    /// would keep saying "已是最新" while the launcher runs a version JASM never installed, and the setup
    /// run would skip the base package, leaving the two copies split.
    /// </remarks>
    private bool BasePackageConsistent(string rootFolder, IReadOnlyDictionary<string, string> installed)
    {
        if (!BaseFilesOk(rootFolder)) return false;

        var recorded = installed.GetValueOrDefault(_options.Value.BasePackageId);
        if (string.IsNullOrWhiteSpace(recorded)) return true; // nothing recorded to compare against

        // An unreadable manifest is not evidence of drift — only a readable, differing version is.
        var visible = ReadLauncherVisibleXxmiVersion(rootFolder);
        return visible is null || string.Equals(visible, recorded, StringComparison.Ordinal);
    }

    private bool BaseFilesOk(string rootFolder)
    {
        if (!Directory.Exists(rootFolder)) return false;
        try
        {
            return BasePackageFiles.All(f => File.Exists(Path.Combine(rootFolder, f)))
                   && BasePackageFiles.All(f => File.Exists(Path.Combine(XxmiPackageDir(rootFolder), f)));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to inspect XXMI root {Root}", rootFolder);
            return false;
        }
    }

    /// <summary>
    /// Version recorded in the framework copy the launcher displays, or null when it is missing/unreadable.
    /// </summary>
    /// <remarks>
    /// JASM owns this copy too, so the two versions must agree; they diverge when something else (the
    /// official updater) touches it. Surfacing the mismatch in the pre-check turns "回退后版本号没变" from a
    /// puzzling symptom into a stated cause.
    /// </remarks>
    private string? ReadLauncherVisibleXxmiVersion(string rootFolder)
    {
        try
        {
            var manifestPath = Path.Combine(XxmiPackageDir(rootFolder), XxmiManifestFileName);
            if (!File.Exists(manifestPath)) return null;

            // File.ReadAllText strips a UTF-8 BOM, which this manifest is free to carry.
            return JsonNode.Parse(File.ReadAllText(manifestPath))?["version"]?.GetValue<string>()?.Trim();
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to read launcher-visible XXMI manifest under {Root}", rootFolder);
            return null;
        }
    }

    /// <summary>
    /// Builds the version list for the picker: the catalogue, with the main manifest's own base version
    /// synthesised in when the catalogue does not list it.
    /// </summary>
    /// <remarks>
    /// The synthesised entry is what turns "leave the dropdown alone == previous behaviour" into a hard
    /// guarantee, instead of something that depends on the maintainer keeping two CDN files in sync.
    /// </remarks>
    private static List<ModEnvCatalogVersion> BuildVersionList(
        IReadOnlyList<ModEnvCatalogVersion> catalogVersions, ModEnvPackage? basePkg)
    {
        var versions = new List<ModEnvCatalogVersion>(catalogVersions);

        if (basePkg is not null &&
            versions.All(v => !string.Equals(v.Version, basePkg.Version, StringComparison.Ordinal)))
        {
            versions.Add(new ModEnvCatalogVersion
            {
                Version = basePkg.Version,
                DownloadUrl = basePkg.DownloadUrl,
                Sha256 = basePkg.Sha256,
                SizeBytes = basePkg.SizeBytes
            });
        }

        versions.Sort((left, right) => ModEnvVersion.CompareDescending(left.Version, right.Version));
        return versions;
    }

    /// <summary>
    /// Picks the base package to install: the version the user selected, or the manifest's own when nothing
    /// was selected. Returns null when a selection is not in the catalogue (stale picker data).
    /// </summary>
    private static ModEnvPackage? ResolveBasePackage(ModEnvPackage? manifestBase, string? selectedVersion,
        IReadOnlyList<ModEnvCatalogVersion> catalogVersions)
    {
        if (string.IsNullOrWhiteSpace(selectedVersion))
            return manifestBase;

        if (manifestBase is not null &&
            string.Equals(manifestBase.Version, selectedVersion, StringComparison.Ordinal))
            return manifestBase;

        return catalogVersions.FirstOrDefault(v =>
            string.Equals(v.Version, selectedVersion, StringComparison.Ordinal));
    }

    /// <summary>
    /// Snapshots the base package files before they are overwritten, so the user can always switch back.
    /// </summary>
    /// <returns>The snapshot, or null on a fresh install where there is nothing to lose.</returns>
    /// <remarks>
    /// An IO failure is converted into a hard stop on purpose: carrying on would overwrite the only copy of
    /// a working install, which is exactly what this snapshot exists to prevent.
    /// </remarks>
    private async Task<ModEnvBackupInfo?> BackupBaseFilesAsync(string rootFolder,
        IReadOnlyDictionary<string, string> installed, IProgress<string>? progress, CancellationToken ct)
    {
        var currentVersion = installed.GetValueOrDefault(_options.Value.BasePackageId);
        if (string.IsNullOrWhiteSpace(currentVersion) && !BaseFilesOk(rootFolder))
            return null;

        progress?.Report("正在备份当前 XXMI 版本...");
        try
        {
            var backup = await _backupService.BackupAsync(rootFolder, currentVersion, BasePackageFiles, ct);
            if (backup is not null)
                progress?.Report($"已备份当前 XXMI 版本到 {backup.Folder}");

            return backup;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelling during the copy must still read as "user cancelled", not as a backup failure.
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to back up ModEnv base package from {Root}", rootFolder);
            throw new InvalidOperationException("备份当前 XXMI 版本失败，已中止操作以免无法回退：" + ex.Message, ex);
        }
    }

    /// <summary>Launcher config file that carries user-edited settings; preserved across package updates.</summary>
    private const string LauncherConfigFileName = "XXMI Launcher Config.json";

    /// <summary>True when the XXMI Launcher GUI executable is present at the root.</summary>
    private static bool LauncherFilesOk(string rootFolder)
        => File.Exists(Path.Combine(rootFolder, "Resources", "Bin", "XXMI Launcher.exe"));

    /// <summary>
    /// Creates (or restores) a "XXMI Launcher" shortcut on the user's desktop pointing at the launcher exe.
    /// The launcher itself does not create one; the MSI used to, so we replicate it for JASM installs.
    /// Non-fatal: failures are logged and surfaced as a warning issue, never abort the setup.
    /// </summary>
    private void EnsureLauncherDesktopShortcut(string rootFolder, List<string> issues, IProgress<string>? progress)
    {
        var launcherExe = Path.Combine(rootFolder, "Resources", "Bin", "XXMI Launcher.exe");
        if (!File.Exists(launcherExe))
        {
            progress?.Report("未找到 XXMI 启动器可执行文件，跳过桌面快捷方式。");
            return;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
        {
            _logger.Warning("Desktop folder unavailable ({Desktop}); skipping launcher shortcut", desktop);
            issues.Add("未找到用户桌面文件夹，未能创建 XXMI 启动器快捷方式。");
            return;
        }

        var shortcutPath = Path.Combine(desktop, "XXMI Launcher.lnk");
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                issues.Add("系统不支持创建桌面快捷方式 (WScript.Shell 不可用)。");
                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = launcherExe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(launcherExe);
            shortcut.IconLocation = $"{launcherExe},0";
            shortcut.Description = "XXMI Launcher";
            shortcut.Save();
            progress?.Report("已在桌面创建 XXMI 启动器快捷方式。");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to create launcher desktop shortcut at {Shortcut}", shortcutPath);
            issues.Add("创建桌面快捷方式失败，不影响 Mod 环境使用。");
        }
    }

    /// <summary>
    /// Pre-fills the launcher GUI's game path and WWMi path in "XXMI Launcher Config.json" so a fresh
    /// install opens with them set instead of relying on the launcher's first-run self-detection, and
    /// aligns the launcher's stale cached package versions (see <see cref="AlignStaleLauncherVersions"/>).
    /// Conservative: only fills when a field is empty (game_folder) or empty/relative (importer_folder),
    /// so user-set absolute paths are never overwritten. Non-fatal on any failure.
    /// </summary>
    private async Task EnsureLauncherConfigPathsAsync(string rootFolder, string miFolder, string? gameInstallDir,
        IReadOnlyDictionary<string, string> deployedVersions, List<string> issues, IProgress<string>? progress)
    {
        var configPath = Path.Combine(rootFolder, LauncherConfigFileName);

        // The launcher rewrites its config by rename (tmp -> config), so while it runs the file can be
        // briefly missing or locked. Closing it first makes our write authoritative; retries ride out
        // any remaining transient states.
        TryStopLauncherProcess(progress);

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    _logger.Information(
                        "Launcher config {Config} not present (attempt {Attempt}/{Max}); skipping GUI path fill",
                        configPath, attempt, maxAttempts);
                    return;
                }

                // JsonNode.Parse does not skip a leading UTF-8 BOM, but the package's clean config has one,
                // so decode manually and strip it. Write back WITHOUT a BOM: the launcher's Python json.loads
                // rejects a BOM ("Unexpected UTF-8 BOM"), which pops its "加载配置失败" dialog on first launch.
                var raw = File.ReadAllBytes(configPath);
                var hadBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                var text = Encoding.UTF8.GetString(raw);
                var root = JsonNode.Parse(hadBom ? text[1..] : text) as JsonObject;
                if (root is null)
                {
                    _logger.Information("Launcher config {Config} is not a JSON object; skipping GUI path fill",
                        configPath);
                    return;
                }

                var importers = root["Importers"] as JsonObject;
                var wwmi = importers?["WWMI"] as JsonObject;
                var importer = wwmi?["Importer"] as JsonObject;
                if (importer is null)
                {
                    _logger.Information(
                        "Launcher config {Config} has no Importers.WWMI.Importer node; skipping GUI path fill",
                        configPath);
                    return;
                }

                var pathsChanged = false;

                // The caller only passes a game dir when the user picked/auto-detected one in the dialog.
                // Fall back to our own detector so a plain one-click run still fills game_folder.
                if (string.IsNullOrWhiteSpace(gameInstallDir))
                {
                    var detected = await _detector.DetectAsync();
                    if (detected is not null)
                    {
                        gameInstallDir = detected.InstallDir;
                        _logger.Information(
                            "Launcher config fill: no game dir supplied, falling back to detected install at {Path}",
                            gameInstallDir);
                    }
                }

                if (!string.IsNullOrWhiteSpace(gameInstallDir))
                {
                    var currentGame = importer["game_folder"]?.GetValue<string>() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(currentGame))
                    {
                        var gameFolder = FindGameFolder(gameInstallDir);
                        if (gameFolder is not null)
                        {
                            importer["game_folder"] = gameFolder;
                            pathsChanged = true;
                        }
                    }
                }

                var currentImporter = importer["importer_folder"]?.GetValue<string>() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(currentImporter) || !Path.IsPathRooted(currentImporter))
                {
                    importer["importer_folder"] = miFolder.Replace('\\', '/');
                    pathsChanged = true;
                }

                // Stops the launcher from offering to "update" to the older version it has cached.
                var alignedVersions =
                    AlignStaleLauncherVersions(deployedVersions, root["Packages"]?["packages"] as JsonObject);

                if (!pathsChanged && alignedVersions.Count == 0 && !hadBom)
                {
                    progress?.Report("启动器配置已是最新，无需更新。");
                    return;
                }

                var writerOptions = new JsonWriterOptions { Indented = true, IndentSize = 4 };
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, writerOptions))
                    root.WriteTo(writer);

                var json = Encoding.UTF8.GetString(stream.ToArray());
                File.WriteAllText(configPath, json, new UTF8Encoding(false));
                if (pathsChanged)
                {
                    _logger.Information(
                        "Pre-filled launcher GUI paths in {Config}: game_folder={GameFolder}, importer_folder={ImporterFolder}",
                        configPath, importer["game_folder"]?.GetValue<string>(), importer["importer_folder"]?.GetValue<string>());
                    progress?.Report("已自动填写启动器 GUI 的游戏路径与 WWMi 路径。");
                }

                if (alignedVersions.Count > 0)
                {
                    _logger.Information("Aligned stale launcher package versions in {Config}: {Aligned}",
                        configPath, string.Join(", ", alignedVersions));
                    progress?.Report(
                        $"已把 XXMI 启动器缓存的过期版本对齐到实装版本（{string.Join("、", alignedVersions)}），避免它反复提示「更新」。");
                }

                return;
            }
            catch (IOException ex) when (attempt < maxAttempts)
            {
                _logger.Information(ex, "Launcher config {Config} is busy (attempt {Attempt}/{Max}); retrying",
                    configPath, attempt, maxAttempts);
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to pre-fill launcher GUI paths in {Config}", configPath);
                issues.Add("未能自动填写启动器 GUI 的游戏路径与 WWMi 路径。");
                return;
            }
        }

        _logger.Warning("Failed to pre-fill launcher GUI paths in {Config} after {Max} attempts", configPath, maxAttempts);
        issues.Add("未能自动填写启动器 GUI 的游戏路径与 WWMi 路径。");
    }

    /// <summary>
    /// Brings the launcher's cached version records for every package JASM just deployed back in line with what
    /// is on disk: <c>latest_version</c> and <c>deployed_version</c> become the deployed version, and the
    /// pending-version leftovers (<c>skipped_version</c>, <c>latest_release_notes</c>) are cleared. Returns what
    /// it touched (launcher display name + the stale version it replaced) for the progress log.
    /// </summary>
    /// <remarks>
    /// The launcher treats "version read off disk != cached latest_version" as an update — including when the
    /// cached value is <em>older</em> than what is installed, which renders the nonsensical
    /// "将包更新到最新版本：XXMI: 1.1.7 → 1.0.5" prompt. Its cache is only refreshed from GitHub releases
    /// (core/package_manager.py -> github_client.py), which is unreachable for most of our users and rate-limits
    /// the rest, so the mismatch persists indefinitely.
    /// Measured 2026-09-14 on a real install (cache latest=1.0.5 / skipped=1.0.5 / on-disk 1.1.7): writing
    /// <c>skipped_version</c> alone — the launcher's own "跳过" button — does <em>not</em> silence that prompt;
    /// it is only honoured by the update dialog. Aligning the cache instead reproduces exactly the state the
    /// launcher's own updater leaves behind (latest == deployed == on-disk), which is silent.
    /// Only strictly older cached versions are aligned: when the cache knows a <em>newer</em> version than the
    /// one we deployed (e.g. the user deliberately rolled back), the launcher's offer is legitimate and stays.
    /// </remarks>
    private static List<string> AlignStaleLauncherVersions(
        IReadOnlyDictionary<string, string> deployedVersions, JsonObject? launcherPackages)
    {
        var aligned = new List<string>();
        if (launcherPackages is null)
            return aligned;

        foreach (var (packageId, deployedVersion) in deployedVersions)
        {
            if (string.IsNullOrWhiteSpace(deployedVersion))
                continue;

            // The config keys are the launcher's display names ("XXMI" / "WWMI" / "Launcher") while our package
            // ids are lower-case, and JsonObject indexers are case-sensitive — hence the manual lookup.
            var entry = launcherPackages.FirstOrDefault(pair =>
                string.Equals(pair.Key, packageId, StringComparison.OrdinalIgnoreCase));
            if (entry.Value is not JsonObject package)
                continue;

            var cachedLatest = package["latest_version"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(cachedLatest) || !IsOlderVersion(cachedLatest, deployedVersion))
                continue;

            package["latest_version"] = deployedVersion;
            package["deployed_version"] = deployedVersion;

            // A skip for that same stale version is now moot. A skip naming some *other* version is the user's
            // own choice about a genuinely pending one, so it is left alone.
            var currentSkip = package["skipped_version"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(currentSkip) ||
                string.Equals(currentSkip, cachedLatest, StringComparison.OrdinalIgnoreCase))
                package["skipped_version"] = string.Empty;

            // The notes describe the cached latest we just replaced; without them the launcher would show the
            // old version's changelog next to the new version number.
            package["latest_release_notes"] = string.Empty;

            aligned.Add($"{entry.Key} {cachedLatest} → {deployedVersion}");
        }

        return aligned;
    }

    /// <summary>
    /// True when both values parse as versions and the first is strictly older, i.e. the launcher's cached
    /// "latest" is actually behind what JASM deployed. Unparseable values return false so an update we cannot
    /// reason about is never suppressed.
    /// </summary>
    private static bool IsOlderVersion(string candidate, string reference)
    {
        var left = ParseLooseVersion(candidate);
        var right = ParseLooseVersion(reference);
        return left is not null && right is not null && left < right;
    }

    /// <summary>
    /// Parses "v1.1.7" / "1.1.7-beta.1" style versions. Returns null when there is no usable "major.minor"
    /// core, so callers can treat the value as unknown instead of guessing at its ordering.
    /// </summary>
    private static Version? ParseLooseVersion(string value)
    {
        var text = value.Trim().TrimStart('v', 'V');
        var suffix = text.IndexOfAny(new[] { '-', '+' });
        if (suffix >= 0)
            text = text[..suffix];

        return Version.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Writes the JASM "start game" / "start model importer" commands so a one-click setup leaves the user
    /// able to launch the game (with XXMI mod injection) straight from the Characters page, no manual command
    /// configuration needed.
    ///
    /// The WWMi importer is a d3d11.dll hook, not an exe, so the only way to "start" it is through the
    /// XXMI Launcher, which supports a no-GUI CLI start: "XXMI Launcher.exe --xxmi WWMI --nogui" starts the
    /// game with the active importer and no window. Both commands are REPLACED when already present, so
    /// re-running setup is idempotent and upgrades a previously manual (plain exe) game-start command.
    /// Non-fatal: any failure is logged and surfaced as a warning issue, never aborts the setup.
    /// </summary>
    private async Task EnsureLaunchCommandsAsync(string rootFolder, GameInfo gameInfo, List<string> issues,
        IProgress<string>? progress)
    {
        // Without a launcher managed by JASM there is nothing to start the game through — leave the
        // existing commands untouched so a manual (importer-less) setup keeps working.
        if (string.IsNullOrWhiteSpace(_options.Value.LauncherPackageId))
            return;

        var launcherExe = Path.Combine(rootFolder, "Resources", "Bin", "XXMI Launcher.exe");
        if (!File.Exists(launcherExe))
        {
            progress?.Report("未找到 XXMI 启动器可执行文件，跳过启动命令配置。");
            return;
        }

        var binDir = Path.GetDirectoryName(launcherExe) ?? rootFolder;
        // The launcher's --xxmi code is the importer's sub-directory name (WWMI/, GIMI/, …), which for
        // WuWa equals ModEnv.SubDirName ("WWMI").
        var importerCode = gameInfo.ModEnv?.SubDirName;
        if (string.IsNullOrWhiteSpace(importerCode))
        {
            _logger.Warning("Cannot configure launch commands: ModEnv.SubDirName is empty for {Game}",
                gameInfo.GameName);
            issues.Add("未能配置启动命令：Mod 环境缺少 importer 代码。");
            return;
        }

        try
        {
            // 1) "Start game" -> launcher --nogui: one click starts the game with mods, no window.
            var gameCommand = new CommandDefinition
            {
                CommandDisplayName = $"Start {gameInfo.GameName} (XXMI)",
                KillOnMainAppExit = false,
                ExecutionOptions = new CommandExecutionOptions
                {
                    UseShellExecute = true,
                    RunAsAdmin = true,
                    CreateWindow = true,
                    Command = launcherExe,
                    Arguments = $"--xxmi {importerCode} --nogui",
                    WorkingDirectory = binDir
                }
            };
            await SaveOrUpdateSpecialCommandAsync(gameCommand, gameStart: true, modelImporterStart: false);
            _logger.Information("Auto-configured game start command: {Cmd} {Args}", launcherExe,
                gameCommand.ExecutionOptions.Arguments);
            progress?.Report("已配置「启动游戏」命令（XXMI 带 mod 启动）。");

            // 2) "Start 3Dmigoto" -> launcher GUI (no --nogui): opens the launcher to manage the mod env.
            var importerCommand = new CommandDefinition
            {
                CommandDisplayName = $"Start {gameInfo.GameModelImporterName} (XXMI)",
                KillOnMainAppExit = false,
                ExecutionOptions = new CommandExecutionOptions
                {
                    UseShellExecute = true,
                    RunAsAdmin = true,
                    CreateWindow = true,
                    Command = launcherExe,
                    Arguments = null,
                    WorkingDirectory = binDir
                }
            };
            await SaveOrUpdateSpecialCommandAsync(importerCommand, gameStart: false, modelImporterStart: true);
            _logger.Information("Auto-configured model importer start command: {Cmd}", launcherExe);
            progress?.Report("已配置「启动 3Dmigoto」命令（打开启动器 GUI）。");

            // Refresh the singleton process managers so the Characters page buttons pick up the new commands
            // instead of falling back to the "pick an exe" dialog.
            await _genshinProcessManager.TryInitialize();
            await _threeDMigtoProcessManager.TryInitialize();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to auto-configure launch commands");
            issues.Add("未能自动配置启动命令，可在设置中手动配置。");
        }
    }

    /// <summary>
    /// Saves <paramref name="newCommand"/> as the game-start or model-importer-start command, replacing an
    /// existing special command of the same kind when present. Re-fetches the current command list so the
    /// snapshot is never stale across the two (game + importer) writes in <see cref="EnsureLaunchCommandsAsync"/>.
    /// </summary>
    private async Task SaveOrUpdateSpecialCommandAsync(CommandDefinition newCommand, bool gameStart,
        bool modelImporterStart)
    {
        var commands = await _commandService.GetCommandDefinitionsAsync();
        var existingSpecial = gameStart
            ? commands.FirstOrDefault(c => c.IsGameStartCommand)
            : commands.FirstOrDefault(c => c.IsModelImporterCommand);

        if (existingSpecial is not null)
        {
            // UpdateCommandDefinitionAsync keeps the IsGameStartCommand/IsModelImporterCommand flags.
            await _commandService.UpdateCommandDefinitionAsync(existingSpecial.Id, newCommand);
            return;
        }

        await _commandService.SaveCommandDefinitionAsync(newCommand);
        await _commandService.SetSpecialCommands(newCommand.Id, gameStart, modelImporterStart);
    }

    /// <summary>
    /// Force-closes a running XXMI Launcher so it cannot lock or overwrite the config while we fill it.
    /// Best-effort and non-fatal: without it the retry loop may still succeed when the file is not held.
    /// </summary>
    private static void TryStopLauncherProcess(IProgress<string>? progress)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("XXMI Launcher"))
            {
                p.Kill();
                p.WaitForExit(3000);
            }
            progress?.Report("已关闭运行中的 XXMI 启动器，避免其覆盖写入的路径。");
        }
        catch (Exception ex)
        {
            // Non-fatal: closing the launcher is best-effort; the write below may still succeed.
            _ = ex;
        }
    }

    /// <summary>
    /// Derives the launcher's game_folder: the directory holding the game executable at its root
    /// (or a Client dir). The real install layout is "&lt;root&gt;\Wuthering Waves Game\Wuthering Waves.exe"
    /// where the exe sits at the game folder root, not under Client\Binaries\Win64. Best-effort:
    /// falls back to the install dir itself.
    /// </summary>
    private static string? FindGameFolder(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return null;

        if (LooksLikeGameFolder(installDir))
            return installDir;

        foreach (var sub in Directory.GetDirectories(installDir))
        {
            if (LooksLikeGameFolder(sub))
                return sub;
        }

        return installDir;
    }

    private static bool LooksLikeGameFolder(string dir)
    {
        if (!Directory.Exists(dir)) return false;

        if (File.Exists(Path.Combine(dir, "Wuthering Waves.exe")))
            return true;

        return Directory.Exists(Path.Combine(dir, "Client"));
    }

    private static ModEnvPackageAction EvaluateAction(IReadOnlyDictionary<string, string> installed, string packageId,
        ModEnvPackage pkg, bool filesOk)
    {
        if (!installed.TryGetValue(packageId, out var installedVer) || string.IsNullOrWhiteSpace(installedVer))
            return ModEnvPackageAction.NotInstalled;

        if (!filesOk)
            return ModEnvPackageAction.NeedsRepair;

        if (string.Equals(installedVer, pkg.Version, StringComparison.Ordinal))
            return ModEnvPackageAction.UpToDate;

        // Numeric comparison, not Ordinal: a string compare ranks "1.1.7" below "0.9.2", so switching to an
        // older version would be labelled an update instead of a rollback. The distinction is what the wizard
        // shows the user, and only Rollback triggers the pre-switch snapshot messaging.
        return ModEnvVersion.IsOlder(installedVer, pkg.Version)
            ? ModEnvPackageAction.UpdateAvailable
            : ModEnvPackageAction.Rollback;
    }

    private static bool IsCompatible(string gameVersion, ModEnvPackage pkg)
    {
        if (string.Equals(gameVersion, pkg.GameVersion, StringComparison.OrdinalIgnoreCase))
            return true;

        return pkg.CompatibleGameVersions.Any(v =>
            string.Equals(gameVersion, v, StringComparison.OrdinalIgnoreCase));
    }

    private static ModEnvSetupResult Fail(string message) =>
        new() { Success = false, Issues = { message } };
}
