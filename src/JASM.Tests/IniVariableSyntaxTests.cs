using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace JASM.Tests;

/// <summary>
/// 3DMigoto 表达式里变量 token 的合法字符判断。
/// 连字符不在允许集合里 —— 实机日志（d3d11_log.txt）里
/// <c>$\Mods\Xin-HunSha\object_detected</c> 会被判成
/// <c>Variable not recognized</c> / <c>Unrecognised entry</c>。
/// </summary>
public class IniVariableSyntaxTests
{
    [Theory]
    [InlineData("FoxMain")]
    [InlineData(@"Mods\Xin.HunSha")]
    [InlineData("JasmF066BEC3")]
    [InlineData("cx_Mod042")]
    public void AcceptsNamesThreeDmigotoCanParse(string name)
        => Assert.True(IniVariableSyntax.IsExpressibleName(name), name);

    [Theory]
    [InlineData(@"Mods\Xin-HunSha")]
    [InlineData("Mods\\Xin HunSha")]
    [InlineData("命名空间")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsNamesThatBreakExpressions(string? name)
        => Assert.False(IniVariableSyntax.IsExpressibleName(name), name);

    [Theory]
    [InlineData(@"$\Fox\xiezi", true)]
    [InlineData(@"$\Mods\Xin-HunSha\qunzi", false)]
    [InlineData("$object_detected", true)]
    [InlineData(@"$\has space", false)]
    [InlineData("", false)]
    public void ChecksWholeReference(string reference, bool expected)
        => Assert.Equal(expected, IniVariableSyntax.IsExpressibleReference(reference));
}