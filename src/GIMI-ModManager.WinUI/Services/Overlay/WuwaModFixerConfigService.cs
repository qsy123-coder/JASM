using System.Net;
using System.Text;
using GIMI_ModManager.Core.Services.WuwaModFixer;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>
/// 修复配置的读取、静默检查与手动更新。
///
/// 源顺序是 Gitee → GitHub raw；两处都失败就保留本地旧配置 —— 配置过期只会修不到新角色，
/// 把旧配置弄坏则会让整个修复页不可用。
/// </summary>
internal sealed class WuwaModFixerConfigService
{
    public const string HttpClientName = "WuwaModFixer";

    private const int MaxConfigBytes = 2 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public WuwaModFixerConfigService(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger.ForContext<WuwaModFixerConfigService>();
    }

    /// <summary>读取并校验本地配置；文件缺失/损坏返回 null。</summary>
    public async Task<WuwaConfigInfo?> ReadLocalAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(configPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(configPath, cancellationToken);
            var validation = WuwaModFixerConfigValidator.Validate(json, WuwaModFixerProtocol.CliVersion);
            return validation.IsValid ? validation.Info : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(e, "[修复引擎] 读取本地配置失败");
            return null;
        }
    }

    /// <summary>
    /// 静默检查上游有没有更新的配置。任何失败都返回 false（不打扰用户）。
    /// </summary>
    public async Task<bool> CheckForUpdateAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var local = await ReadLocalAsync(configPath, cancellationToken);
            var remote = await DownloadValidatedAsync(cancellationToken);

            if (remote is null)
                return false;

            if (local is null)
                return true;

            return WuwaVersion.Compare(remote.Info.CurrentVersion, local.CurrentVersion) > 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e)
        {
            _logger.Debug(e, "[修复引擎] 配置静默检查失败");
            return false;
        }
    }

    /// <summary>
    /// 手动更新：下载 → 校验 → 备份旧文件 → 原子替换。
    /// 失败抛 <see cref="InvalidOperationException"/>，由界面显示原因；旧配置不变。
    /// </summary>
    public async Task<WuwaConfigInfo> UpdateAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        var remote = await DownloadValidatedAsync(cancellationToken)
                     ?? throw new InvalidOperationException("无法从上游获取有效的修复配置");

        var directory = Path.GetDirectoryName(configPath)
                        ?? throw new InvalidOperationException("配置路径没有父目录");
        Directory.CreateDirectory(directory);

        var tempPath = configPath + ".new." + Environment.ProcessId;
        try
        {
            if (File.Exists(configPath))
            {
                try
                {
                    File.Copy(configPath, configPath + ".bak", overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _logger.Warning(e, "[修复引擎] 配置备份失败，继续替换");
                }
            }

            // 下载得到的是字符串；重新写一遍保证落盘编码固定为 UTF-8 无 BOM。
            await File.WriteAllTextAsync(tempPath, remote.Json, new UTF8Encoding(false), cancellationToken);
            File.Move(tempPath, configPath, overwrite: true);

            // 标记只用于诊断；写不上不影响功能
            try
            {
                var markerPath = Path.Combine(directory, WuwaModFixerProtocol.ConfigMarkerFileName);
                await File.WriteAllTextAsync(markerPath, remote.Info.CurrentVersion, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger.Debug(e, "[修复引擎] 配置版本标记写入失败");
            }

            _logger.Information("[修复引擎] 配置已更新到 {Version}", remote.Info.CurrentVersion);
            return remote.Info;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw new InvalidOperationException("写入修复配置失败：" + e.Message, e);
        }
    }

    private async Task<DownloadedConfig?> DownloadValidatedAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        foreach (var source in WuwaConfigUpdateSources.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await client.GetAsync(
                    source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Debug("[修复引擎] 配置源 {Source} 返回 {Status}", source, response.StatusCode);
                    continue;
                }

                if (!WuwaConfigUpdateSources.IsAllowed(response.RequestMessage?.RequestUri))
                {
                    _logger.Warning("[修复引擎] 配置重定向到了不在白名单的主机：{Uri}",
                        response.RequestMessage?.RequestUri);
                    continue;
                }

                if (response.Content.Headers.ContentLength is > MaxConfigBytes)
                    continue;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (Encoding.UTF8.GetByteCount(json) > MaxConfigBytes)
                    continue;

                var validation = WuwaModFixerConfigValidator.Validate(
                    json, WuwaModFixerProtocol.CliVersion);

                if (!validation.IsValid || validation.Info is null)
                {
                    _logger.Warning("[修复引擎] 配置校验失败（{Source}）：{Reason}", source, validation.Message);
                    continue;
                }

                return new DownloadedConfig(json, validation.Info);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                _logger.Debug(e, "[修复引擎] 配置源 {Source} 请求失败", source);
            }
        }

        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 临时文件残留不影响旧配置
        }
    }

    private sealed record DownloadedConfig(string Json, WuwaConfigInfo Info);
}