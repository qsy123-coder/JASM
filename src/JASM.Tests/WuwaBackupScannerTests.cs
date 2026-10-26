using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 备份命名解析与分组。上游的命名规则是
/// <c>&lt;原文件名&gt;_&lt;yyyy-MM-dd HH-mm-ss[.fff]&gt;.BAK</c>，原文件名本身可能带下划线和中文。
/// </summary>
public class WuwaBackupScannerTests : IDisposable
{
    private readonly DirectoryInfo _root;

    public WuwaBackupScannerTests()
    {
        _root = Directory.CreateTempSubdirectory("jasm-wuwa-backup-");
    }

    public void Dispose()
    {
        try
        {
            _root.Delete(true);
        }
        catch (Exception)
        {
            // 临时目录删不掉不该让测试失败
        }
    }

    [Theory]
    [InlineData("mod.ini_2026-10-26 20-15-03.BAK", "mod.ini", "2026-10-26 20-15-03.000")]
    [InlineData("mod.ini_2026-10-26 20-15-03.123.BAK", "mod.ini", "2026-10-26 20-15-03.123")]
    [InlineData("my_mod.ini_2026-01-02 03-04-05.BAK", "my_mod.ini", "2026-01-02 03-04-05.000")]
    [InlineData("皮肤 01.dds_2026-10-26 20-15-03.BAK", "皮肤 01.dds", "2026-10-26 20-15-03.000")]
    [InlineData("mod.ini_2026-10-26 20-15-03.bak", "mod.ini", "2026-10-26 20-15-03.000")]
    public void ParsesBackupNames(string fileName, string expectedOriginal, string expectedKey)
    {
        Assert.True(WuwaBackupScanner.TryParseName(fileName, out var name));
        Assert.Equal(expectedOriginal, name.OriginalFileName);
        Assert.Equal(expectedKey, name.TimestampKey);
    }

    [Theory]
    [InlineData("mod.ini")]
    [InlineData("mod.ini.BAK")]
    [InlineData("mod.ini_2026-13-99 20-15-03.BAK")]
    [InlineData("_2026-10-26 20-15-03.BAK")]
    public void RejectsNonBackupOrInvalidTimestamp(string fileName)
    {
        Assert.False(WuwaBackupScanner.TryParseName(fileName, out _));
    }

    [Fact]
    public void GroupsByTimestampAndSortsDescending()
    {
        var modA = Directory.CreateDirectory(Path.Combine(_root.FullName, "modA")).FullName;
        var modB = Directory.CreateDirectory(Path.Combine(_root.FullName, "modB")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(modB, "textures")).FullName;

        File.WriteAllText(Path.Combine(modA, "mod.ini_2026-10-26 20-15-03.BAK"), "old-a");
        File.WriteAllText(Path.Combine(modB, "mod.ini_2026-10-26 20-15-04.BAK"), "old-b");
        File.WriteAllText(Path.Combine(nested, "tex.dds_2026-10-26 20-15-04.BAK"), "old-tex");
        File.WriteAllText(Path.Combine(modA, "newer.ini_2026-10-27 09-00-00.500.BAK"), "new");

        var result = WuwaBackupScanner.Scan([modA, modB], _root.FullName);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.Groups.Count);
        Assert.Equal("2026-10-27 09-00-00.500", result.Groups[0].TimestampKey);
        Assert.Equal("2026-10-26 20-15-04.000", result.Groups[1].TimestampKey);
        Assert.Equal(1, result.Groups[1].ModCount);
        Assert.Equal(2, result.Groups[1].Files.Count);
        Assert.Equal("2026-10-26 20-15-03.000", result.Groups[2].TimestampKey);
    }

    [Fact]
    public void MarkerUsesOriginalFileSizeForDisplay()
    {
        var mod = Directory.CreateDirectory(Path.Combine(_root.FullName, "mod")).FullName;
        var original = Path.Combine(mod, "added.dds");
        File.WriteAllText(original, "1234567890");
        File.WriteAllBytes(Path.Combine(mod, "added.dds_2026-10-26 20-15-03.BAK"), []);

        var result = WuwaBackupScanner.Scan([mod], _root.FullName);

        var file = Assert.Single(Assert.Single(result.Groups).Files);
        Assert.True(file.IsMarker);
        Assert.Equal(10, file.SizeBytes);
    }

    [Fact]
    public void IgnoresNonBackupFiles()
    {
        var mod = Directory.CreateDirectory(Path.Combine(_root.FullName, "mod")).FullName;
        File.WriteAllText(Path.Combine(mod, "mod.ini"), "current");
        File.WriteAllText(Path.Combine(mod, "readme.txt"), "hi");

        var result = WuwaBackupScanner.Scan([mod], _root.FullName);

        Assert.Empty(result.Groups);
    }

    [Fact]
    public void SkipsRootsOutsideAllowedRoot()
    {
        var outside = Directory.CreateTempSubdirectory("jasm-wuwa-outside-");
        try
        {
            File.WriteAllText(Path.Combine(outside.FullName, "mod.ini_2026-10-26 20-15-03.BAK"), "x");

            var result = WuwaBackupScanner.Scan([outside.FullName], _root.FullName);

            Assert.Empty(result.Groups);
            Assert.NotEmpty(result.Warnings);
        }
        finally
        {
            outside.Delete(true);
        }
    }
}