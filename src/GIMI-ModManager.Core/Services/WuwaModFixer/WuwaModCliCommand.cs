namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 拼 <c>wuwa-mod-cli</c> 的启动参数。
///
/// 只产出参数列表，不拼命令行字符串：调用方必须用 <c>ProcessStartInfo.ArgumentList</c> 逐项传入，
/// 否则 Mod 路径里的空格、中文、引号会被二次解析，表现为「CLI 说找不到目录」。
/// </summary>
public static class WuwaModCliCommand
{
    /// <summary>
    /// <c>--path &lt;mod&gt; --format json --config &lt;cfg&gt;</c> + 开关参数。
    /// 顺序固定，方便日志与测试断言。
    /// </summary>
    public static IReadOnlyList<string> Build(string modDirectory, string configPath, WuwaFixOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string>(12)
        {
            "--path", modDirectory,
            "--format", "json",
            "--config", configPath
        };

        arguments.AddRange(options.ToArguments());
        return arguments;
    }
}