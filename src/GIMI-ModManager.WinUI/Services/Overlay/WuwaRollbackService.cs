using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>清理备份的结果。删不掉的文件单独列出，不影响已删除的部分。</summary>
public sealed record WuwaCleanupReport(int Deleted, IReadOnlyList<string> Failures);

/// <summary>
/// 备份扫描 / 回滚 / 清理的后台包装。Core 里是纯逻辑，这里只负责切到线程池，
/// 避免在 UI 线程上递归扫几十个 Mod 目录。
/// </summary>
internal sealed class WuwaRollbackService
{
    public Task<WuwaBackupScanResult> ScanAsync(
        IReadOnlyList<string> modRoots,
        string allowedRoot,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => WuwaBackupScanner.Scan(modRoots, allowedRoot, cancellationToken), cancellationToken);

    public Task<WuwaRollbackReport> RollbackToAsync(
        IReadOnlyList<WuwaBackupGroup> groups,
        string targetTimestampKey,
        string allowedRoot,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var plan = WuwaRollbackPlanner.Create(groups, targetTimestampKey);
            return WuwaRollbackExecutor.Execute(plan, allowedRoot, cancellationToken);
        }, cancellationToken);

    public Task<WuwaRollbackReport> RestoreEarliestAsync(
        IReadOnlyList<WuwaBackupGroup> groups,
        string allowedRoot,
        CancellationToken cancellationToken = default)
    {
        var earliest = groups.OrderBy(group => group.Timestamp).FirstOrDefault();
        if (earliest is null)
        {
            return Task.FromResult(new WuwaRollbackReport(
                false, 0, "没有可回滚的备份", Array.Empty<string>(), 0, Array.Empty<string>()));
        }

        return RollbackToAsync(groups, earliest.TimestampKey, allowedRoot, cancellationToken);
    }

    /// <summary>只删 <c>.BAK</c>，不碰 Mod 文件。每个路径都再过一次根校验。</summary>
    public Task<WuwaCleanupReport> CleanAllAsync(
        IReadOnlyList<WuwaBackupGroup> groups,
        string allowedRoot,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var deleted = 0;
            var failures = new List<string>();

            foreach (var backup in groups.SelectMany(group => group.Files))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!WuwaPathGuard.IsSafeTarget(backup.BackupPath, allowedRoot))
                {
                    failures.Add($"{Path.GetFileName(backup.BackupPath)}（路径越界）");
                    continue;
                }

                try
                {
                    if (File.Exists(backup.BackupPath))
                    {
                        File.Delete(backup.BackupPath);
                        deleted++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    failures.Add($"{Path.GetFileName(backup.BackupPath)}（{e.Message}）");
                }
            }

            return new WuwaCleanupReport(deleted, failures);
        }, cancellationToken);
}