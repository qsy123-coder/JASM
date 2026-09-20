using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="D3dxIniTargetParser"/> — the only zero-config way JASM learns which game process to
/// send keys to (<c>[Loader] target =</c> inside 3dmigoto's <c>d3dx.ini</c>).
///
/// The returned value must NOT carry the <c>.exe</c> suffix: <c>Process.GetProcessesByName("Game.exe")</c>
/// matches nothing.
/// </summary>
public class D3dxIniTargetParserTests
{
    [Fact]
    public void ParsesTheRealLoaderTarget()
    {
        // 本机 D:\XXMI\WWMI\d3dx.ini 的真实形态
        var processName = D3dxIniTargetParser.ParseTargetProcessName("""
            [Loader]
            target = Client-Win64-Shipping.exe

            [Include]
            include = mods\mod.ini
            """);

        Assert.Equal("Client-Win64-Shipping", processName);
    }

    [Fact]
    public void PrefersTheLoaderSectionOverAnyOtherTarget()
    {
        var processName = D3dxIniTargetParser.ParseTargetProcessName("""
            [SomeOtherSection]
            target = Decoy.exe

            [Loader]
            target = Client-Win64-Shipping.exe
            """);

        Assert.Equal("Client-Win64-Shipping", processName);
    }

    [Fact]
    public void FallsBackToAnyTargetWhenThereIsNoLoaderSection()
    {
        var processName = D3dxIniTargetParser.ParseTargetProcessName("target = GenshinImpact.exe");

        Assert.Equal("GenshinImpact", processName);
    }

    [Theory]
    [InlineData("target = \"Game.exe\"")]
    [InlineData("target = 'Game.exe'")]
    [InlineData("target = D:\\Games\\WuWa\\Client\\Binaries\\Win64\\Client-Win64-Shipping.exe")]
    [InlineData("  target   =   Client-Win64-Shipping.exe   ")]
    [InlineData("target = Game.exe ; 注释")]
    [InlineData("target = Game.exe # 注释")]
    [InlineData("[loader]\r\ntarget = Game.exe")]
    public void ToleratesMessyTargetLines(string line)
    {
        var expected = line.Contains("Client-Win64-Shipping", StringComparison.OrdinalIgnoreCase)
            ? "Client-Win64-Shipping"
            : "Game";

        Assert.Equal(expected, D3dxIniTargetParser.ParseTargetProcessName(line));
    }

    [Fact]
    public void MatchesTheKeyCaseInsensitivelyButKeepsTheCasingFromTheFile()
    {
        // 不做大小写归一：进程名大小写由文件决定，Process.GetProcessesByName 本身不区分大小写
        Assert.Equal("GAME", D3dxIniTargetParser.ParseTargetProcessName("TARGET = GAME.EXE"));
    }

    [Theory]
    [InlineData("; target = Game.exe")]     // 整行被注释掉
    [InlineData("# target = Game.exe")]
    [InlineData("targets = Game.exe")]      // 前缀像但不是这个键（IsIniKey 的 StartsWith 会把这条吃下去）
    [InlineData("target =")]                // 空值
    [InlineData("target = Game")]           // 不带 .exe 的值不可信
    [InlineData("target = Game.dll")]
    [InlineData("[Loader]\r\ncondition = $x")]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNullWhenThereIsNoUsableTarget(string iniContent)
        => Assert.Null(D3dxIniTargetParser.ParseTargetProcessName(iniContent));

    [Fact]
    public void ReturnsNullForNullInput()
        => Assert.Null(D3dxIniTargetParser.ParseTargetProcessName(null));

    [Fact]
    public void UsesTheTargetFromAnotherSectionWhenLoaderHasNone()
    {
        // [Loader] 在但没写 target：不直接放弃，退化为文件里第一个可用的 target
        var processName = D3dxIniTargetParser.ParseTargetProcessName("""
            [Loader]
            loader = 1

            [Other]
            target = Game.exe
            """);

        Assert.Equal("Game", processName);
    }

    [Fact]
    public void ReadsCrlfFiles()
        => Assert.Equal("Client-Win64-Shipping",
            D3dxIniTargetParser.ParseTargetProcessName("[Loader]\r\ntarget = Client-Win64-Shipping.exe\r\n"));
}