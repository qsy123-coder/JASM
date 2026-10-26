using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 回滚执行：预检不通过时一个字节都不动；恢复成功后逐字节一致；0 字节标记删除新增文件；
/// 清理只删计划内的 BAK。
/// </summary>
public class WuwaRollbackExecutorTests : IDisposable
{
    private readonly DirectoryInfo _root;

    public WuwaRollbackExecutorTests()
    {
        _root = Directory.CreateTempSubdirectory("jasm-wuwa-rollback-");
    }

    public void Dispose()
    {
        try
        {
            _root.Delete(true);
        }
        catch (Exception)
        {
            // 忽略清理失败
        }
    }

    [Fact]
    public void RestoresFileAndDeletesBackup()
    {
        var mod = Directory.CreateDirectory(Path.Combine(_root.FullName, "mod")).FullName;
        var original = Path.Combine(mod, "mod.ini");
        var backup = Path.Combine(mod, "mod.ini_2026-10-26 20-15-03.BAK");
        File.WriteAllText(original, "new");
        File.WriteAllText(backup, "old");

        var plan = Plan(
            new WuwaRollbackAction(original, backup, DeleteOriginal: false, "2026-10-26 20-15-03.000"),
            [backup]);

        var report = WuwaRollbackExecutor.Execute(plan, _root.FullName);

        Assert.True(report.Success);
        Assert.Equal(1, report.AppliedCount);
        Assert.Equal("old", File.ReadAllText(original));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void MarkerDeletesOriginalFile()
    {
        var mod = Directory.CreateDirectory(Path.Combine(_root.FullName, "mod")).FullName;
        var original = Path.Combine(mod, "added.dds");
        var backup = Path.Combine(mod, "added.dds_2026-10-26 20-15-03.BAK");
        File.WriteAllText(original, "content");
        File.WriteAllBytes(backup, []);

        var plan = Plan(
            new WuwaRollbackAction(original, backup, DeleteOriginal: true, "2026-10-26 20-15-03.000"),
            [backup]);

        var report = WuwaRollbackExecutor.Execute(plan, _root.FullName);

        Assert.True(report.Success);
        Assert.False(File.Exists(original));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public void MissingBackupAbortsBeforeWritingAnything()
    {
        var mod = Directory.CreateDirectory(Path.Combine(_root.FullName, "mod")).FullName;
        var original = Path.Combine(mod, "mod.ini");
        var missingBackup = Path.Combine(mod, "mod.ini_2026-10-26 20-15-03.BAK");
        File.WriteAllText(original, "current");

        var plan = Plan(
            new WuwaRollbackAction(original, missingBackup, DeleteOriginal: false, "2026-10-26 20-15-03.000"),
            [missingBackup]);

        var report = WuwaRollbackExecutor.Execute(plan, _root.FullName);

        Assert.False(report.Success);
        Assert.Equal("current", File.ReadAllText(original));
        Assert.NotEmpty(report.Failures);
    }

    [Fact]
    public void CreatesMissingDestinationDirectory()
    {
        var mod = Directory.CreateDirectory(Path.Combine(_root.FullName, "mod")).FullName;
        var backup = Path.Combine(mod, "textures", "tex.dds_2026-10-26 20-15-03.BAK");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "texture");

        var original = Path.Combine(mod, "textures", "deeper", "tex.dds");
        var plan = Plan(
            new WuwaRollbackAction(original, backup, DeleteOriginal: false, "2026-10-26 20-15-03.000"),
            [backup]);

        var report = WuwaRollbackExecutor.Execute(plan, _root.FullName);

        Assert.True(report.Success);
        Assert.Equal("texture", File.ReadAllText(original));
    }

    [Fact]
    public void RejectsActionOutsideRoot()
    {
        var outside = Directory.CreateTempSubdirectory("jasm-wuwa-outside-");
        try
        {
            var backup = Path.Combine(outside.FullName, "x.ini_2026-10-26 20-15-03.BAK");
            File.WriteAllText(backup, "x");

            var plan = Plan(
                new WuwaRollbackAction(Path.Combine(_root.FullName, "x.ini"), backup, false,
                    "2026-10-26 20-15-03.000"),
                [backup]);

            var report = WuwaRollbackExecutor.Execute(plan, _root.FullName);

            Assert.False(report.Success);
            Assert.True(File.Exists(backup));
        }
        finally
        {
            outside.Delete(true);
        }
    }

    [Fact]
    public void CleanupFailureDoesNotUndoSuccessfulRestore()
    {
        var mod = Directory.CreateDirectory(Path.Combine(_root.FullName, "mod")).FullName;
        var original = Path.Combine(mod, "mod.ini");
        var backup = Path.Combine(mod, "mod.ini_2026-10-26 20-15-03.BAK");
        File.WriteAllText(original, "new");
        File.WriteAllText(backup, "old");

        var plan = Plan(
            new WuwaRollbackAction(original, backup, false, "2026-10-26 20-15-03.000"),
            [backup, Path.Combine(_root.FullName, "missing.BAK")]);

        var report = WuwaRollbackExecutor.Execute(plan, _root.FullName);

        Assert.True(report.Success);
        Assert.Equal("old", File.ReadAllText(original));
        Assert.Empty(report.CleanupFailures);
    }

    private static WuwaRollbackPlan Plan(
        WuwaRollbackAction action,
        IReadOnlyList<string> backupsToDelete) =>
        new(true, null, [action], backupsToDelete);
}