using GIMI_ModManager.Core.Services.WuwaUhd;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Injection;

/// <summary>
/// 把内嵌在主 exe 里的 UHD 映射 DLL 释放到
/// <c>%LOCALAPPDATA%\JASM\UhdRuntime\Jasm.UhdMapper.dll</c>。
///
/// 与 <see cref="ElevatorProvisioner"/> 同一套路（临时文件 + <c>File.Move(overwrite)</c> +
/// 版本标记），区别在于目标目录：提权助手只允许注入父目录名为 <c>UhdRuntime</c> 的映射 DLL，
/// 所以这个位置不能换成 <c>%LOCALAPPDATA%\JASM</c> 根目录。
/// </summary>
public sealed class UhdMapperProvisioner
{
    private readonly ILogger _logger;

    public static string ProvisionedDllPath { get; } =
        Path.Combine(UhdActiveModStore.RuntimeDirectory, UhdMapperProvisioning.FileName);

    private static string MarkerPath { get; } =
        Path.Combine(UhdActiveModStore.RuntimeDirectory, UhdMapperProvisioning.VersionMarkerFileName);

    /// <summary>主程序版本：标记与「要不要重写」都以它为准（自更新后必然变化）。</summary>
    private static string CurrentVersion { get; } =
        typeof(UhdMapperProvisioner).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public UhdMapperProvisioner(ILogger logger)
    {
        _logger = logger.ForContext<UhdMapperProvisioner>();
    }

    /// <summary>
    /// 需要时把内嵌 DLL 写到磁盘，返回磁盘上可用的路径；没有内嵌资源且磁盘上也没有时返回 null。
    /// 写失败（游戏正加载着这个 DLL、杀软拦截）不抛，沿用磁盘上的现有副本。
    /// </summary>
    public string? EnsureProvisioned()
    {
        var existing = File.Exists(ProvisionedDllPath) ? ProvisionedDllPath : null;

        using var resource = typeof(UhdMapperProvisioner).Assembly
            .GetManifestResourceStream(UhdMapperProvisioning.EmbeddedResourceName);

        if (resource is null)
        {
            // 日常 dotnet build / 未把 CI 产物放进 src/Jasm.UhdMapper/build 的构建：没有内嵌资源。
            // 不是错误 —— 开发时可以把 DLL 手动放到目标目录，或退回「只注入 d3d11」。
            _logger.Debug("[UHD映射] 这份构建没有内嵌映射 DLL，跳过释放");
            return existing;
        }

        if (!UhdMapperProvisioning.ShouldProvision(existing is not null, ReadMarker(), CurrentVersion))
            return existing;

        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(UhdActiveModStore.RuntimeDirectory);

            tempPath = $"{ProvisionedDllPath}.new.{Environment.ProcessId}";
            using (var file = File.Create(tempPath))
            {
                resource.CopyTo(file);
            }

            File.Move(tempPath, ProvisionedDllPath, overwrite: true);
            File.WriteAllText(MarkerPath, CurrentVersion);

            _logger.Information("[UHD映射] 已释放映射 DLL 到 {Path}（释放者 JASM {Version}）",
                ProvisionedDllPath, CurrentVersion);
            return ProvisionedDllPath;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 游戏正带着旧 DLL 跑（文件被锁）或杀软拦了写入：这次换不了，下次启动重试。
            // 标记不更新，于是 ShouldProvision 下次仍为 true。
            _logger.Warning(e, "[UHD映射] 释放映射 DLL 失败，继续用磁盘上的现有副本");
            TryDeleteTemp(tempPath);
            return existing;
        }
    }

    private static string? ReadMarker()
    {
        try
        {
            return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath).Trim() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void TryDeleteTemp(string? tempPath)
    {
        if (tempPath is null)
            return;

        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(e, "[UHD映射] 清理临时文件 {Path} 失败", tempPath);
        }
    }
}