using System.Diagnostics;
using GIMI_ModManager.Core.Helpers;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.AppManagement;

/// <summary>
/// 去提权重启时的**交接凭条**（一个落盘的小文件）的读写。
///
/// <para>
/// 为什么需要它：去提权只能请 explorer 代起进程，而 <c>explorer.exe "路径"</c> 传不了参数，
/// 于是新的一份与正在退出的那一份映像路径完全相同、命令行也相同 —— 单实例检查会把新进程
/// 当成「已经有一个 JASM 在跑」而把它挡回去，用户看到的是「点了重启，什么都没发生」。
/// 凭条里写着**前任的 pid**，新进程据此知道该等谁；等它退出再继续启动，两份就不会并存
/// （两份共用 <c>%LOCALAPPDATA%\JASM</c>，同时跑会互相覆盖）。
/// </para>
///
/// <para>
/// 时效与格式的判定在 <see cref="UnelevatedRelaunchProtocol"/>（纯逻辑、有单测）；
/// 这里只管文件本身。
/// </para>
/// </summary>
internal static class UnelevatedRelaunchMarker
{
    /// <summary>等前任退出的上限。够它跑完关窗口 + 清临时目录那一套。</summary>
    private static readonly TimeSpan PredecessorWaitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>凭条路径：<c>%LOCALAPPDATA%\JASM\unelevated-relaunch.txt</c>（与 Elevator.version 同级）。</summary>
    private static string MarkerPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JASM",
        UnelevatedRelaunchProtocol.MarkerFileName);

    /// <summary>
    /// 写下凭条。**必须在请 explorer 起新进程之前写**：写得晚了，新的一份会在凭条出现之前
    /// 就把单实例检查跑完，然后自己退出。
    /// </summary>
    internal static void Write(ILogger logger, int predecessorProcessId)
    {
        try
        {
            var directory = Path.GetDirectoryName(MarkerPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(MarkerPath, UnelevatedRelaunchProtocol.FormatMarker(predecessorProcessId));
            logger.Information("[去提权] 已写下交接凭条：前任 pid={PredecessorProcessId}", predecessorProcessId);
        }
        catch (Exception e)
        {
            // 写不下不影响「新的那份能起来」，只影响「它会不会等前任」——
            // 记 Warning 不抛：这条路不该因为一个文件写失败就整个放弃
            logger.Warning(e, "[去提权] 交接凭条写不下（{Path}）；新进程可能不等前任，按单实例检查的老行为处理",
                MarkerPath);
        }
    }

    /// <summary>
    /// 只看一眼凭条在不在，**不消费**。用来判断「本进程是不是上一份重启出来的」——
    /// 是的话就不能再做一次降级重启，否则一份接一份地重启下去。
    /// </summary>
    internal static bool IsPendingHandoff(ILogger logger)
    {
        try
        {
            if (!File.Exists(MarkerPath))
                return false;

            return UnelevatedRelaunchProtocol.IsHandoffPending(File.ReadAllText(MarkerPath),
                File.GetLastWriteTimeUtc(MarkerPath), DateTime.UtcNow, out _);
        }
        catch (Exception e)
        {
            logger.Warning(e, "[去提权] 看交接凭条失败（{Path}）", MarkerPath);
            return false;
        }
    }

    /// <summary>
    /// 消费凭条：新鲜且可解析就返回前任 pid 并**把它删掉**（一次性凭条），
    /// 否则返回 null（过期的顺手清掉，免得下次启动又被它拦一下）。
    /// </summary>
    internal static int? Consume(ILogger logger)
    {
        try
        {
            if (!File.Exists(MarkerPath))
                return null;

            var content = File.ReadAllText(MarkerPath);
            var writtenUtc = File.GetLastWriteTimeUtc(MarkerPath);

            if (!UnelevatedRelaunchProtocol.IsHandoffPending(content, writtenUtc, DateTime.UtcNow,
                    out var predecessorProcessId))
            {
                logger.Information("[去提权] 交接凭条已过期或读不懂，丢弃（写入时间 {WrittenUtc:u}）", writtenUtc);
                Delete(logger);
                return null;
            }

            Delete(logger);
            return predecessorProcessId;
        }
        catch (Exception e)
        {
            logger.Warning(e, "[去提权] 读交接凭条失败（{Path}）", MarkerPath);
            return null;
        }
    }

    private static void Delete(ILogger logger)
    {
        try
        {
            File.Delete(MarkerPath);
        }
        catch (Exception e)
        {
            // 删不掉最多是下次启动多判一次（有 60 秒时效兜着）
            logger.Warning(e, "[去提权] 交接凭条删不掉（{Path}）", MarkerPath);
        }
    }

    /// <summary>等前任进程退出。等到返回 true；超时返回 false。</summary>
    internal static async Task<bool> WaitForExitAsync(int predecessorProcessId, ILogger logger)
    {
        try
        {
            using var predecessor = Process.GetProcessById(predecessorProcessId);
            using var timeout = new CancellationTokenSource(PredecessorWaitTimeout);

            await predecessor.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (ArgumentException)
        {
            // 取不到就是已经退出了 —— 这正是我们要等的结果
            logger.Information("[去提权] 前任（pid={PredecessorProcessId}）已经退出", predecessorProcessId);
            return true;
        }
        catch (OperationCanceledException)
        {
            logger.Error("[去提权] 等前任（pid={PredecessorProcessId}）退出超时（{Timeout}）",
                predecessorProcessId, PredecessorWaitTimeout);
            return false;
        }
        catch (Exception e)
        {
            logger.Error(e, "[去提权] 等前任（pid={PredecessorProcessId}）退出时出错", predecessorProcessId);
            return false;
        }
    }
}