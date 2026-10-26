using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>单次 CLI 调用的原始结果。<see cref="FinishEvent"/> 可能为 null（输出被截断/旧版 CLI）。</summary>
public sealed record WuwaModCliRunResult(
    int ExitCode,
    WuwaModCliEvent? FinishEvent,
    string StandardErrorTail,
    bool TimedOut,
    bool Cancelled);

/// <summary>
/// 执行一次 <c>wuwa-mod-cli --path … --format json</c>。
///
/// 三个容易踩的点：
/// 1. 参数必须走 <c>ArgumentList</c>，不能拼字符串（Mod 路径常带空格与中文）；
/// 2. stdout 逐行读 NDJSON，stderr 单独收集（上游把日志写在 stderr 里）；
/// 3. 取消/超时必须 <c>Kill(entireProcessTree: true)</c>，否则 CLI 派生的子进程会继续占着 Mod 文件。
/// </summary>
internal sealed class WuwaModCliRunner
{
    /// <summary>单个 Mod 的最长执行时间。上游修复通常几秒，90 秒足够且能防卡死。</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private const int StandardErrorTailLines = 30;

    public async Task<WuwaModCliRunResult> RunAsync(
        string cliPath,
        string configPath,
        string modDirectory,
        WuwaFixOptions options,
        IProgress<WuwaModCliEvent>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(cliPath))
            throw new FileNotFoundException("修复引擎不存在", cliPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = cliPath,
            WorkingDirectory = modDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        foreach (var argument in WuwaModCliCommand.Build(modDirectory, configPath, options))
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);

        using var registration = timeoutCts.Token.Register(() => TryKill(process));

        process.Start();
        process.StandardInput.Close();

        WuwaModCliEvent? finishEvent = null;
        var standardError = new ConcurrentQueue<string>();

        var stdoutTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (!WuwaModCliEvent.TryParse(line, out var parsed) || parsed is null)
                    {
                        // 上游可能在 JSON 之前打印纯文本；当作一条日志，不丢信息。
                        if (!string.IsNullOrWhiteSpace(line))
                            progress?.Report(new WuwaModCliEvent(
                                WuwaModCliEventKind.Log, Message: line.Trim(), RawLine: line));
                        continue;
                    }

                    if (parsed.Kind == WuwaModCliEventKind.Finish)
                        finishEvent = parsed;

                    progress?.Report(parsed);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // 进程被杀/管道关闭：结果由退出码与取消状态判断
            }
        });

        var stderrTask = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    standardError.Enqueue(line);
                    while (standardError.Count > StandardErrorTailLines)
                        standardError.TryDequeue(out _);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                // 同上
            }
        });

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await WaitForExitWithTimeoutAsync(process);
        }

        await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(TimeSpan.FromSeconds(5)));

        var exited = process.HasExited;
        var exitCode = -1;
        if (exited)
        {
            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                exitCode = -1;
            }
        }

        return new WuwaModCliRunResult(
            exitCode,
            finishEvent,
            string.Join(Environment.NewLine, standardError),
            TimedOut: timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested,
            Cancelled: cancellationToken.IsCancellationRequested);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // 已经退出或没有权限终止：交给后续的等待逻辑
        }
    }

    private static async Task WaitForExitWithTimeoutAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            // 杀不掉时不再阻塞界面；结果标记为超时/取消
        }
    }
}