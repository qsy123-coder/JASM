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
/// 分组是纯启发式：最大的一块（以及同材质槽的网格）当身体，其余可见网格当衣物。
/// 一个 drawcall 同时含身体和衣物时分不开，只能整块归到一组——这是自动分组的已知边界。
/// 分错时用详情页的「逐块识别身体」手动指定。
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

    /// <summary>
    /// 扫描整个 Mod 目录。单个文件读失败不会拖垮整次扫描。
    /// <paramref name="ignoreHairFolderGuard"/> 为 true 时跳过「发型 Mod 整包排除」的判定，
    /// 供详情页的「调整油光分组」手动挑选身体网格使用。
    /// </summary>
    public static OilGlossScanResult Scan(string modFolderPath, bool ignoreHairFolderGuard = false)
    {
        if (!Directory.Exists(modFolderPath))
        {
            return new OilGlossScanResult { Reason = "Mod 目录不存在" };
        }

        // 纯发型 Mod（名字里就写着 发型/头发/hair）整包跳过：
        // 它的所有网格都是头发，索引位置规则分不出来，上了油光就是整头变色。
        if (!ignoreHairFolderGuard && IsHairOnlyModFolder(modFolderPath))
        {
            return new OilGlossScanResult { Reason = "这是一个发型 Mod，默认不包油光" };
        }

        var drawcalls = new List<OilGlossDrawcall>();

        // 所有 ini 都读一遍：drawcall 只在部分文件里，但 $swapvar_toggle /
        // $draw_component 的默认值与映射关系可能写在别的文件。
        var texts = new List<string>();

        foreach (var file in Directory
                     .EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(modFolderPath, file);
            if (ShouldSkip(relativePath))
                continue;

            try
            {
                var text = IniFileText.ReadAllText(file);
                texts.Add(text);
                drawcalls.AddRange(ScanText(text, relativePath));
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

        var defaults = OilGlossConditionEvaluator.Evaluate(texts);
        var annotated = drawcalls
            .Select(d => d with { VisibleByDefault = OilGlossConditionEvaluator.IsTrue(d.Condition, defaults) })
            .ToList();

        // 头部/头发只按关键词排除（hat/hair/face/eye/brow/head）。
        // 不再按索引区间猜：WWMI 的组件顺序不固定，按位置猜会把「最大的身体块」
        // 当成头发排掉，出现「身体没油光、头发反而变色」。头部没名字的包用
        // 详情页「调整分组」手动指定。

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

    /// <summary>
    /// 按大小 + 材质槽自动分组：
    /// <list type="bullet">
    ///   <item>身体组 = 与最大块**同材质槽**的全部可见网格（只给最大一块上油会变成「只有大腿亮」）；</item>
    ///   <item>衣物组 = 其余全部可见网格（袜子、裙子、上衣、鞋、配饰都算衣物组）。</item>
    /// </list>
    /// 可见性无法求值时按可见处理；全部都被判定为隐藏时才退回全体候选。
    /// </summary>
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

        var visible = candidates.Where(d => d.VisibleByDefault != false).ToList();
        var pool = visible.Count > 0 ? visible : candidates;

        var largest = pool.OrderByDescending(d => d.IndexCount).First();

        // 身体：同一材质槽的整组网格。材质槽读不出来时退化回「只有最大块」。
        var bodySet = !string.IsNullOrEmpty(largest.TextureSlot)
            ? pool.Where(d => string.Equals(d.TextureSlot, largest.TextureSlot, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<OilGlossDrawcall> { largest };

        if (bodySet.Count == 0)
            bodySet = new List<OilGlossDrawcall> { largest };

        // 衣物：其余全部可见网格；只有一块时衣物组为空
        var clothSet = pool.Where(d => !bodySet.Any(b => IsSame(b, d))).ToList();

        var annotated = drawcalls
            .Select(d => bodySet.Any(b => IsSame(b, d))
                ? d with { Group = OilGlossGroup.Body }
                : clothSet.Any(c => IsSame(c, d))
                    ? d with { Group = OilGlossGroup.Cloth }
                    : d)
            .ToList();

        var bodyResult = annotated
            .Where(d => d.Group == OilGlossGroup.Body)
            .OrderByDescending(d => d.IndexCount)
            .First();

        var clothResult = annotated
            .Where(d => d.Group == OilGlossGroup.Cloth)
            .OrderByDescending(d => d.IndexCount)
            .FirstOrDefault();

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

    /// <summary>Mod 文件夹名直接表明是发型 Mod（如「尤诺-C酱发型」）时整包跳过。</summary>
    private static bool IsHairOnlyModFolder(string modFolderPath)
    {
        var name = Path.GetFileName(modFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (name.StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase))
            name = name["DISABLED_".Length..];

        var lower = name.ToLowerInvariant();
        return lower.Contains("发型", StringComparison.Ordinal)
               || lower.Contains("头发", StringComparison.Ordinal)
               || lower.Contains("hair", StringComparison.Ordinal);
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