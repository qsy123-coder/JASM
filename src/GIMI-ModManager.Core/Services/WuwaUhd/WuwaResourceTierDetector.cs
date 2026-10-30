namespace GIMI_ModManager.Core.Services.WuwaUhd;

public enum WuwaTierDetectionKind
{
    /// <summary>只找到一个已安装档位。</summary>
    Detected,

    /// <summary>找到了 Content 目录，但没有已安装的资源档位。</summary>
    NotInstalled,

    /// <summary>多个档位都已安装，需要用户按官方启动器的当前选择指定。</summary>
    Ambiguous,

    /// <summary>找不到 <c>Client\Content</c>，无法判断。</summary>
    NotFound
}

/// <summary>
/// 档位检测结果。<see cref="Installed"/> 按 SD/HD/UHD 顺序列出检测到的档位。
/// </summary>
public sealed record WuwaTierDetectionResult(
    WuwaTierDetectionKind Kind,
    WuwaResourceTier? Tier,
    IReadOnlyList<WuwaResourceTier> Installed);

/// <summary>
/// 从游戏安装形态推断当前资源档位：鸣潮 3.7+ 把不同档位放在
/// <c>Client\Content\{SD,HD,UHD}</c> 下（社区工具/文档统一这么称呼）。
///
/// 检测只判断“这个目录下有没有实际文件”，不解析 launcher 的私有配置 —— 后者格式随启动器版本变，
/// 而且启动器运行时还会把内存里的旧值写回去。多个档位同时存在时返回 <see cref="WuwaTierDetectionKind.Ambiguous"/>，
/// 由界面要求用户按官方启动器当前选择指定（Rocket 也是这个策略，选错会让 UE4 崩）。
/// </summary>
public static class WuwaResourceTierDetector
{
    private static readonly string[] TierDirectoryNames = ["SD", "HD", "UHD"];

    public static WuwaTierDetectionResult DetectFromGameExecutable(string? gameExecutablePath)
    {
        var contentDirectory = ResolveContentDirectory(gameExecutablePath);
        if (contentDirectory is null)
            return new WuwaTierDetectionResult(WuwaTierDetectionKind.NotFound, null, []);

        var installed = new List<WuwaResourceTier>();

        foreach (var tier in new[] { WuwaResourceTier.Sd, WuwaResourceTier.Hd, WuwaResourceTier.Uhd })
        {
            var directory = Path.Combine(contentDirectory, TierDirectoryNames[(int)tier - 1]);
            if (HasAnyFile(directory))
                installed.Add(tier);
        }

        return installed.Count switch
        {
            1 => new WuwaTierDetectionResult(WuwaTierDetectionKind.Detected, installed[0], installed),
            0 => new WuwaTierDetectionResult(WuwaTierDetectionKind.NotInstalled, null, installed),
            _ => new WuwaTierDetectionResult(WuwaTierDetectionKind.Ambiguous, null, installed)
        };
    }

    private static string? ResolveContentDirectory(string? gameExecutablePath)
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
        for (var depth = 0; depth < 8 && !string.IsNullOrEmpty(directory); depth++)
        {
            var underClient = Path.Combine(directory, "Client", "Content");
            if (Directory.Exists(underClient))
                return underClient;

            if (string.Equals(Path.GetFileName(directory), "Client", StringComparison.OrdinalIgnoreCase))
            {
                var content = Path.Combine(directory, "Content");
                if (Directory.Exists(content))
                    return content;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    /// <summary>目录下是否存在至少一个文件（含子目录）。权限不足/IO 异常按“没有”处理。</summary>
    private static bool HasAnyFile(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                   && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
