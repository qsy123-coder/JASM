namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 一次回滚的逐项结果。失败时 <see cref="FailureMessage"/> 是给人看的原因；
/// <see cref="CleanupFailures"/> 与恢复结果分开 —— 文件已经恢复、只是备份没删掉时，这仍算成功。
/// </summary>
public sealed record WuwaRollbackReport(
    bool Success,
    int AppliedCount,
    string? FailureMessage,
    IReadOnlyList<string> Failures,
    int DeletedBackupCount,
    IReadOnlyList<string> CleanupFailures);

/// <summary>
/// 执行回滚计划。
///
/// 顺序是刻意的：**先预检全部源文件，再写盘**。回滚到一半发现某个备份缺失时，
/// 已经恢复的文件会处于「一半旧一半新」的状态，比完全没动更糟；所以只读检查必须全部通过。
/// 执行全部成功后才清理参与本次回滚的 <c>.BAK</c>；失败则一个都不删，用户还能重试。
/// </summary>
public static class WuwaRollbackExecutor
{
    public static WuwaRollbackReport Execute(
        WuwaRollbackPlan plan,
        string allowedRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedRoot);

        if (!plan.CanExecute)
            return Failed(plan.Error ?? "回滚计划不可执行");

        // ── 预检：任何一项不满足都不写盘 ────────────────────────────────
        var preflightFailures = new List<string>();

        foreach (var action in plan.Actions)
        {
            if (!WuwaPathGuard.IsSafeTarget(action.OriginalPath, allowedRoot))
                preflightFailures.Add($"目标越界：{Path.GetFileName(action.OriginalPath)}");

            if (action.DeleteOriginal)
                continue;

            if (!WuwaPathGuard.IsSafeTarget(action.SourceBackupPath, allowedRoot))
                preflightFailures.Add($"备份越界：{Path.GetFileName(action.SourceBackupPath)}");
            else if (!File.Exists(action.SourceBackupPath))
                preflightFailures.Add($"备份缺失：{Path.GetFileName(action.SourceBackupPath)}");
        }

        foreach (var backupPath in plan.BackupsToDelete)
        {
            if (!WuwaPathGuard.IsSafeTarget(backupPath, allowedRoot))
                preflightFailures.Add($"待清理备份越界：{Path.GetFileName(backupPath)}");
        }

        if (preflightFailures.Count > 0)
            return Failed("预检失败，未修改任何文件", preflightFailures);

        // ── 执行：逐项恢复/删除，任一项失败即中止 ──────────────────────
        var applied = 0;
        foreach (var action in plan.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (action.DeleteOriginal)
                {
                    if (Directory.Exists(action.OriginalPath))
                    {
                        return Failed(
                            $"目标是目录，拒绝删除：{Path.GetFileName(action.OriginalPath)}",
                            appliedCount: applied);
                    }

                    if (File.Exists(action.OriginalPath))
                        File.Delete(action.OriginalPath);
                }
                else
                {
                    RestoreFile(action.SourceBackupPath, action.OriginalPath);
                }

                applied++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                          or NotSupportedException)
            {
                return Failed($"恢复失败：{Path.GetFileName(action.OriginalPath)}（{e.Message}）",
                    appliedCount: applied);
            }
        }

        // ── 清理：只删计划内的 .BAK；删不掉不影响已恢复的结果 ──────────
        var deleted = 0;
        var cleanupFailures = new List<string>();
        foreach (var backupPath in plan.BackupsToDelete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                    deleted++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                cleanupFailures.Add($"{Path.GetFileName(backupPath)}（{e.Message}）");
            }
        }

        return new WuwaRollbackReport(true, applied, null, Array.Empty<string>(), deleted, cleanupFailures);
    }

    /// <summary>
    /// 先写临时文件再覆盖。直接 <c>File.Copy(..., overwrite:true)</c> 在目标被占用/中途失败时
    /// 会留下半个文件；临时文件 + <c>File.Move(overwrite)</c> 至少保证「要么旧内容、要么完整新内容」。
    /// </summary>
    private static void RestoreFile(string sourcePath, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new IOException("目标路径没有父目录");

        Directory.CreateDirectory(directory);

        var tempPath = destinationPath + ".rollback.tmp." + Environment.ProcessId;
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static WuwaRollbackReport Failed(
        string message,
        IReadOnlyList<string>? failures = null,
        int appliedCount = 0) =>
        new(false, appliedCount, message, failures ?? Array.Empty<string>(), 0, Array.Empty<string>());

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 临时文件残留不影响结果；下一次同目录回滚会用带 PID 的新名字。
        }
    }
}