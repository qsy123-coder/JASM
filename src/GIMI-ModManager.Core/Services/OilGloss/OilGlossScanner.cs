using System.Text.RegularExpressions;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace GIMI_ModManager.Core.Services.OilGloss;

/// <summary>
/// 扫描 Mod 里的 <c>drawindexed</c>，并按「全自动猜测」分成身体 / 衣物两组。
///
/// 规则移植自 <c>oil_sc</c> 的 <c>split_oil.py</c>，但收紧了两处：
/// <list type="bullet">
///   <item>只处理 <c>[TextureOverride*]</c> 段落里的 drawcall —— 命令列表里的透明层绘制包了也无效；</item>
///   <item>忽略数量为 0 的 drawindexed（WWMI 模型里常见，包上会变成空的资源切换）。</item>
/// </list>
///
/// 分组是纯启发式：最大的一块当身体，排除头部后最大的一块当衣物。
/// 一个 drawcall 同时含身体和衣物时分不开，只能整块归到一组——这是自动分组的已知边界。
/// </summary>
public static class OilGlossScanner
{
    private static readonly string[] HeadKeywords =
        ["hat", "hair", "face", "eye", "brow", "head"];

    private static readonly string[] SkippedFileNames =
        ["JASM_Menu.ini", OilGlossProtocol.OilIniFileName];

    private static readonly string[] SkippedFilePrefixes =
        ["disabled_", "_depth", "light"];

    private static readonly Regex SectionRegex = new(@"^\s*\[(.+?)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex DrawIndexedRegex =
        new(@"^\s*drawindexed\s*=\s*(\d+)\s*,\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ConditionRegex = new(@"^\s*if\s+(\$[\w\\]+)", RegexOptions.Compiled);
    private static readonly Regex TextureSlotRegex =
        new(@"ResourceTexture(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DrawCommentRegex = new(@"^\s*;\s*Draw\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>扫描整个 Mod 目录。单个文件读失败不会拖垮整次扫描。</summary>
    public static OilGlossScanResult Scan(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
        {
            return new OilGlossScanResult { Reason = "Mod 目录不存在" };
        }

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

        return Group(drawcalls);
    }

    /// <summary>扫描单个 ini，返回其中的 drawcall（行号基于该文件）。</summary>
    public static IReadOnlyList<OilGlossDrawcall> ScanFile(string filePath, string relativePath)
    {
        var text = IniFileText.ReadAllText(filePath);
        if (!text.Contains("[TextureOverride", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("drawindexed", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var lines = text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var result = new List<OilGlossDrawcall>();
        var section = string.Empty;

        for (var i = 0; i < lines.Length; i++)
        {
            var sectionMatch = SectionRegex.Match(lines[i]);
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups[1].Value.Trim();
                continue;
            }

            if (!section.StartsWith("TextureOverride", StringComparison.OrdinalIgnoreCase))
                continue;

            var drawMatch = DrawIndexedRegex.Match(lines[i]);
            if (!drawMatch.Success)
                continue;

            var indexCount = int.Parse(drawMatch.Groups[1].Value);
            if (indexCount <= 0)
                continue;

            var startIndex = int.Parse(drawMatch.Groups[2].Value);
            var condition = string.Empty;
            var textureSlot = string.Empty;

            // 往回最多 30 行找同一段落里的条件与贴图槽；遇到上一个段落头就停
            for (var j = i - 1; j >= 0 && j >= i - 30; j--)
            {
                if (SectionRegex.IsMatch(lines[j]))
                    break;

                if (condition.Length == 0)
                {
                    var conditionMatch = ConditionRegex.Match(lines[j]);
                    if (conditionMatch.Success)
                        condition = conditionMatch.Groups[1].Value.Trim();
                }

                if (textureSlot.Length == 0)
                {
                    var textureMatch = TextureSlotRegex.Match(lines[j]);
                    if (textureMatch.Success)
                        textureSlot = textureMatch.Groups[1].Value;
                }
            }

            var comment = string.Empty;
            for (var j = i - 1; j >= 0 && j >= i - 6; j--)
            {
                var commentMatch = DrawCommentRegex.Match(lines[j]);
                if (!commentMatch.Success)
                    continue;

                comment = lines[j].Trim().TrimStart(';').Trim();
                break;
            }

            result.Add(new OilGlossDrawcall
            {
                IniRelativePath = relativePath,
                SectionName = section,
                LineIndex = i,
                IndexCount = indexCount,
                StartIndex = startIndex,
                Condition = condition,
                TextureSlot = textureSlot,
                Comment = comment,
                IsHeadLike = IsHeadLike(condition, textureSlot, comment, section),
            });
        }

        return result;
    }

    /// <summary>按大小自动分组：最大 = 身体，排除头部后次大 = 衣物。</summary>
    public static OilGlossScanResult Group(IReadOnlyList<OilGlossDrawcall> drawcalls)
    {
        if (drawcalls.Count == 0)
        {
            return new OilGlossScanResult { Reason = "没有找到可包油光的 drawindexed" };
        }

        var candidates = drawcalls.Where(d => !d.IsHeadLike).ToList();
        if (candidates.Count == 0)
        {
            return new OilGlossScanResult
            {
                Drawcalls = drawcalls,
                Reason = "只找到头部/头发/脸的 drawcall，已全部排除",
            };
        }

        var body = candidates.OrderByDescending(d => d.IndexCount).First();
        var cloth = candidates
            .Where(d => d.IniRelativePath != body.IniRelativePath
                        || d.LineIndex != body.LineIndex)
            .OrderByDescending(d => d.IndexCount)
            .FirstOrDefault();

        var annotated = drawcalls
            .Select(d => IsSame(d, body)
                ? d with { Group = OilGlossGroup.Body }
                : IsSame(d, cloth)
                    ? d with { Group = OilGlossGroup.Cloth }
                    : d)
            .ToList();

        var bodyResult = annotated.First(d => d.Group == OilGlossGroup.Body);
        var clothResult = annotated.FirstOrDefault(d => d.Group == OilGlossGroup.Cloth);

        return new OilGlossScanResult
        {
            Drawcalls = annotated,
            Body = bodyResult,
            Cloth = clothResult,
        };
    }

    private static bool IsSame(OilGlossDrawcall? left, OilGlossDrawcall? right)
        => left is not null
           && right is not null
           && left.IniRelativePath == right.IniRelativePath
           && left.LineIndex == right.LineIndex;

    private static bool IsHeadLike(string condition, string textureSlot, string comment, string section)
    {
        var haystack = (condition + " " + textureSlot + " " + comment + " " + section).ToLowerInvariant();
        return HeadKeywords.Any(haystack.Contains);
    }

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