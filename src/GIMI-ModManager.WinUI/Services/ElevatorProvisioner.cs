using GIMI_ModManager.Core.Helpers;
using Serilog;

namespace GIMI_ModManager.WinUI.Services;

/// <summary>
/// 把内嵌在主 exe 里的提权助手按需释放到磁盘 —— 单 exe 版拿到 <c>Elevator.exe</c> 的唯一途径。
///
/// 单 exe 分发形态里只有一个 exe，助手不可能作为散文件躺在旁边，所以它在构建期被内嵌进程序集
/// （见 GIMI-ModManager.WinUI.csproj 的 EmbeddedResource），运行时落到 <c>%LOCALAPPDATA%\JASM\</c>。
/// 用户磁盘上因此**始终只有一个 exe**。这不是缓存优化 —— <c>Process.Start(..., Verb = "runas")</c>
/// 的目标必须是磁盘上的真实文件，没有这一步就没法提权启动助手。
///
/// 什么时候写由 <see cref="ElevatorProvisioning.ShouldProvision"/> 定：同目录那份已经够用就一行都不写，
/// 每个主程序版本也最多写一次 —— 「往磁盘落一个 exe」是杀软会盯的动作，能少做就少做。
/// </summary>
public sealed class ElevatorProvisioner
{
    private readonly ILogger _logger;

    /// <summary>释放目录：沿用 <c>%LOCALAPPDATA%\JASM</c>（应用自己的数据目录，见 ModEnvBackupService 等）。</summary>
    public static string ProvisionDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JASM");

    /// <summary>释放后的助手路径。folder 版不写这里 —— 它用的是同目录那份，由调用方另行探测。</summary>
    public static string ProvisionedHelperPath { get; } =
        Path.Combine(ProvisionDirectory, ElevatorService.ElevatorProcessName);

    private static string MarkerPath { get; } =
        Path.Combine(ProvisionDirectory, ElevatorProvisioning.VersionMarkerFileName);

    /// <summary>
    /// 主程序自身的版本，标记与「要不要重写」都拿它当基准。
    /// 取自程序集版本（<c>VersionPrefix</c> 由 release-please 维护）——自更新换掉主 exe 后它必然变化，
    /// 于是标记失配、下次启动重写助手：助手版本的推进不需要另立一套机制。
    /// </summary>
    private static string CurrentVersion { get; } =
        typeof(ElevatorProvisioner).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public ElevatorProvisioner(ILogger logger)
    {
        _logger = logger.ForContext<ElevatorProvisioner>();
    }

    /// <summary>
    /// 需要时把内嵌的助手写到磁盘，返回**磁盘上那份可用的**助手的路径。
    /// 没有内嵌资源、磁盘上也没有可用副本时返回 null（调用方据此进「没有可用助手」的既有分支）。
    /// </summary>
    /// <param name="siblingVersion">
    /// 随包安装的同目录助手的 FileVersion（null = 没有这个文件，或版本读不出来）。
    /// 由调用方先探测再传进来，本类不重复扫盘。
    /// </param>
    public string? EnsureProvisioned(string? siblingVersion)
    {
        var existing = File.Exists(ProvisionedHelperPath) ? ProvisionedHelperPath : null;

        using var resource = typeof(ElevatorProvisioner).Assembly
            .GetManifestResourceStream(ElevatorProvisioning.EmbeddedResourceName);

        if (resource is null)
        {
            // 开发机构的日常 dotnet build、或带 ExcludeElevator 打出来的包：压根没内嵌资源。
            // 不是错误 —— 退回「磁盘上有没有现成的」这条既有行为。
            _logger.Debug("[ElevatorProvisioner] 这份构建没有内嵌助手，跳过释放");
            return existing;
        }

        // 注意这里兜了 existing：ShouldProvision 为 false 还可能是「同目录那份够用、释放副本根本不存在」
        // 那一支，此时把路径原样返回会交出一个不存在的文件。
        if (!ElevatorProvisioning.ShouldProvision(siblingVersion, existing is not null, ReadMarker(), CurrentVersion))
            return existing;

        string? tempPath = null;
        try
        {
            Directory.CreateDirectory(ProvisionDirectory);

            // 临时名带 pid：两个实例同时释放时各写各的，最后由 File.Move 的覆盖语义决出胜者（两边字节相同）
            tempPath = $"{ProvisionedHelperPath}.new.{Environment.ProcessId}";
            using (var file = File.Create(tempPath))
            {
                resource.CopyTo(file);
            }

            File.Move(tempPath, ProvisionedHelperPath, overwrite: true);
            File.WriteAllText(MarkerPath, CurrentVersion);

            _logger.Information("[ElevatorProvisioner] 已释放助手到 {Path}（释放者 JASM {Version}）",
                ProvisionedHelperPath, CurrentVersion);
            return ProvisionedHelperPath;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 两种常见原因，都不该让启动失败：
            // ① 助手正跑着（提权进程）锁住了目标文件 —— 换不了，主窗口退出时会 kill 它，下次启动再换；
            // ② 杀软拦了这次写入。
            // 标记**不更新**，于是下次启动会自然重试。
            _logger.Warning(e, "[ElevatorProvisioner] 释放助手失败，继续用磁盘上的现有副本");

            TryDeleteTemp(tempPath);
            return existing;
        }
    }

    /// <summary>
    /// 读标记文件。读不到 / 文件不在都返回 null —— <see cref="ElevatorProvisioning"/> 把 null 当「要重写」，方向是安全的。
    /// </summary>
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

    /// <summary>清掉释放失败时留下的临时文件；清不掉也无所谓，它带 pid 后缀，下次不会撞上同名。</summary>
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
            _logger.Debug(e, "[ElevatorProvisioner] 清理临时文件 {Path} 失败", tempPath);
        }
    }
}