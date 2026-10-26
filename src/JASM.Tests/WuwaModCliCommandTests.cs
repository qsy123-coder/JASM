using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// CLI 参数必须是逐项列表，路径不能自己拼引号 —— 这是中文/空格/引号路径最常见的坏法。
/// </summary>
public class WuwaModCliCommandTests
{
    [Fact]
    public void BuildsBaseArgumentsInFixedOrder()
    {
        var arguments = WuwaModCliCommand.Build(@"D:\Mods\feixue", @"C:\JASM\config.json", new WuwaFixOptions());

        Assert.Equal(
            ["--path", @"D:\Mods\feixue", "--format", "json", "--config", @"C:\JASM\config.json"],
            arguments);
    }

    [Fact]
    public void KeepsPathsWithSpacesAndChineseAsSingleArguments()
    {
        const string modPath = @"D:\鸣潮 Mod\角色 目录\绯雪";
        const string configPath = @"C:\Users\qsy123\AppData\Local\JASM\WuwaModFixer\config.json";

        var arguments = WuwaModCliCommand.Build(modPath, configPath, new WuwaFixOptions());

        Assert.Contains(modPath, arguments);
        Assert.Contains(configPath, arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith('"'));
    }

    [Fact]
    public void AppendsSwitchArgumentsAfterConfig()
    {
        var arguments = WuwaModCliCommand.Build(@"D:\mods\a", @"D:\config.json",
            new WuwaFixOptions(Rendering33: true, AeroFixMode: 1));

        Assert.Equal("--rendering-33", arguments[^3]);
        Assert.Equal("--aero-fix", arguments[^2]);
        Assert.Equal("1", arguments[^1]);
    }

    [Theory]
    [InlineData("", @"D:\config.json")]
    [InlineData("   ", @"D:\config.json")]
    [InlineData(@"D:\mods\a", "")]
    public void RejectsEmptyPaths(string modDirectory, string configPath)
    {
        Assert.Throws<ArgumentException>(() =>
            WuwaModCliCommand.Build(modDirectory, configPath, new WuwaFixOptions()));
    }
}