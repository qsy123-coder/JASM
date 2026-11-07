using System.Text;
using System.Text.RegularExpressions;
using GIMI_ModManager.Core.Entities.Mods.Helpers;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>
/// 扫描 Mod 目录里所有 ini，把 <c>[Key*]</c> 段落解析成菜单行。
///
/// 与既有的 <see cref="ModIniKeyBindingParser"/> 刻意分开：
/// 那个类服务「显示按键徽章 / 把按键发给游戏」，只关心 <c>key =</c>；
/// 这边要的是 <c>type</c> + <c>$变量 = 值列表</c>，用于「点击直接改变量」。
/// 两边的失败方式也不同：这边解析不出来只影响菜单，不能影响既有按键面板。
/// </summary>
public static partial class ModIniToggleParser
{
    private const string GeneratedMenuFileName = "JASM_Menu.ini";

    /// <summary>扫描整个 Mod 目录（递归）。</summary>
    public static IReadOnlyList<ModIniToggleRow> ParseDirectory(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
            return [];

        var rows = new List<ModIniToggleRow>();

        foreach (var file in Directory
                     .EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            // 不解析 JASM 自己的产物，否则重复生成时会把菜单段落当作者按键
            if (string.Equals(Path.GetFileName(file), GeneratedMenuFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            var relativePath = Path.GetRelativePath(modFolderPath, file);

            try
            {
                rows.AddRange(ParseText(IniFileText.ReadAllText(file), relativePath));
            }
            catch (IOException)
            {
                // 单个文件读不了不能拖垮整次生成
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return Deduplicate(rows);
    }

    /// <summary>解析单个 ini 的文本。测试入口：不碰文件系统。</summary>
    public static IReadOnlyList<ModIniToggleRow> ParseText(string iniContent, string relativePath)
    {
        if (string.IsNullOrEmpty(iniContent))
            return [];

        var lines = iniContent.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        var result = new List<ModIniToggleRow>();

        var fileNamespace = string.Empty;
        SectionBuilder? current = null;
        string? pendingComment = null;
        var sawSection = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var trimmed = raw.Trim();

            if (trimmed.Length == 0)
            {
                // 空行终止「紧贴段落」的注释块
                pendingComment = null;
                continue;
            }

            if (trimmed.StartsWith(';') || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                pendingComment = CleanComment(trimmed);
                continue;
            }

            if (SectionHeaderRegex().Match(trimmed) is { Success: true } sectionMatch)
            {
                Flush(current, result, relativePath, fileNamespace);
                sawSection = true;
                current = new SectionBuilder
                {
                    Header = sectionMatch.Groups[1].Value.Trim(),
                    PriorComment = pendingComment,
                };
                pendingComment = null;
                continue;
            }

            // namespace 是文件级指令，出现在第一个段落之前；段落里不会再有
            if (!sawSection && fileNamespace.Length == 0
                && KeyValueRegex().Match(trimmed) is { Success: true } nsMatch
                && string.Equals(nsMatch.Groups[1].Value.Trim(), "namespace", StringComparison.OrdinalIgnoreCase))
            {
                fileNamespace = Unquote(nsMatch.Groups[2].Value.Trim());
                continue;
            }

            if (current is null)
                continue;

            if (KeyValueRegex().Match(trimmed) is not { Success: true } kv)
            {
                // 格式 2：3DMigoto 允许 [Key*] 段落里直接写裸按键行，
                // 如 "no_ctrl no_shift VK_RIGHT" / "ctrl shift H"
                if (current.KeyValue.Length == 0 && IsBareKeyLine(trimmed))
                    current.KeyValue = trimmed;

                continue;
            }

            var name = kv.Groups[1].Value.Trim();
            var value = kv.Groups[2].Value.Trim();

            if (string.Equals(name, "key", StringComparison.OrdinalIgnoreCase))
            {
                current.KeyValue = value;
                continue;
            }

            if (string.Equals(name, "type", StringComparison.OrdinalIgnoreCase))
            {
                current.Type = value;
                continue;
            }

            if (string.Equals(name, "condition", StringComparison.OrdinalIgnoreCase))
            {
                current.Condition = StripInlineComment(value);
                continue;
            }

            // $变量 = 值1,值2,...；pre/post 是动作提示，不是循环目标
            if (name.StartsWith('$'))
            {
                var variableName = name.TrimStart('$');
                if (variableName.StartsWith("\\", StringComparison.Ordinal))
                    continue; // 跨命名空间的赋值，说明这段不是「本段声明的循环目标」

                current.Assignments.Add((variableName, SplitValues(value)));
            }
        }

        Flush(current, result, relativePath, fileNamespace);
        return Deduplicate(result);
    }

    /// <summary>
    /// 裸按键行：没有 <c>=</c>，且至少一个 token 能被 <see cref="VirtualKeyMap"/> 认成真实按键。
    /// 这样不会把 <c>run CommandListX</c> 之类的行误当成按键。
    /// </summary>
    private static bool IsBareKeyLine(string line)
    {
        if (line.Contains('=') || line.Contains('['))
            return false;

        var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length is 0 or > 4)
            return false;

        return tokens.Any(token =>
            !IsModifierToken(token) && VirtualKeyMap.Resolve(token).VirtualKeyCode.HasValue);
    }

    /// <summary>
    /// 一个段落只有「恰好一个至少两值的赋值」才算确定的循环目标；
    /// 多个赋值时可能是复合动作（如同时重置两个变量），点一下无法等价复现，降级为不可点击。
    /// </summary>
    private static void Flush(SectionBuilder? section, List<ModIniToggleRow> result, string relativePath,
        string fileNamespace)
    {
        if (section is null)
            return;

        var header = section.Header;
        if (!IsKeySection(header))
            return;

        var cycleCandidates = section.Assignments.Where(a => a.Values.Count >= 2).ToList();
        var variableName = string.Empty;
        IReadOnlyList<string> values = [];

        if (cycleCandidates.Count == 1)
        {
            variableName = cycleCandidates[0].Name;
            values = cycleCandidates[0].Values;
        }

        result.Add(new ModIniToggleRow
        {
            SectionName = "[" + header + "]",
            IniRelativePath = relativePath,
            Namespace = fileNamespace,
            VariableName = variableName,
            Values = values,
            KeyType = section.Type,
            KeyDisplay = FormatKeyDisplay(section.KeyValue),
            Condition = section.Condition,
            Label = BuildLabel(section.PriorComment, header),
        });
    }

    private static bool IsKeySection(string header)
        => header.StartsWith("Key", StringComparison.OrdinalIgnoreCase);

    /// <summary>把 <c>no_modifiers VK_UP</c> / <c>CTRL SHIFT H</c> 归一成可读的按键名。</summary>
    private static string FormatKeyDisplay(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return string.Empty;

        var tokens = rawKey.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keyToken = tokens.LastOrDefault(t => !IsModifierToken(t));
        if (keyToken is null)
            return string.Empty;

        return VirtualKeyMap.Resolve(keyToken).Display;
    }

    private static bool IsModifierToken(string token)
    {
        var lower = token.ToLowerInvariant();
        return lower is "ctrl" or "shift" or "alt" or "win"
            or "no_ctrl" or "no_shift" or "no_alt" or "no_modifiers";
    }

    /// <summary>
    /// 菜单标签：优先用紧贴段落上方的注释；没有就用段落名推导。
    /// 只做格式化，不做语义翻译（PRD 明确不猜）。
    /// </summary>
    private static string BuildLabel(string? priorComment, string header)
    {
        if (!string.IsNullOrWhiteSpace(priorComment))
            return Truncate(priorComment, 48);

        var name = header;
        if (name.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
            name = name[3..];
        if (name.StartsWith("Swap", StringComparison.OrdinalIgnoreCase))
            name = name[4..];
        if (name.Length == 0)
            name = header;

        // KeyBlackHairV176 → Black Hair V176
        name = SpaceBeforeUpperRegex().Replace(name, " ").Trim();
        if (name.Length == 0)
            return header;

        return Truncate(char.ToUpperInvariant(name[0]) + name[1..], 48);
    }

    private static string CleanComment(string line)
    {
        var text = line.TrimStart(';', '/', ' ', '\t').Trim();
        // 纯分隔线（==== / ---- / ####）不是标签
        return text.All(c => c is '=' or '-' or '#' or '*' or '_' or '~') ? string.Empty : text;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static string StripInlineComment(string value)
    {
        var index = value.IndexOf(';');
        return index >= 0 ? value[..index].Trim() : value.Trim();
    }

    private static string Unquote(string value)
        => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>
    /// 按逗号拆分循环候选值。引号内的逗号不拆；保留引号，因为生成 ini 时要原样回写。
    /// </summary>
    private static List<string> SplitValues(string raw)
    {
        var values = new List<string>();
        var buffer = new StringBuilder();
        var inQuote = false;
        var quoteChar = '\0';

        foreach (var c in raw)
        {
            if (inQuote)
            {
                buffer.Append(c);
                if (c == quoteChar)
                    inQuote = false;
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    inQuote = true;
                    quoteChar = c;
                    buffer.Append(c);
                    continue;
                case ';':
                    goto Done; // 行内注释
                case ',':
                    AddValue(values, buffer);
                    continue;
                default:
                    buffer.Append(c);
                    continue;
            }
        }

    Done:
        AddValue(values, buffer);
        return values;
    }

    private static void AddValue(List<string> values, StringBuilder buffer)
    {
        var value = buffer.ToString().Trim();
        buffer.Clear();
        if (value.Length > 0)
            values.Add(value);
    }

    /// <summary>同一个变量被多个段落绑定时只保留第一条，避免菜单出现重复行。</summary>
    private static IReadOnlyList<ModIniToggleRow> Deduplicate(List<ModIniToggleRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModIniToggleRow>(rows.Count);

        foreach (var row in rows)
        {
            var key = row.IsClickable
                ? row.QualifiedVariable
                : row.SectionName + "|" + row.IniRelativePath;

            if (seen.Add(key))
                result.Add(row);
        }

        return result;
    }

    [GeneratedRegex(@"^\s*\[(.+)\]\s*$")]
    private static partial Regex SectionHeaderRegex();

    [GeneratedRegex(@"^([^=\s]+)\s*=\s*(.*)$")]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex SpaceBeforeUpperRegex();

    private sealed class SectionBuilder
    {
        public required string Header { get; init; }
        public string? PriorComment { get; init; }
        public string KeyValue { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Condition { get; set; } = string.Empty;
        public List<(string Name, List<string> Values)> Assignments { get; } = [];
    }
}
