using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 回滚计划必须与上游 <c>rollback.rs</c> 同语义：目标组及之后的所有组里，
/// 每个文件取**最早**的一份备份；0 字节表示「删除该文件」；参与本次回滚的 BAK 全部列入清理。
/// </summary>
public class WuwaRollbackPlannerTests
{
    private const string Root = @"C:\Mods\feixue";

    private static WuwaBackupGroup Group(string key, params WuwaBackupFile[] files)
    {
        Assert.True(WuwaBackupScanner.TryNormalizeTimestampKey(key, out var normalized, out var timestamp));
        return new WuwaBackupGroup(normalized, timestamp, files, files.Select(f => f.ModIndex).Distinct().Count(),
            files.Sum(f => f.SizeBytes));
    }

    private static WuwaBackupFile Bak(string originalName, string key, bool marker = false) =>
        new(Path.Combine(Root, "mod", originalName + "_" + key + ".BAK"),
            Path.Combine(Root, "mod", originalName),
            Normalize(key),
            Parse(key),
            marker ? 0 : 10,
            marker,
            0);

    private static string Normalize(string key)
    {
        Assert.True(WuwaBackupScanner.TryNormalizeTimestampKey(key, out var normalized, out _));
        return normalized;
    }

    private static DateTime Parse(string key)
    {
        Assert.True(WuwaBackupScanner.TryNormalizeTimestampKey(key, out _, out var timestamp));
        return timestamp;
    }

    [Fact]
    public void UsesEarliestBackupAtOrAfterTarget()
    {
        var t1 = "2026-10-26 20-15-03";
        var t2 = "2026-10-26 21-15-03";
        var t3 = "2026-10-26 22-15-03";

        var groups = new[]
        {
            Group(t1, Bak("mod.ini", t1)),
            Group(t2, Bak("mod.ini", t2)),
            Group(t3, Bak("mod.ini", t3), Bak("textures.dds", t3))
        };

        var plan = WuwaRollbackPlanner.Create(groups, t2);

        Assert.True(plan.CanExecute);
        Assert.Equal(2, plan.Actions.Count);

        var modIni = plan.Actions.Single(a => a.OriginalPath.EndsWith("mod.ini"));
        Assert.Equal(Parse(t2), Parse(Path.GetFileName(modIni.SourceBackupPath).Split('_')[^1].Replace(".BAK", "")));
        Assert.False(modIni.DeleteOriginal);

        Assert.Contains(plan.Actions, a => a.OriginalPath.EndsWith("textures.dds"));
        Assert.Equal(3, plan.BackupsToDelete.Count);
    }

    [Fact]
    public void MarkerProducesDeleteAction()
    {
        var key = "2026-10-26 20-15-03";
        var plan = WuwaRollbackPlanner.Create([Group(key, Bak("added.dds", key, marker: true))], key);

        Assert.True(plan.CanExecute);
        Assert.True(Assert.Single(plan.Actions).DeleteOriginal);
    }

    [Fact]
    public void TargetEarliestIncludesAllGroups()
    {
        var t1 = "2026-10-26 20-15-03";
        var t2 = "2026-10-26 21-15-03";

        var plan = WuwaRollbackPlanner.Create(
            [Group(t1, Bak("a.ini", t1)), Group(t2, Bak("b.ini", t2))], t1);

        Assert.Equal(2, plan.Actions.Count);
        Assert.Equal(2, plan.BackupsToDelete.Count);
    }

    [Fact]
    public void TargetNotFoundFails()
    {
        var plan = WuwaRollbackPlanner.Create([Group("2026-10-26 20-15-03", Bak("a.ini", "2026-10-26 20-15-03"))],
            "2026-10-26 23-59-59");

        Assert.False(plan.CanExecute);
        Assert.NotNull(plan.Error);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void InvalidTargetKeyFails()
    {
        var plan = WuwaRollbackPlanner.Create([], "not-a-date");

        Assert.False(plan.CanExecute);
        Assert.Equal("还原点时间无效", plan.Error);
    }

    [Fact]
    public void TargetsNormalizedWithoutMillisecondsMatch()
    {
        var key = "2026-10-26 20-15-03.123";
        var groups = new[] { Group(key, Bak("a.ini", key)) };

        var plan = WuwaRollbackPlanner.Create(groups, "2026-10-26 20-15-03.123");

        Assert.True(plan.CanExecute);
    }
}