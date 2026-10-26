namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>回滚时对单个原文件要做的事。</summary>
public sealed record WuwaRollbackAction(
    string OriginalPath,
    string SourceBackupPath,
    bool DeleteOriginal,
    string TimestampKey);

/// <summary>
/// 回滚计划：动作列表 + 本次要清理的备份。
/// <see cref="CanExecute"/> 为 false 时 <see cref="Error"/> 说明原因，执行器不得动盘。
/// </summary>
public sealed record WuwaRollbackPlan(
    bool CanExecute,
    string? Error,
    IReadOnlyList<WuwaRollbackAction> Actions,
    IReadOnlyList<string> BackupsToDelete)
{
    public static WuwaRollbackPlan Fail(string error) =>
        new(false, error, Array.Empty<WuwaRollbackAction>(), Array.Empty<string>());
}

/// <summary>
/// 按上游 <c>rollback.rs</c> 的语义生成回滚计划。
///
/// 目标组 = 用户选中的还原点；把所有「目标组及更新」的组一起处理：
/// 对每个原文件取这些组里**最早**的一份备份 —— 那份才是「目标组这次修复之前」的内容。
/// 0 字节备份是「该文件是修复新增出来的」标记，回滚时删除原文件。
///
/// 计划与执行分开：本类只读数据，执行器负责预检与写盘，两者都能单测。
/// </summary>
public static class WuwaRollbackPlanner
{
    public static WuwaRollbackPlan Create(
        IReadOnlyList<WuwaBackupGroup> groups,
        string targetTimestampKey)
    {
        ArgumentNullException.ThrowIfNull(groups);

        if (!WuwaBackupScanner.TryNormalizeTimestampKey(targetTimestampKey, out var normalizedKey, out var targetTime))
            return WuwaRollbackPlan.Fail("还原点时间无效");

        var target = groups.FirstOrDefault(group =>
            string.Equals(group.TimestampKey, normalizedKey, StringComparison.Ordinal));

        // 也接受调用方直接传组里已经归一化过的键；上面的 TryNormalize 已覆盖这种形态，
        // 再按时间比较一次是为了容忍设置文件里存的是另一种分隔符写法。
        target ??= groups.FirstOrDefault(group => group.Timestamp == targetTime);

        if (target is null)
            return WuwaRollbackPlan.Fail("找不到该还原点");

        var selectedGroups = groups
            .Where(group => group.Timestamp >= target.Timestamp)
            .ToList();

        if (selectedGroups.Count == 0)
            return WuwaRollbackPlan.Fail("没有可回滚的备份");

        var earliestPerFile = new Dictionary<string, WuwaBackupFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in selectedGroups.SelectMany(group => group.Files))
        {
            if (!earliestPerFile.TryGetValue(file.OriginalPath, out var current)
                || file.Timestamp < current.Timestamp)
            {
                earliestPerFile[file.OriginalPath] = file;
            }
        }

        var actions = earliestPerFile.Values
            .OrderBy(file => file.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .Select(file => new WuwaRollbackAction(
                file.OriginalPath,
                file.BackupPath,
                file.IsMarker,
                file.TimestampKey))
            .ToList();

        var backupsToDelete = selectedGroups
            .SelectMany(group => group.Files)
            .Select(file => file.BackupPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (actions.Count == 0)
            return WuwaRollbackPlan.Fail("没有可回滚的文件");

        return new WuwaRollbackPlan(true, null, actions, backupsToDelete);
    }
}