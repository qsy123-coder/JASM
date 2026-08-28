using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.WinUI.Models.ModEnvSetup;
using GIMI_ModManager.WinUI.Models.Options;
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

    /// <summary>Marker says installed but key files are missing; reinstall to repair.</summary>
    NeedsRepair
}

public record ModEnvSetupRequest
{
    /// <summary>Resolved game install directory (auto-detected or manually picked by the user).</summary>
    public string? GameInstallDir { get; init; }

    /// <summary>Optional user override for the XXMI root folder.</summary>
    public string? CustomRootFolder { get; init; }
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

            return Action == ModEnvPackageAction.UpdateAvailable
                ? $"已安装 v{InstalledVersion}，可更新到 v{latest}"
                : $"已安装 v{InstalledVersion}";
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
    private readonly ModEnvInstallerService _installer;
    private readonly GameInstallPathDetector _detector;
    private readonly IOptions<ModEnvSetupOptions> _options;
    private readonly ILogger _logger;

    public ModEnvSetupFacade(ModEnvManifestService manifestService, ModEnvInstallerService installer,
        GameInstallPathDetector detector, IOptions<ModEnvSetupOptions> options, ILogger logger)
    {
        _manifestService = manifestService;
        _installer = installer;
        _detector = detector;
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
        var driveRoot = await ResolveDriveRootAsync(request, ct);
        if (driveRoot is null)
            return new ModEnvPreCheck { Issues = { "未检测到游戏安装位置，请在向导中选择游戏目录" } };

        var rootFolder = request.CustomRootFolder ?? Path.Combine(driveRoot, modEnv.RootDirName);
        var miFolder = Path.Combine(rootFolder, modEnv.SubDirName);
        var issues = new List<string>();
        var packages = new List<ModEnvPackagePreCheck>();
        var installed = await _installer.ReadInstalledVersionsAsync(rootFolder, ct);

        var manifest = await _manifestService.GetManifestAsync(ct);
        if (manifest is null)
        {
            issues.Add("无法获取 Mod 环境版本清单，请检查网络后重试");
        }
        else
        {
            var basePkg = manifest.Packages.GetValueOrDefault(_options.Value.BasePackageId);
            var gamePkg = manifest.Packages.GetValueOrDefault(modEnv.PackageId);

            if (basePkg is not null)
            {
                packages.Add(new ModEnvPackagePreCheck
                {
                    PackageId = _options.Value.BasePackageId,
                    PackageName = "XXMI 注入器框架",
                    ManifestVersion = basePkg.Version,
                    InstalledVersion = installed.GetValueOrDefault(_options.Value.BasePackageId),
                    Action = EvaluateAction(installed, _options.Value.BasePackageId, basePkg, BaseFilesOk(rootFolder))
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
        }

        return new ModEnvPreCheck
        {
            RootFolder = rootFolder,
            MiFolder = miFolder,
            ModsFolder = Path.Combine(miFolder, "Mods"),
            GameVersion = request.GameInstallDir is { Length: > 0 } dir ? _detector.GetGameVersion(dir) : null,
            Packages = packages,
            Issues = issues
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
            var driveRoot = await ResolveDriveRootAsync(request, ct);
            if (driveRoot is null)
                return Fail("未检测到游戏安装位置，请先手动选择游戏目录");

            var rootFolder = request.CustomRootFolder ?? Path.Combine(driveRoot, modEnv.RootDirName);
            var miFolder = Path.Combine(rootFolder, modEnv.SubDirName);
            var modsFolder = Path.Combine(miFolder, "Mods");
            var issues = new List<string>();

            var manifest = await _manifestService.GetManifestAsync(ct);
            if (manifest is null)
                return Fail("无法获取 Mod 环境版本清单，请检查网络后重试");

            var basePkg = manifest.Packages.GetValueOrDefault(_options.Value.BasePackageId);
            var gamePkg = manifest.Packages.GetValueOrDefault(modEnv.PackageId);
            if (basePkg is null || gamePkg is null)
                return Fail("版本清单缺少必要的安装包");

            var installed = await _installer.ReadInstalledVersionsAsync(rootFolder, ct);
            var (filesOk, modsOk) = _installer.CheckGamePackageFiles(miFolder);

            // Base XXMI framework -> into the XXMI root itself.
            var baseAction = EvaluateAction(installed, _options.Value.BasePackageId, basePkg, BaseFilesOk(rootFolder));
            if (baseAction != ModEnvPackageAction.UpToDate)
            {
                progress?.Report($"安装/更新 XXMI 注入器框架 ({basePkg.Version})...");
                await _installer.InstallPackageAsync(basePkg, rootFolder, null, progress, ct);
            }
            else
            {
                progress?.Report("XXMI 注入器框架已是最新版本，跳过。");
            }

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
                }
            }

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

            // Ensure the Mods folder exists — some game packages may omit the (empty) dir from the zip.
            Directory.CreateDirectory(modsFolder);

            // Re-verify after install.
            (filesOk, modsOk) = _installer.CheckGamePackageFiles(miFolder);
            if (!filesOk)
                issues.Add("安装后校验未通过：未找到注入器文件 (d3d11.dll / d3dx.ini)");
            if (!modsOk)
                issues.Add("安装后校验未通过：未找到 Mods 文件夹");

            // Persist idempotency marker only after everything succeeded.
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

    // ---- Helpers ------------------------------------------------------------

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

    /// <summary>Signature files of the shared XXMI base package, placed at the XXMI root.</summary>
    private static readonly string[] BasePackageSignatureFiles = { "3dmloader.dll", "d3d11.dll", "d3dcompiler_47.dll" };

    /// <summary>True when all base package files are present at the XXMI root.</summary>
    private bool BaseFilesOk(string rootFolder)
    {
        if (!Directory.Exists(rootFolder)) return false;
        try
        {
            return BasePackageSignatureFiles.All(f => File.Exists(Path.Combine(rootFolder, f)));
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to inspect XXMI root {Root}", rootFolder);
            return false;
        }
    }

    /// <summary>Launcher config file that carries user-edited settings; preserved across package updates.</summary>
    private const string LauncherConfigFileName = "XXMI Launcher Config.json";

    /// <summary>True when the XXMI Launcher GUI executable is present at the root.</summary>
    private static bool LauncherFilesOk(string rootFolder)
        => File.Exists(Path.Combine(rootFolder, "Resources", "Bin", "XXMI Launcher.exe"));

    private static ModEnvPackageAction EvaluateAction(IReadOnlyDictionary<string, string> installed, string packageId,
        ModEnvPackage pkg, bool filesOk)
    {
        if (!installed.TryGetValue(packageId, out var installedVer) || string.IsNullOrWhiteSpace(installedVer))
            return ModEnvPackageAction.NotInstalled;

        if (!filesOk)
            return ModEnvPackageAction.NeedsRepair;

        return string.Equals(installedVer, pkg.Version, StringComparison.Ordinal)
            ? ModEnvPackageAction.UpToDate
            : ModEnvPackageAction.UpdateAvailable;
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
