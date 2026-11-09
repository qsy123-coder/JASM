using System.Text.RegularExpressions;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace GIMI_ModManager.Core.Services.OilGloss;

/// <summary>
/// 按官方说明扫描 Mod 里的 <c>drawindexed</c>：
/// <list type="number">
///   <item>只处理 <c>[TextureOverride*]</c> 段落里的 drawcall（命令列表里的透明层绘制包了也无效）；</item>
///   <item>忽略数量为 0 的 drawindexed（WWMI 模型里常见，包上会变成空的资源切换）；</item>
///   <item>只包 <c>[TextureOverrideComponent3]</c> 及以后的段落 —— 官方说明身体/衣物的 drawcall
///         通常在这里，而 Component0–2 是身体绒毛 / 头发 / 脸；</item>
///   <item>Mod 完全没有 ComponentN 段落时（作者用自定义段落名），包所有
///         <c>[TextureOverride*]</c> 里的 drawcall，但跳过命中头部关键词的段落；</item>
///   <item>只有 Component0–2 的 Mod 直接跳过，不再回退成「包全部」——
///         回退会让纯发型 Mod 的头发也上油光。</item>
/// </list>
///
/// 不做身体/衣物分组：同一个 drawcall 经常同时包含身体和衣物，拆成两个开关只会做出
/// 「关了衣物身体还亮」的假开关。整个 Mod 只有一个总开关 + 强度。
/// </summary>
public static class OilGlossScanner
{
    private static readonly string[] SkippedFileNames =
        ["JASM_Menu.ini", OilGlossProtocol.OilIniFileName];

    private static readonly string[] SkippedFilePrefixes =
        ["disabled_", "_depth", "light"];

    private static readonly Regex SectionRegex = new(@"^\s*\[(.+?)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex DrawIndexedRegex =
        new(@"^\s*drawindexed\s*=\s*(\d+)\s*,\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ComponentIndexRegex =
        new(@"TextureOverrideComponent(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 头部关键词（与 split_oil.py 一致）。只用于**排除**，不做身体识别。
    /// 用词边界避免 "that" / "thread" 之类的误判。
    /// </summary>
    private static readonly Regex HeadKeywordRegex = new(
        @"(?<![A-Za-z0-9])(hair|head|face|eye|brow|hat)(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>扫描整个 Mod 目录。单个文件读失败不会拖垮整次扫描。</summary>
    public static OilGlossScanResult Scan(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
            return new OilGlossScanResult { Reason = "Mod 目录不存在" };

        var drawcalls = new List<OilGlossDrawcall>();

        foreach (var file in Directory
                     .EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(modFolderPath, file);
            if (ShouldSkip(relativePath))
                continue;

            try
            {
                drawcalls.AddRange(ScanFile(file, relativePath));
            }
            catch (IOException)
            {
                // 单个 ini 读不了不影响其它文件
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (drawcalls.Count == 0)
            return new OilGlossScanResult { Reason = "没有找到可包油光的 drawindexed" };

        // 有 ComponentN 段落时，只认 Component3+：0–2 是头，非组件段落（自定义名）
        // 也不在官方身体/衣物约定里，一并排除。完全没有组件段落时才走「包全部」。
        var hasComponentSections = drawcalls.Any(d => GetComponentIndex(d) >= 0);
        var annotated = drawcalls
            .Select(d => d with
            {
                IsHeadLike = d.IsHeadLike
                             || (hasComponentSections && GetComponentIndex(d) < 3),
            })
            .ToList();

        return Group(annotated);
    }

    /// <summary>扫描单个 ini，返回其中的 drawcall（行号基于该文件）。</summary>
    public static IReadOnlyList<OilGlossDrawcall> ScanFile(string filePath, string relativePath)
        => ScanText(IniFileText.ReadAllText(filePath), relativePath);

    /// <summary>扫描一段 ini 文本，返回其中的 drawcall（行号基于这段文本）。</summary>
    public static IReadOnlyList<OilGlossDrawcall> ScanText(string text, string relativePath)
    {
        if (!text.Contains("[TextureOverride", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("drawindexed", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var result = new List<OilGlossDrawcall>();
        var section = string.Empty;
        var sectionHasHeadKeyword = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var sectionMatch = SectionRegex.Match(lines[i]);
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups[1].Value.Trim();
                sectionHasHeadKeyword = HeadKeywordRegex.IsMatch(section);
                continue;
            }

            if (!section.StartsWith("TextureOverride", StringComparison.OrdinalIgnoreCase))
                continue;

            // 条件 / 注释 / 贴图绑定里出现 hair/head/face… 时，整段都按头部处理。
            // 宁可少包，也不要把头发包进油光。
            if (!sectionHasHeadKeyword && HeadKeywordRegex.IsMatch(lines[i]))
                sectionHasHeadKeyword = true;

            var drawMatch = DrawIndexedRegex.Match(lines[i]);
            if (!drawMatch.Success)
                continue;

            var indexCount = int.Parse(drawMatch.Groups[1].Value);
            if (indexCount <= 0)
                continue;

            result.Add(new OilGlossDrawcall
            {
                IniRelativePath = relativePath,
                SectionName = section,
                LineIndex = i,
                IndexCount = indexCount,
                StartIndex = int.Parse(drawMatch.Groups[2].Value),
                IsHeadLike = sectionHasHeadKeyword || IsHeadComponent(section),
            });
        }

        return result;
    }

    /// <summary>选中所有非头部网格，归入唯一的油光组。</summary>
    public static OilGlossScanResult Group(IReadOnlyList<OilGlossDrawcall> drawcalls)
    {
        if (drawcalls.Count == 0)
            return new OilGlossScanResult { Reason = "没有找到可包油光的 drawindexed" };

        var candidates = drawcalls.Where(d => !d.IsHeadLike).ToList();
        if (candidates.Count == 0)
        {
            return new OilGlossScanResult
            {
                Drawcalls = drawcalls,
                Reason = "只找到脸/头发/头部网格（Component0–2 或命中头部关键词），已全部排除",
            };
        }

        var annotated = drawcalls
            .Select(d => candidates.Any(c => IsSame(c, d)) ? d with { Group = OilGlossGroup.Body } : d)
            .ToList();

        var selected = annotated
            .Where(d => d.Group is not null)
            .OrderByDescending(d => d.IndexCount)
            .ToList();

        return new OilGlossScanResult
        {
            Drawcalls = annotated,
            Body = selected.FirstOrDefault(),
            Cloth = null,
        };
    }

    private static int GetComponentIndex(OilGlossDrawcall draw) => GetComponentIndex(draw.SectionName);

    private static int GetComponentIndex(string sectionName)
    {
        var match = ComponentIndexRegex.Match(sectionName);
        return match.Success && int.TryParse(match.Groups[1].Value, out var index) ? index : -1;
    }

    /// <summary>Component0–2 = 身体绒毛 / 头发 / 脸，默认不包油光。</summary>
    private static bool IsHeadComponent(string sectionName) => GetComponentIndex(sectionName) is >= 0 and <= 2;

    private static bool IsSame(OilGlossDrawcall? left, OilGlossDrawcall? right)
        => left is not null
           && right is not null
           && left.IniRelativePath == right.IniRelativePath
           && left.LineIndex == right.LineIndex;

    private static bool ShouldSkip(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);

        if (SkippedFileNames.Any(name => string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (fileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".jasm.tmp", StringComparison.OrdinalIgnoreCase))
            return true;

        var lowerName = fileName.ToLowerInvariant();
        if (SkippedFilePrefixes.Any(prefix => lowerName.StartsWith(prefix, StringComparison.Ordinal)))
            return true;

        var lowerPath = relativePath.Replace('\\', '/').ToLowerInvariant();
        if (lowerPath.Contains("/disabled_", StringComparison.Ordinal)
            || lowerPath.StartsWith("disabled_", StringComparison.Ordinal))
            return true;

        // 与 split_oil.py 一致：路径里带 face/head 的模型文件通常不是皮肤/衣物
        return lowerPath.Contains("face", StringComparison.Ordinal)
               || lowerPath.Contains("head", StringComparison.Ordinal);
    }
}
