using GIMI_ModManager.Core.Services.WuwaModFixer;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>引擎就绪检查的结果。失败时 <see cref="Error"/> 是能直接展示给用户的一句话。</summary>
public sealed record WuwaModFixerReadyResult(
    bool IsReady,
    string? Error,
    WuwaConfigInfo? Config,
    string? CliPath,
    string? ConfigPath);

/// <summary>
/// 修复引擎的门面：释放资源、读配置、执行单个 Mod。
///
/// 界面只跟这一层打交道，不直接碰进程/资源；CLI 被杀软删除后由这里强制重放一次。
/// </summary>
internal sealed class WuwaModFixerEngine
{
    private readonly WuwaModFixerProvisioner _provisioner;
    private readonly WuwaModFixerConfigService _configService;
    private readonly WuwaModCliRunner _runner;
    private readonly ILogger _logger;

    public WuwaModFixerEngine(
        WuwaModFixerProvisioner provisioner,
        WuwaModFixerConfigService configService,
        WuwaModCliRunner runner,
        ILogger logger)
    {
        _provisioner = provisioner;
        _configService = configService;
        _runner = runner;
        _logger = logger.ForContext<WuwaModFixerEngine>();
    }

    /// <summary>
    /// 确保 CLI 与配置都可用，并返回当前配置摘要。
    /// <paramref name="forceProvision"/> 用于「刚执行失败、怀疑 CLI 被删/损坏」后的重试。
    /// </summary>
    public async Task<WuwaModFixerReadyResult> EnsureReadyAsync(
        bool forceProvision = false,
        CancellationToken cancellationToken = default)
    {
        var cliPath = await _provisioner.EnsureCliAsync(forceProvision, cancellationToken);
        if (cliPath is null)
        {
            const string error = "修复引擎不可用：未找到内置 CLI。请运行 Build/FetchWuwaModFixer.py 后重启 JASM。";
            _logger.Warning("[修复引擎] {Error}", error);
            return new WuwaModFixerReadyResult(false, error, null, null, null);
        }

        var configPath = await _provisioner.EnsureConfigAsync(cancellationToken);
        if (configPath is null)
        {
            const string error = "修复配置不可用：内置配置缺失或无效。";
            _logger.Warning("[修复引擎] {Error}", error);
            return new WuwaModFixerReadyResult(false, error, null, cliPath, null);
        }

        var config = await _configService.ReadLocalAsync(configPath, cancellationToken);
        if (config is null)
        {
            const string error = "修复配置校验未通过，请更新配置或检查内置版本。";
            _logger.Warning("[修复引擎] {Error}", error);
            return new WuwaModFixerReadyResult(false, error, null, cliPath, configPath);
        }

        return new WuwaModFixerReadyResult(true, null, config, cliPath, configPath);
    }

    public Task<WuwaConfigInfo?> ReadConfigInfoAsync(CancellationToken cancellationToken = default) =>
        _configService.ReadLocalAsync(WuwaModFixerProvisioner.ConfigPath, cancellationToken);

    public Task<bool> CheckForConfigUpdateAsync(CancellationToken cancellationToken = default) =>
        _configService.CheckForUpdateAsync(WuwaModFixerProvisioner.ConfigPath, cancellationToken);

    public Task<WuwaConfigInfo> UpdateConfigAsync(CancellationToken cancellationToken = default) =>
        _configService.UpdateAsync(WuwaModFixerProvisioner.ConfigPath, cancellationToken);

    /// <summary>
    /// 执行一个 Mod。CLI/配置缺失直接抛异常，由会话层决定是否重放后重试；
    /// 其余失败（非零退出、超时）以结果对象返回，不抛。
    /// </summary>
    public Task<WuwaModCliRunResult> RunModAsync(
        WuwaFixTarget target,
        WuwaFixOptions options,
        IProgress<WuwaModCliEvent>? progress,
        CancellationToken cancellationToken = default)
    {
        return _runner.RunAsync(
            WuwaModFixerProvisioner.CliPath,
            WuwaModFixerProvisioner.ConfigPath,
            target.ModPath,
            options,
            progress,
            cancellationToken);
    }
}