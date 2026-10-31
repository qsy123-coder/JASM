using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services.WuwaUhd;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Options;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.ModEnv;
using GIMI_ModManager.WinUI.Services.Overlay;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Injection;

/// <summary>一次「内置注入启动」的结果。</summary>
public sealed record InjectionLaunchResult(bool Success, int ProcessId, string? Error, string? Reason)
{
    public static InjectionLaunchResult Ok(int pid) => new(true, pid, null, null);
    public static InjectionLaunchResult Fail(string error, string? reason = null) => new(false, 0, error, reason);
}

/// <summary>
/// 鸣潮「内置注入启动」：解析游戏 shipping exe 与 WWMI 的 d3d11.dll，交给提权助手
/// 挂起创建 + 注入 + 恢复。Phase 1 只注入 d3d11；`Jasm.UhdMapper.dll` 存在时一并注入（Phase 2）。
///
/// 默认关闭；失败时由 <see cref="CommandHandlerService"/> 回退现有 XXMI Launcher 命令。
/// </summary>
public sealed class GameLaunchInjectionService
{
    private readonly ILocalSettingsService _localSettingsService;
    private readonly GameInstallPathDetector _gameInstallPathDetector;
    private readonly ElevatorService _elevatorService;
    private readonly SelectedGameService _selectedGameService;
    private readonly UhdActiveModService _uhdActiveModService;
    private readonly UhdMapperProvisioner _uhdMapperProvisioner;
    private readonly UhdAliasImportService _uhdAliasImportService;
    private readonly ILogger _logger;

    public GameLaunchInjectionService(
        ILocalSettingsService localSettingsService,
        GameInstallPathDetector gameInstallPathDetector,
        ElevatorService elevatorService,
        SelectedGameService selectedGameService,
        UhdActiveModService uhdActiveModService,
        UhdMapperProvisioner uhdMapperProvisioner,
        UhdAliasImportService uhdAliasImportService,
        ILogger logger)
    {
        _localSettingsService = localSettingsService;
        _gameInstallPathDetector = gameInstallPathDetector;
        _elevatorService = elevatorService;
        _selectedGameService = selectedGameService;
        _uhdActiveModService = uhdActiveModService;
        _uhdMapperProvisioner = uhdMapperProvisioner;
        _uhdAliasImportService = uhdAliasImportService;
        _logger = logger.ForContext<GameLaunchInjectionService>();
    }

    public async Task<GameLaunchInjectionSettings> ReadSettingsAsync(CancellationToken ct = default)
    {
        return await _localSettingsService
            .ReadOrCreateSettingAsync<GameLaunchInjectionSettings>(GameLaunchInjectionSettings.Key,
                SettingScope.App)
            .ConfigureAwait(false);
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
        => (await ReadSettingsAsync(ct).ConfigureAwait(false)).Enabled;

    /// <summary>
    /// 尝试直启 + 注入。返回失败时调用方应回退到原启动命令（XXMI Launcher）。
    /// </summary>
    public async Task<InjectionLaunchResult> TryLaunchAsync(CancellationToken ct = default)
    {
        var settings = await ReadSettingsAsync(ct).ConfigureAwait(false);
        if (!settings.Enabled)
            return InjectionLaunchResult.Fail("内置注入启动未开启");

        var selectedGame = await _selectedGameService.GetSelectedGameAsync().ConfigureAwait(false);
        if (!string.Equals(selectedGame, "WuWa", StringComparison.OrdinalIgnoreCase))
            return InjectionLaunchResult.Fail("当前游戏不是鸣潮", "not-wuwa");

        var gameInfo = await GameService.GetGameInfoAsync(SupportedGames.WuWa).ConfigureAwait(false);
        var modEnv = gameInfo?.ModEnv;
        var subDirName = string.IsNullOrWhiteSpace(modEnv?.SubDirName) ? "WWMI" : modEnv!.SubDirName;
        var rootDirName = string.IsNullOrWhiteSpace(modEnv?.RootDirName) ? "XXMI" : modEnv!.RootDirName;

        var install = await _gameInstallPathDetector.DetectAsync(ct).ConfigureAwait(false);
        if (install is null)
            return InjectionLaunchResult.Fail("找不到鸣潮安装位置", "no-install");

        var gameExe = ResolveShippingExecutable(install.InstallDir)
                      ?? _gameInstallPathDetector.FindGameExecutable(install.InstallDir);
        if (string.IsNullOrEmpty(gameExe) || !File.Exists(gameExe))
            return InjectionLaunchResult.Fail("找不到鸣潮可执行文件", "no-game-exe");

        var options = await _localSettingsService
            .ReadSettingAsync<ModManagerOptions>(ModManagerOptions.Section)
            .ConfigureAwait(false);
        var xxmiRoot = options?.XxmiRootFolderPath;
        if (string.IsNullOrWhiteSpace(xxmiRoot))
        {
            var driveRoot = Path.GetPathRoot(install.InstallDir);
            if (string.IsNullOrWhiteSpace(driveRoot))
                return InjectionLaunchResult.Fail("无法推导 XXMI 根目录", "no-xxmi-root");
            xxmiRoot = Path.Combine(driveRoot, rootDirName);
        }

        var d3d11Path = Path.Combine(xxmiRoot, subDirName, "d3d11.dll");
        if (!File.Exists(d3d11Path))
            return InjectionLaunchResult.Fail("找不到 WWMI 的 d3d11.dll，请先完成 Mod 环境配置", "no-d3d11");

        // 映射 DLL 由内嵌资源按需释放；没有映射 DLL 时 Phase 1 仍可只注入 d3d11
        var mapperPath = _uhdMapperProvisioner.EnsureProvisioned();

        var overlay = await _localSettingsService
            .ReadSettingAsync<OverlaySettings>(OverlaySettings.Key, SettingScope.App)
            .ConfigureAwait(false);

        // 启动前自动把 Rocket 学到的 UHD 配对补成 mod ini 里的 _UHD_ 段。
        // 这条路径由 3DMigoto 在 draw 时接管，是本项目实机验证过的可靠路径；
        // 只写有变化的文件（每个文件先备份），失败不影响启动。
        // 这样用户不需要每次手动点「导入 Rocket UHD 映射」。
        await EnsureUhdSectionsAsync(overlay?.LastSelectedCharacter, ct).ConfigureAwait(false);

        // 再落盘 active-mod.json：映射 DLL 在进程启动时就会读它，必须早于 CreateProcess。
        // 已被 _UHD_ 段覆盖的 hash 会在清单里被排除；写失败只记日志，不阻断启动。
        _uhdActiveModService.WriteForCharacter(overlay?.LastSelectedCharacter);

        var arguments = BuildArguments(settings.LaunchArguments, overlay);
        var workingDirectory = Path.GetDirectoryName(gameExe) ?? install.InstallDir;
        var request = new ElevatorLaunchInjectRequest(gameExe, d3d11Path, workingDirectory, arguments, mapperPath);

        var result = await _elevatorService.TryLaunchAndInjectAsync(request, ct).ConfigureAwait(false);
        if (result.Kind == ElevatorLaunchInjectReply.Ok)
        {
            _logger.Information("[注入启动] 已启动 pid={Pid} exe={Exe} dll={Dll} mapper={Mapper}",
                result.ProcessId, gameExe, d3d11Path, mapperPath ?? "(无)");
            return InjectionLaunchResult.Ok(result.ProcessId);
        }

        var message = ElevatorLaunchInjectProtocol.DescribeFailure(result.FailureReason);
        _logger.Warning("[注入启动] 失败 reason={Reason} message={Message}", result.FailureReason, message);
        return InjectionLaunchResult.Fail(message, result.FailureReason);
    }

    /// <summary>
    /// 对当前角色所有已启用的 mod 补齐 UHD 覆盖段。学习库缺失/该角色没学过/mod 不需要补，
    /// 都只是「没变化」，不当作启动失败。真正写盘时由导入服务逐个文件备份。
    /// </summary>
    private async Task EnsureUhdSectionsAsync(string? characterInternalName, CancellationToken ct)
    {
        try
        {
            var selection = _uhdActiveModService.ResolveSelection(characterInternalName);
            if (selection is null)
                return;

            var result = await _uhdAliasImportService
                .ApplyAsync(selection.RoleName, selection.EnabledModPaths, ct)
                .ConfigureAwait(false);

            _logger.Information("[UHD映射] 启动前自动补齐：success={Success} files={Files} sections={Sections} message={Message}",
                result.Success, result.FilesChanged, result.SectionsAdded, result.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // 自动补齐只是「让 mod 在 UHD 下正确显示」的优化，绝不能阻断游戏启动。
            _logger.Warning(e, "[UHD映射] 启动前自动补齐失败，继续启动");
        }
    }

    /// <summary>
    /// 直接启动必须指向 UE 的 shipping exe，而不是安装根目录的启动器；否则注入的是启动器进程。
    /// 顺序按 WuWa 常见布局排列。
    /// </summary>
    public static string? ResolveShippingExecutable(string installDir)
    {
        var candidates = new[]
        {
            Path.Combine(installDir, "Client", "Binaries", "Win64", "Client-Win64-Shipping.exe"),
            Path.Combine(installDir, "Wuthering Waves Game", "Client", "Binaries", "Win64",
                "Client-Win64-Shipping.exe"),
            Path.Combine(installDir, "Wuthering Waves Game", "Client", "Binaries", "Win64",
                "Wuthering Waves.exe"),
            Path.Combine(installDir, "Client", "Binaries", "Win64", "Wuthering Waves.exe"),
            Path.Combine(installDir, "Wuthering Waves.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>直接启动必须显式带 <c>-dx11</c>（WWMI 只支持 DX11）；资源档位沿用浮窗里的选择。</summary>
    private static string BuildArguments(string userArguments, OverlaySettings? overlay)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(userArguments))
            parts.Add(userArguments.Trim());

        if (!parts.Any(p => p.Contains("-dx11", StringComparison.OrdinalIgnoreCase)))
            parts.Add("-dx11");

        var tierArg = overlay is null
            ? null
            : WuwaResourceTierExtensions.FromIndex(overlay.UhdResourceTierIndex ?? 0).ToLaunchArgument();
        if (tierArg is not null)
            parts.Add(tierArg);

        return string.Join(' ', parts);
    }
}