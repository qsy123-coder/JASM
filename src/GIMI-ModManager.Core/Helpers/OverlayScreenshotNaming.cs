using System.Text;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 浮窗截图的名字规则（纯逻辑、可单测）。
///
/// 名字来自浮窗列表上的显示名，而显示名是用户随手写的：可能带 <c>\ / : * ? " &lt; &gt; |</c>、
/// 可能超长、也可能正好是 Windows 的保留设备名（<c>CON</c> / <c>NUL</c> 之类）。
/// 这些情况都必须在**落盘之前**处理掉 —— <c>File.Create</c> 抛异常的时机太晚，
/// 用户看到的是「截图失败」，而真正的原因只是名字里有个问号。
/// </summary>
public static class OverlayScreenshotNaming
{
    /// <summary>文件名（不含扩展名）的长度上限。给输出目录与扩展名留足余量。</summary>
    public const int MaxBaseNameLength = 80;

    /// <summary>清洗之后一个字符都不剩时用的名字。</summary>
    public const string FallbackName = "未命名";

    /// <summary>
    /// Windows 保留的设备名。大小写不敏感，且**带扩展名也不行**（<c>CON.png</c> 一样打不开），
    /// 所以判的是去掉扩展名后的主干。
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// 把显示名清洗成可用的文件名主干（不含扩展名）。
    ///
    /// 规则：
    /// <list type="number">
    /// <item>非法字符换成 <c>_</c>（<see cref="Path.GetInvalidFileNameChars"/> 之外的 U+0000–U+001F 也一并换掉）；</item>
    /// <item>去首尾空白与点 —— Windows 会静默吃掉结尾的点，留着只会让实际文件名与界面显示的对不上；</item>
    /// <item>截断到 <see cref="MaxBaseNameLength"/> 个字符；</item>
    /// <item>洗成空串就退回 <see cref="FallbackName"/>；</item>
    /// <item>命中保留设备名时在前面补一个 <c>_</c>。</item>
    /// </list>
    /// </summary>
    public static string SanitizeBaseName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return FallbackName;

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(displayName.Length);

        foreach (var ch in displayName)
        {
            builder.Append(invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch);
        }

        var sanitized = builder.ToString().Trim().TrimEnd('.').Trim();

        if (sanitized.Length > MaxBaseNameLength)
            sanitized = sanitized[..MaxBaseNameLength].Trim().TrimEnd('.').Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
            return FallbackName;

        return ReservedNames.Contains(sanitized) ? "_" + sanitized : sanitized;
    }

    /// <summary>
    /// 在 <paramref name="folder"/> 里挑一个不冲突的完整路径。
    ///
    /// 第一个候选是 <c>{baseName}{extension}</c>；已存在就依次试 <c>{baseName}-2</c>、<c>-3</c>……
    /// **不覆盖已有文件**：同一个 Mod 反复截图时，上一张往往正是要对比的那张。
    ///
    /// <paramref name="fileExists"/> 由调用方注入（真实现是 <c>File.Exists</c> / 虚拟文件系统），
    /// 这样这段挑名逻辑不依赖磁盘、可单测。
    /// </summary>
    /// <param name="folder">输出目录（不需要已存在；本方法只拼路径）。</param>
    /// <param name="baseName">已清洗的文件名主干。</param>
    /// <param name="extension">带点的扩展名，如 <c>.png</c>。</param>
    /// <param name="fileExists">判断某个完整路径是否已存在的函数。</param>
    public static string ResolveUniqueFilePath(string folder, string baseName, string extension,
        Func<string, bool> fileExists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentNullException.ThrowIfNull(extension);
        ArgumentNullException.ThrowIfNull(fileExists);

        var candidate = Path.Combine(folder, baseName + extension);

        for (var suffix = 2; fileExists(candidate); suffix++)
        {
            candidate = Path.Combine(folder, $"{baseName}-{suffix}{extension}");
        }

        return candidate;
    }
}
