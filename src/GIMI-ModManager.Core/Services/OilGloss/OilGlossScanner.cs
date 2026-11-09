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

    /// <summary>
    /// 这些条件只是 WWMI 的「状态缓存 / 已检测」守卫，与网格是否可见无关；
    /// 拿它们判断可见性会把整块身体排除掉。
    /// </summary>
    private static readonly string[] IgnoredConditionMarkers =
        ["state_id", "mod_enabled", "object_detected"];

    private static readonly Regex SectionRegex = new(@"^\s*\[(.+?)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex DrawIndexedRegex =
        new(@"^\s*drawindexed\s*=\s*(\d+)\s*,\s*(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex IfRegex = new(@"^if\s+.+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ComponentIndexRegex =
        new(@"TextureOverrideComponent(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TextureSlotRegex =
        new(@"ResourceTexture(\w+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DrawCommentRegex = new(@"^\s*;\s*Draw\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 扫描整个 Mod 目录。单个文件读失败不会拖垮整次扫描。
    /// </summary>
    public static OilGlossScanResult Scan(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
        {
            return new OilGlossScanResult { Reason = "Mod 目录不存在" };
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

        // 头部/头发先按关键词排除（hat/hair/face/eye/brow/head）。
        // 不按索引区间猜：WWMI 的组件顺序不固定，按位置猜会把「最大的身体块」
        // 当成头发排掉，出现「身体没油光、头发反而变色」。
        //
        // 有网格数据时补上每块的高度范围：
        //   - 整块都在头部区域（75% 以上，例如脸/刘海/头饰）→ 不包油光；
        //   - 从腰部/上半身一直伸到头顶、却不覆盖下半身的（长发、发饰）→ 也不包。
        //     只看 min 会漏掉长发（0.44–0.97），它会被分进衣物组，一开衣物油光就整头变绿。
        // 覆盖到下半身的大块（身体）不受影响。
        var heights = OilGlossMeshGeometry.TryAnalyze(modFolderPath, annotated);

        // 只有 Mod 确实存在 Component3 及以后的网格时，才把 0–2 当脸/头发排除。
        // 如果整个 Mod 只有 0–2（不少老 Mod 的身体就在 Component0），说明编号约定不适用。
        var hasLaterComponents = annotated.Any(d => GetComponentIndex(d) >= 3);

        annotated = annotated
            .Select(d =>
            {
                var draw = heights is not null
                           && heights.TryGetValue((d.IniRelativePath, d.StartIndex, d.IndexCount), out var range)
                    ? d with { HeightMin = range.Min, HeightMax = range.Max }
                    : d;

                // 官方说明：身体/衣物通常位于 [TextureOverrideComponent3] 及更靠后的段落。
                // 0–2 多为脸/头发——没有几何信息（多套 Meshes 的 Mod）时只能靠这条兜底，
                // 否则它们会全部落进衣物组，一开衣物油光就把脸/头发糊掉。
                var headByComponent = hasLaterComponents && IsHeadComponent(draw);
                var headByHeight = draw.HeightMin is { } min && draw.HeightMax is { } max
                                   && (min >= 0.75 || (max >= 0.95 && min > 0.25));

                return draw with
                {
                    IsHeadLike = draw.IsHeadLike || headByComponent || headByHeight,
                };
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

        // 当前 drawcall 真正嵌套在哪些 if 里。必须跟踪 endif：
        // 只看「上面最近的 if」会把已经结束的分支算进来（例如身体块被上一个
        // "$headacc == 0" 分支误判成头部）。
        var conditionStack = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var sectionMatch = SectionRegex.Match(lines[i]);
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups[1].Value.Trim();
                conditionStack.Clear();
                continue;
            }

            if (!section.StartsWith("TextureOverride", StringComparison.OrdinalIgnoreCase))
                continue;

            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("endif", StringComparison.OrdinalIgnoreCase))
            {
                if (conditionStack.Count > 0)
                    conditionStack.RemoveAt(conditionStack.Count - 1);
                continue;
            }

            if (IfRegex.IsMatch(trimmed))
            {
                conditionStack.Add(GetConditionText(trimmed));
                continue;
            }

            var drawMatch = DrawIndexedRegex.Match(lines[i]);
            if (!drawMatch.Success)
                continue;

            var indexCount = int.Parse(drawMatch.Groups[1].Value);
            if (indexCount <= 0)
                continue;

            var startIndex = int.Parse(drawMatch.Groups[2].Value);
            var condition = GetInnermostCondition(conditionStack);
            var textureSlot = string.Empty;

            // 往回最多 30 行找同一段落里的贴图槽；遇到上一个段落头就停
            for (var j = i - 1; j >= 0 && j >= i - 30; j--)
            {
                if (SectionRegex.IsMatch(lines[j]))
                    break;

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

    /// <summary>去掉行尾注释后的 if 条件表达式。</summary>
    private static string GetConditionText(string trimmedIfLine)
    {
        var text = trimmedIfLine["if".Length..].Trim();
        var commentIndex = text.IndexOf(';');
        return commentIndex >= 0 ? text[..commentIndex].Trim() : text;
    }

    /// <summary>
    /// 取最内层的「变量条件」。JASM 自己插入的油光门控必须跳过，
    /// 否则补过丁的文件会把 <c>$\JasmOil...\oil_body</c> 当成作者写的条件。
    /// </summary>
    private static string GetInnermostCondition(IReadOnlyList<string> conditionStack)
    {
        for (var i = conditionStack.Count - 1; i >= 0; i--)
        {
            var condition = conditionStack[i];
            if (!condition.TrimStart().StartsWith('$'))
                continue;

            if (condition.Contains(OilGlossProtocol.MasterVariable, StringComparison.OrdinalIgnoreCase)
                || condition.Contains(OilGlossProtocol.BodyVariable, StringComparison.OrdinalIgnoreCase)
                || condition.Contains(OilGlossProtocol.ClothVariable, StringComparison.OrdinalIgnoreCase))
                continue;

            if (IgnoredConditionMarkers.Any(marker => condition.Contains(marker, StringComparison.OrdinalIgnoreCase)))
                continue;

            return condition;
        }

        return string.Empty;
    }

    /// <summary>
    /// 按大小 + 材质槽自动分组：
    /// <list type="bullet">
    ///   <item>身体组 = 身体锚点 + 与它同材质槽的可见网格（只给一块上油会变成「只有大腿亮」）；</item>
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

        // 身体：只取「身体锚点 + 同材质槽」的网格。
        // 不要把所有跨下半身的网格都收进来：KatFXMatcap 的 ShaderRegex 匹配面很宽，
        // 包到衣服/特殊材质上会直接变成贴图错误。身体被拆成上下两块的 Mod
        // 用详情页的「逐块识别身体」手动补上上半身。
        var bodyAnchor = ChooseBodyAnchor(pool);
        var bodySet = !string.IsNullOrEmpty(bodyAnchor.TextureSlot)
            ? pool.Where(d => string.Equals(d.TextureSlot, bodyAnchor.TextureSlot, StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<OilGlossDrawcall> { bodyAnchor };

        if (bodySet.Count == 0)
            bodySet = new List<OilGlossDrawcall> { bodyAnchor };

        // 作者明确标了 body 的其它变体（例如 body / body_squezzed 两套）一起包进身体组：
        // 只包其中一块时，游戏里切换形态后会出现「点了身体油光但没反应」。
        foreach (var namedBody in candidates.Where(IsNamedBody))
        {
            if (bodySet.All(b => !IsSame(b, namedBody)))
                bodySet.Add(namedBody);
        }

        // 衣物：其余全部非头部网格。包含默认隐藏的变体（丝袜/高跟等）——
        // 隐藏时没有画面，用户在菜单里打开该变体后油光才会出现；
        // 不包含的话就会出现「切了衣物油光但这个变体没反应」。
        var clothSet = candidates.Where(d => !bodySet.Any(b => IsSame(b, d))).ToList();

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

    private static int GetComponentIndex(OilGlossDrawcall draw)
    {
        var match = ComponentIndexRegex.Match(draw.SectionName);
        return match.Success && int.TryParse(match.Groups[1].Value, out var index) ? index : -1;
    }

    /// <summary>
    /// <c>[TextureOverrideComponent0/1/2]</c> 默认视为脸/头发/头饰（官方说明：身体/衣物
    /// 通常在 Component3 及更靠后）。有几何信息且它明确覆盖下半身时除外——
    /// 少数 Mod 会把身体放在前面的段落。
    /// </summary>
    private static bool IsHeadComponent(OilGlossDrawcall draw)
    {
        if (GetComponentIndex(draw) is < 0 or > 2)
            return false;

        return !(draw.HeightMin is <= 0.30 && draw.HeightMax is >= 0.60);
    }

    /// <summary>作者在段落名/注释/条件里明确写了 body 的网格（例如 <c>$draw_component_3_body_001</c>）。</summary>
    private static bool IsNamedBody(OilGlossDrawcall draw)
        => (draw.Comment + " " + draw.Condition + " " + draw.SectionName)
            .Contains("body", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 选身体锚点：有网格高度信息时，选「从下半身一直覆盖到躯干」且跨度最大的那块
    /// （角色身体皮肤）；没有几何信息时退回索引数最大的一块。
    /// </summary>
    private static OilGlossDrawcall ChooseBodyAnchor(IReadOnlyList<OilGlossDrawcall> pool)
    {
        var geometric = pool
            .Where(d => d.HeightMin is <= 0.25 && d.HeightMax is >= 0.60)
            .OrderByDescending(d => (d.HeightMax ?? 0) - (d.HeightMin ?? 0))
            .ThenByDescending(d => d.IndexCount)
            .FirstOrDefault();

        return geometric ?? pool.OrderByDescending(d => d.IndexCount).First();
    }

    private static bool IsSame(OilGlossDrawcall? left, OilGlossDrawcall? right)        => left is not null
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