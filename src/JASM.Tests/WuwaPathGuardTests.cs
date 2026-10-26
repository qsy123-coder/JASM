using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 路径护栏：回滚的目标路径是由备份文件名推导出来的，必须挡住 <c>..</c> 与兄弟目录前缀。
/// </summary>
public class WuwaPathGuardTests : IDisposable
{
    private readonly DirectoryInfo _root;

    public WuwaPathGuardTests()
    {
        _root = Directory.CreateTempSubdirectory("jasm-wuwa-path-");
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
    public void AcceptsPathInsideRoot()
    {
        var target = Path.Combine(_root.FullName, "mod", "mod.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "x");

        Assert.True(WuwaPathGuard.IsWithinRoot(target, _root.FullName));
        Assert.False(WuwaPathGuard.ContainsReparsePoint(target, _root.FullName));
        Assert.True(WuwaPathGuard.IsSafeTarget(target, _root.FullName));
    }

    [Fact]
    public void AcceptsRootItself()
    {
        Assert.True(WuwaPathGuard.IsWithinRoot(_root.FullName, _root.FullName));
    }

    [Fact]
    public void RejectsSiblingDirectoryWithSamePrefix()
    {
        var sibling = _root.FullName + "2";
        Directory.CreateDirectory(sibling);
        try
        {
            Assert.False(WuwaPathGuard.IsWithinRoot(sibling, _root.FullName));
        }
        finally
        {
            Directory.Delete(sibling);
        }
    }

    [Fact]
    public void RejectsParentTraversal()
    {
        var target = Path.Combine(_root.FullName, "..", "escape.ini");
        Assert.False(WuwaPathGuard.IsWithinRoot(target, _root.FullName));
    }

    [Fact]
    public void IsCaseInsensitiveOnWindows()
    {
        var target = _root.FullName.ToUpperInvariant() + Path.DirectorySeparatorChar + "MOD" +
                     Path.DirectorySeparatorChar + "mod.ini";
        Directory.CreateDirectory(Path.Combine(_root.FullName, "mod"));

        Assert.True(WuwaPathGuard.IsWithinRoot(target, _root.FullName.ToLowerInvariant()));
    }

    [Fact]
    public void UnsafeTargetForcesReparseCheckToFail()
    {
        Assert.True(WuwaPathGuard.ContainsReparsePoint(Path.Combine(_root.FullName, "..", "x.ini"),
            _root.FullName));
    }
}