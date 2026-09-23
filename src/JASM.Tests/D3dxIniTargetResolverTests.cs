using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="D3dxIniTargetResolver"/> — where <c>d3dx.ini</c> lives, and which game process it names.
///
/// Both the key-sending path and the elevated refresh path depend on the same candidate rules, so they are
/// a shared contract: in particular an empty root must NOT produce a candidate, otherwise
/// <c>Path.Combine("", "d3dx.ini")</c> is a relative path that resolves against whatever the process CWD
/// happens to be.
///
/// No test touches the real machine installation: candidates are compared as strings, and file reads use a
/// throwaway temp directory.
/// </summary>
public class D3dxIniTargetResolverTests
{
    [Fact]
    public void PrefersTheGimiRootThenTheModsParent()
    {
        var candidates = D3dxIniTargetResolver.GetD3dxIniCandidates(@"D:\XXMI\WWMI", @"D:\Games\Mods");

        Assert.Equal(new[] { @"D:\XXMI\WWMI\d3dx.ini", @"D:\Games\d3dx.ini" }, candidates);
    }

    [Fact]
    public void ToleratesTrailingSeparatorsOnTheModsFolder()
    {
        var candidates = D3dxIniTargetResolver.GetD3dxIniCandidates(null, @"D:\Games\Mods\");

        Assert.Equal(new[] { @"D:\Games\d3dx.ini" }, candidates);
    }

    [Fact]
    public void BothCandidatesMayPointAtTheSameFile()
    {
        // 正常安装就是这样（d3dx.ini 与 Mods 同级）—— 刻意不去重：真去重了，「哪个候选不存在」的
        // 调试日志会跟着变，而那条日志是排查安装问题的唯一线索
        var candidates = D3dxIniTargetResolver.GetD3dxIniCandidates(@"D:\XXMI\WWMI", @"D:\XXMI\WWMI\Mods");

        Assert.Equal(new[] { @"D:\XXMI\WWMI\d3dx.ini", @"D:\XXMI\WWMI\d3dx.ini" }, candidates);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData(null, "Mods")] // 相对路径：父目录是空串，不能拿去 Combine
    public void ProducesNoCandidateForEmptyPaths(string? gimiRootFolderPath, string? modsFolderPath)
        => Assert.Empty(D3dxIniTargetResolver.GetD3dxIniCandidates(gimiRootFolderPath, modsFolderPath));

    [Fact]
    public void ResolvesTheFirstExistingCandidate()
    {
        using var temp = new TempDirectory();
        var iniPath = temp.WriteIni("[Loader]\r\ntarget = Client-Win64-Shipping.exe\r\n");

        // 第一个候选（GIMI 根）不存在，第二个（Mods 的父目录）存在 —— 应该由后者命中
        var resolved = D3dxIniTargetResolver.ResolveD3dxIniPath(
            System.IO.Path.Combine(temp.Folder, "missing"), System.IO.Path.Combine(temp.Folder, "Mods"));

        Assert.Equal(iniPath, resolved);
    }

    [Fact]
    public void ResolvesNothingWhenNoCandidateExists()
    {
        using var temp = new TempDirectory();

        Assert.Null(D3dxIniTargetResolver.ResolveD3dxIniPath(
            temp.Folder, System.IO.Path.Combine(temp.Folder, "Mods")));
    }

    [Fact]
    public void ReadsTheTargetProcessNameWithoutTheExeSuffix()
    {
        using var temp = new TempDirectory();
        var iniPath = temp.WriteIni("[Loader]\r\ntarget = Client-Win64-Shipping.exe\r\n");

        Assert.Equal("Client-Win64-Shipping", D3dxIniTargetResolver.ReadTargetProcessName(iniPath));
    }

    [Theory]
    [InlineData("")]                            // 空文件
    [InlineData("[Loader]\r\ncondition = $x")]  // 有段、没 target
    public void ReturnsNullWhenTheIniHasNoUsableTarget(string content)
    {
        using var temp = new TempDirectory();

        Assert.Null(D3dxIniTargetResolver.ReadTargetProcessName(temp.WriteIni(content)));
    }

    [Fact]
    public void ReturnsNullWhenTheIniCannotBeRead()
    {
        using var temp = new TempDirectory();

        // 读不了不是异常：游戏运行中 d3dx.ini 可能被独占打开，调用方按「这次没解析出目标」处理
        Assert.Null(D3dxIniTargetResolver.ReadTargetProcessName(
            System.IO.Path.Combine(temp.Folder, "does-not-exist.ini")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Folder = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "jasm-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
        }

        public string Folder { get; }

        public string WriteIni(string content)
        {
            var iniPath = System.IO.Path.Combine(Folder, D3dxIniTargetResolver.D3dxIniFileName);
            File.WriteAllText(iniPath, content);
            return iniPath;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Folder, true);
            }
            catch (IOException)
            {
                // 清不掉就留给系统临时目录，测试不该因此失败
            }
        }
    }
}