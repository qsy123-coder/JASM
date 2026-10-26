using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GIMI_ModManager.Core.Services.WuwaModFixer;
using GIMI_ModManager.WinUI.Services.Overlay;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>
/// 一次「修复当前角色」的长任务。做成单例而不是塞进浮窗 VM：
/// 浮窗可能被隐藏、角色列表会被刷新，但正在跑的任务与进度必须活着且不被重建。
///
/// 状态属性都在 UI 线程更新（<see cref="StartAsync"/> 由界面调用，内部 await 未使用
/// <c>ConfigureAwait(false)</c>；文件枚举与进程执行在后台完成）。
/// </summary>
internal sealed partial class WuwaFixSession : ObservableObject, IDisposable
{
    private const int MaxLogLines = 300;

    private readonly WuwaModFixerEngine _engine;
    private readonly ILogger _logger;
    private readonly Queue<string> _logLines = new();
    private CancellationTokenSource? _cts;

    public WuwaFixSession(WuwaModFixerEngine engine, ILogger logger)
    {
        _engine = engine;
        _logger = logger.ForContext<WuwaFixSession>();
    }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _total;
    [ObservableProperty] private int _completed;
    [ObservableProperty] private int _successCount;
    [ObservableProperty] private int _skippedCount;
    [ObservableProperty] private int _failedCount;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string? _currentModName;
    [ObservableProperty] private string _statusText = "准备就绪";
    [ObservableProperty] private string _logText = string.Empty;
    [ObservableProperty] private bool _isLogExpanded;

    /// <summary>任务结束（成功、失败、取消）时触发；调用方负责决定是否发 F10。</summary>
    public event EventHandler<WuwaFixRunSummary>? RunCompleted;

    /// <summary>
    /// 串行修复。快照目标列表在调用方完成，运行中角色切换不会影响本次任务。
    /// </summary>
    public async Task StartAsync(
        IReadOnlyList<WuwaFixTarget> targets,
        WuwaFixOptions options,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning || targets.Count == 0)
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;

        ResetForRun(targets.Count);
        AppendLog($"[{characterName}] 开始修复 {targets.Count} 个 Mod");

        var failures = new List<WuwaFixFailure>();
        var wasCancelled = false;

        try
        {
            foreach (var target in targets)
            {
                token.ThrowIfCancellationRequested();

                CurrentModName = target.Name;
                StatusText = $"正在修复：{target.Name}";

                var before = await SnapshotBackupsAsync(target.ModPath, token);
                WuwaModCliRunResult result;

                try
                {
                    result = await RunWithRetryAsync(target, options, token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger.Warning(e, "[修复引擎] {Mod} 执行异常", target.Name);
                    MarkFailed(failures, target.Name, e.Message);
                    Advance();
                    continue;
                }

                var after = await SnapshotBackupsAsync(target.ModPath, token);
                var backupChanged = !before.SetEquals(after);

                var classification = WuwaFixOutcomeClassifier.Classify(
                    result.ExitCode, result.FinishEvent, backupChanged, result.TimedOut, result.Cancelled);

                ApplyOutcome(classification, target.Name, result, failures);
                AppendLog($"{target.Name}：{classification.Message}");
                Advance();
            }
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            AppendLog("已终止：已处理的 Mod 保留结果与备份，未处理的不计入成功");
        }
        finally
        {
            IsRunning = false;
            CurrentModName = null;
            _cts.Dispose();
            _cts = null;

            var summary = new WuwaFixRunSummary(
                Total, SuccessCount, SkippedCount, FailedCount, wasCancelled, failures);

            StatusText = summary.ToSummaryText();
            AppendLog(summary.ToSummaryText());
            _logger.Information("[修复引擎] 修复结束：成功 {Success} / 跳过 {Skipped} / 失败 {Failed} / 取消 {Cancelled}",
                summary.Success, summary.Skipped, summary.Failed, summary.WasCancelled);

            RunCompleted?.Invoke(this, summary);
        }
    }

    /// <summary>用户点「终止」。当前进程会在 runner 里被杀掉（含子进程树）。</summary>
    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务刚好结束
        }
    }

    public void Dispose() => Cancel();

    private async Task<WuwaModCliRunResult> RunWithRetryAsync(
        WuwaFixTarget target,
        WuwaFixOptions options,
        CancellationToken token)
    {
        var progress = new Progress<WuwaModCliEvent>(OnCliEvent);

        try
        {
            return await _engine.RunModAsync(target, options, progress, token);
        }
        catch (Exception e) when (e is FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            // CLI 被杀软删除/损坏：重放一次再试。只重试一次，避免反复拉起无意义进程。
            _logger.Warning(e, "[修复引擎] CLI 启动失败，尝试重放");
            AppendLog("修复引擎异常，正在重新释放并重试…");
            await _engine.EnsureReadyAsync(forceProvision: true, token);
            return await _engine.RunModAsync(target, options, progress, token);
        }
    }

    private void OnCliEvent(WuwaModCliEvent evt)
    {
        switch (evt.Kind)
        {
            case WuwaModCliEventKind.Log when !string.IsNullOrWhiteSpace(evt.Message):
                AppendLog(evt.Message);
                break;
            case WuwaModCliEventKind.Unknown when !string.IsNullOrWhiteSpace(evt.Message):
                AppendLog(evt.Message);
                break;
            case WuwaModCliEventKind.Finish when !string.IsNullOrWhiteSpace(evt.Message):
                AppendLog(evt.Message);
                break;
        }
    }

    private void ApplyOutcome(
        WuwaFixClassification classification,
        string modName,
        WuwaModCliRunResult result,
        List<WuwaFixFailure> failures)
    {
        switch (classification.Outcome)
        {
            case WuwaFixOutcome.Success:
                SuccessCount++;
                break;

            case WuwaFixOutcome.Skipped:
                SkippedCount++;
                break;

            case WuwaFixOutcome.Cancelled:
                // 取消由外层统一收尾；这里不把当前 Mod 计成功
                break;

            default:
                FailedCount++;
                var reason = classification.Message;
                if (!string.IsNullOrWhiteSpace(result.StandardErrorTail))
                    reason += "；" + LastLine(result.StandardErrorTail);
                failures.Add(new WuwaFixFailure(modName, reason));
                break;
        }
    }

    private void MarkFailed(List<WuwaFixFailure> failures, string modName, string reason)
    {
        FailedCount++;
        failures.Add(new WuwaFixFailure(modName, reason));
    }

    private void Advance()
    {
        Completed++;
        Percent = Total == 0 ? 0 : Math.Round(Completed * 100.0 / Total, 1);
    }

    private void ResetForRun(int total)
    {
        IsRunning = true;
        Total = total;
        Completed = 0;
        SuccessCount = 0;
        SkippedCount = 0;
        FailedCount = 0;
        Percent = 0;
        CurrentModName = null;
        StatusText = "正在准备…";
        _logLines.Clear();
        LogText = string.Empty;
    }

    private async Task<HashSet<string>> SnapshotBackupsAsync(string modPath, CancellationToken token)
    {
        return await Task.Run(() =>
        {
            var result = WuwaBackupScanner.Scan([modPath], modPath, token);
            return result.Groups
                .SelectMany(group => group.Files)
                .Select(file => $"{file.BackupPath}|{file.SizeBytes}|{file.TimestampKey}")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }, token);
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var line = $"{DateTime.Now:HH:mm:ss} {message.Trim()}";
        _logLines.Enqueue(line);

        while (_logLines.Count > MaxLogLines)
            _logLines.Dequeue();

        LogText = string.Join(Environment.NewLine, _logLines);
    }

    private static string LastLine(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? text.Trim() : lines[^1].Trim();
    }
}