using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GIMI_ModManager.Core.Services.WuwaModFixer;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>
/// 把内嵌的修复 CLI 与配置释放到 <c>%LOCALAPPDATA%\JASM\WuwaModFixer</c>。
///
/// 为什么不用程序目录：单文件版的 <c>AppContext.BaseDirectory</c> 是 <c>%TEMP%</c> 下的
/// 自解压目录，配置更新写进去会随临时目录清理一起消失；folder 版又可能装在 Program Files
/// （只读）。所以可变数据一律落在 LocalAppData，三种分发形态的行为完全一致。
///
/// 释放手法沿用 ElevatorProvisioner：临时文件名带 PID → <c>File.Move(overwrite)</c> → 最后写标记。
/// 失败时保留旧副本，下次再试；不会因为一次写盘失败把浮窗打挂。
/// </summary>
internal sealed class WuwaModFixerProvisioner
{
    private readonly ILogger _logger;

    public WuwaModFixerProvisioner(ILogger logger)
    {
        _logger = logger.ForContext<WuwaModFixerProvisioner>();
    }

    /// <summary>运行目录：跨 folder / 单文件 / 自包含一致。</summary>
    public static string RuntimeDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JASM", "WuwaModFixer");

    public static string CliPath => Path.Combine(RuntimeDirectory, WuwaModFixerProtocol.CliFileName);
    public static string ConfigPath => Path.Combine(RuntimeDirectory, WuwaModFixerProtocol.ConfigFileName);

    private static string CliMarkerPath =>
        Path.Combine(RuntimeDirectory, WuwaModFixerProtocol.CliMarkerFileName);

    private static string ConfigMarkerPath =>
        Path.Combine(RuntimeDirectory, WuwaModFixerProtocol.ConfigMarkerFileName);

    /// <summary>
    /// 确保 CLI 可用。返回释放后的路径；内嵌资源缺失且本地也没有可用副本时返回 null。
    /// <paramref name="force"/> 用于「CLI 被杀软删了/启动失败」后的重放。
    /// </summary>
    public async Task<string?> EnsureCliAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCliCore(force);
        }, cancellationToken);

        return File.Exists(CliPath) ? CliPath : null;
    }

    /// <summary>确保配置可用。返回配置路径；两边都不可用时返回 null。</summary>
    public async Task<string?> EnsureConfigAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConfigCore();
        }, cancellationToken);

        return File.Exists(ConfigPath) ? ConfigPath : null;
    }

    /// <summary>计算文件 SHA256（小写十六进制）。</summary>
    internal static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private void EnsureCliCore(bool force)
    {
        Directory.CreateDirectory(RuntimeDirectory);

        var extractedExists = File.Exists(CliPath);
        var existingSha = extractedExists ? SafeComputeSha256(CliPath) : null;
        var marker = SafeReadText(CliMarkerPath);

        using var embedded = OpenEmbedded(WuwaModFixerProtocol.CliEmbeddedResourceName);
        var shouldProvision = force
                              || WuwaModFixerProvisioning.ShouldProvision(
                                  embedded is not null, extractedExists, marker,
                                  WuwaModFixerProtocol.CliVersion, WuwaModFixerProtocol.CliSha256, existingSha);

        if (!shouldProvision)
        {
            // 哈希对、只是标记缺了（上次在写标记前退出）—— 补标记即可，不必再写 4MB。
            if (extractedExists && existingSha is not null)
                SafeWriteText(CliMarkerPath,
                    WuwaModFixerProtocol.BuildMarker(WuwaModFixerProtocol.CliVersion, existingSha));
            return;
        }

        if (embedded is null)
        {
            _logger.Warning("[修复引擎] 内嵌 CLI 不存在，且本地副本不可用；请先运行 Build/FetchWuwaModFixer.py");
            return;
        }

        var tempPath = CliPath + ".new." + Environment.ProcessId;
        try
        {
            using (var target = File.Create(tempPath))
                embedded.CopyTo(target);

            File.Move(tempPath, CliPath, overwrite: true);
            File.WriteAllText(CliMarkerPath,
                WuwaModFixerProtocol.BuildMarker(
                    WuwaModFixerProtocol.CliVersion, WuwaModFixerProtocol.CliSha256));
            _logger.Information("[修复引擎] CLI 已释放：{Path}", CliPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(e, "[修复引擎] CLI 释放失败，保留旧副本");
            TryDelete(tempPath);
        }
    }

    private void EnsureConfigCore()
    {
        Directory.CreateDirectory(RuntimeDirectory);

        var embeddedText = ReadEmbeddedText(WuwaModFixerProtocol.ConfigEmbeddedResourceName);
        var embeddedValidation = WuwaModFixerConfigValidator.Validate(
            embeddedText, WuwaModFixerProtocol.CliVersion);

        if (!embeddedValidation.IsValid || embeddedValidation.Info is null)
        {
            _logger.Warning("[修复引擎] 内嵌配置不可用：{Reason}", embeddedValidation.Message);
            return;
        }

        var localExists = File.Exists(ConfigPath);
        var localText = localExists ? SafeReadText(ConfigPath) : null;
        var localValidation = WuwaModFixerConfigValidator.Validate(
            localText, WuwaModFixerProtocol.CliVersion);

        var shouldReplace = !localExists
                            || WuwaModFixerProvisioning.ShouldReplaceConfig(
                                localValidation.Info?.CurrentVersion,
                                embeddedValidation.Info.CurrentVersion,
                                localValidation.IsValid);

        if (!shouldReplace)
            return;

        var tempPath = ConfigPath + ".new." + Environment.ProcessId;
        try
        {
            if (localExists)
            {
                try
                {
                    File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _logger.Warning(e, "[修复引擎] 旧配置备份失败，继续替换");
                }
            }

            File.WriteAllText(tempPath, embeddedText, new UTF8Encoding(false));
            File.Move(tempPath, ConfigPath, overwrite: true);
            File.WriteAllText(ConfigMarkerPath, embeddedValidation.Info.CurrentVersion);
            _logger.Information("[修复引擎] 配置已释放：{Version}", embeddedValidation.Info.CurrentVersion);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(e, "[修复引擎] 配置释放失败，保留旧配置");
            TryDelete(tempPath);
        }
    }

    private static Stream? OpenEmbedded(string resourceName) =>
        typeof(WuwaModFixerProvisioner).GetTypeInfo().Assembly
            .GetManifestResourceStream(resourceName);

    private static string? ReadEmbeddedText(string resourceName)
    {
        using var stream = OpenEmbedded(resourceName);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? SafeReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SafeWriteText(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 标记写不上不影响功能；下次启动按哈希重新判断
        }
    }

    private static string? SafeComputeSha256(string path)
    {
        try
        {
            return ComputeSha256(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
            // 临时文件残留会在下次释放时被新 PID 覆盖
        }
    }
}