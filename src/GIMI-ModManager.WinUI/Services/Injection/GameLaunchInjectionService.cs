using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services.WuwaUhd;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Options;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.ModEnv;
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
    private const string MapperDllName = "Jasm.UhdMapper.dll";
    private const string MapperDirectoryName = "UhdRuntime";

    private readonly ILocalSettingsService _localSettingsService;
    private readonly GameInstallPathDetector _gameInstallPathDetector;
    private readonly ElevatorService _elevatorService;
    private readonly SelectedGameService _selectedGameService;
    private readonly UhdActiveModService _uhdActiveModService;
    private readonly ILogger _logger;

    public GameLaunchInjectionService(
        ILocalSettingsService localSettingsService,
        GameInstallPathDetector gameInstallPathDetector,
        ElevatorService elevatorService,
        SelectedGameService selectedGameService,
        UhdActiveModService uhdActiveModService,
        ILogger logger)
    {
        _localSettingsService = localSettingsService;
        _gameInstallPathDetector = gameInstallPathDetector;
        _elevatorService = elevatorService;
        _selectedGameService = selectedGameService;
        _uhdActiveModService = uhdActiveModService;
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

        var mapperPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JASM", MapperDirectoryName, MapperDllName);
        if (!File.Exists(mapperPath))
            mapperPath = null;

        var overlay = await _localSettingsService
            .ReadSettingAsync<OverlaySettings>(OverlaySettings.Key, SettingScope.App)
            .ConfigureAwait(false);

        // 先落盘 active-mod.json：映射 DLL 在进程启动时就会读它，必须早于 CreateProcess。
        // 没有注入就不需要这份清单；写失败只记日志，不阻断启动。
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