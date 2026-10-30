namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>鸣潮的两份画质/渲染配置。两者都可能不存在，调用方负责创建。</summary>
public sealed record WuwaGameConfigPaths(string EngineIniPath, string GameUserSettingsIniPath);

/// <summary>
/// 从游戏 exe 反推 <c>Client\Saved\Config\WindowsNoEditor</c>。
///
/// 游戏目录形态不止一种：
/// <list type="bullet">
/// <item><c>...\Wuthering Waves Game\Client\Binaries\Win64\Wuthering Waves.exe</c>（官方安装）</item>
/// <item><c>...\Client\Binaries\Win64\Client-Win64-Shipping.exe</c>（部分渠道/测试包）</item>
/// <item>用户手动选到 <c>Client.exe</c>（启动器）</item>
/// </list>
/// 所以这里从 exe 目录逐级向上找，遇到名为 <c>Client</c> 的目录，或当前目录下存在
/// <c>Client\Saved\Config\WindowsNoEditor</c> 就停。找不到返回 <c>null</c>，由调用方提示用户。
/// </summary>
public static class WuwaGameConfigPathsResolver
{
    private const string ConfigFolderName = "WindowsNoEditor";

    /// <summary>从游戏 exe 的完整路径解析配置文件路径；解析不出返回 <c>null</c>。</summary>
    public static WuwaGameConfigPaths? ResolveFromGameExecutable(string? gameExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(gameExecutablePath))
            return null;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(gameExecutablePath);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
            return null;

        // 逐级向上，最多 8 层（Win64 → Binaries → Client → 游戏根 → …）
        for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(directory); depth++)
        {
            // 形态 A：当前目录就是 Client
            if (string.Equals(Path.GetFileName(directory), "Client", StringComparison.OrdinalIgnoreCase))
            {
                var saved = Path.Combine(directory, "Saved", "Config", ConfigFolderName);
                if (Directory.Exists(saved))
                    return Build(saved);
            }

            // 形态 B：当前目录是游戏根，下面有 Client
            var clientUnderCurrent = Path.Combine(directory, "Client", "Saved", "Config", ConfigFolderName);
            if (Directory.Exists(clientUnderCurrent))
                return Build(clientUnderCurrent);

            // 形态 C：当前目录直接就是 Saved 的上一级（少数打包会把 Config 放在根下）
            var directSaved = Path.Combine(directory, "Saved", "Config", ConfigFolderName);
            if (Directory.Exists(directSaved))
                return Build(directSaved);

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    private static WuwaGameConfigPaths Build(string configDirectory) => new(
        Path.Combine(configDirectory, "Engine.ini"),
        Path.Combine(configDirectory, "GameUserSettings.ini"));
}
