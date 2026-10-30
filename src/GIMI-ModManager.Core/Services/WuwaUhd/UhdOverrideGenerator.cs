namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>生成结果：新 INI 文本 + 本次新增的 UHD section 名。</summary>
public sealed record UhdOverrideGenerationResult(string Text, IReadOnlyList<string> AddedSections)
{
    public bool HasChanges => AddedSections.Count > 0;
}

/// <summary>
/// 把 Rocket RoleHash 学到的「HD hash → UHD hash」映射落成 3DMigoto 的
/// <c>[TextureOverrideTexture*_UHD_&lt;hash&gt;]</c> 段。
///
/// 做法是**复制原 section 的整段内容**，只把 <c>hash =</c> 换成 UHD hash：
/// 这样 <c>match_priority</c>、<c>if $object_detected</c>、<c>this = ResourceXxx</c>
/// 等条件与资源绑定原样保留，UHD 贴图会绑到同一个 mod 资源上。
///
/// 幂等：文件里已存在该 UHD hash 或同名 section 就跳过；重复调用不会重复追加。
/// </summary>
public static class UhdOverrideGenerator
{
    public static UhdOverrideGenerationResult Generate(
        string? iniText,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> hdToUhdMap)
    {
        ArgumentNullException.ThrowIfNull(hdToUhdMap);

        var source = iniText ?? string.Empty;
        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var sections = ParseSections(lines);
        var existingHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            existingNames.Add(section.Name);
            foreach (var line in section.Body)
            {
                var hash = TryReadHash(line);
                if (hash is not null)
                    existingHashes.Add(hash);
            }
        }

        var added = new List<string>();
        var appended = new List<string>();

        foreach (var section in sections)
        {
            if (!section.Name.StartsWith("TextureOverrideTexture", StringComparison.OrdinalIgnoreCase))
                continue;

            var hdHash = section.Body.Select(TryReadHash).FirstOrDefault(h => h is not null);
            if (hdHash is null || !hdToUhdMap.TryGetValue(hdHash, out var uhdHashes))
                continue;

            foreach (var uhdHash in uhdHashes)
            {
                if (existingHashes.Contains(uhdHash))
                    continue;

                var newName = $"{section.Name}_UHD_{uhdHash}";
                if (!existingNames.Add(newName))
                    continue;

                appended.Add(string.Empty);
                appended.Add($"[{newName}]");

                var replaced = false;
                foreach (var line in section.Body)
                {
                    if (!replaced && TryReadHash(line) is not null)
                    {
                        appended.Add($"{LeadingWhitespace(line)}hash = {uhdHash}");
                        replaced = true;
                    }
                    else
                    {
                        appended.Add(line);
                    }
                }

                if (!replaced)
                    appended.Add($"hash = {uhdHash}");

                existingHashes.Add(uhdHash);
                added.Add(newName);
            }
        }

        if (appended.Count == 0)
            return new UhdOverrideGenerationResult(source, added);

        while (appended.Count > 0 && string.IsNullOrWhiteSpace(appended[^1]))
            appended.RemoveAt(appended.Count - 1);

        lines.AddRange(appended);

        var builder = new System.Text.StringBuilder();
        foreach (var line in lines)
            builder.Append(line).Append(newline);

        return new UhdOverrideGenerationResult(builder.ToString(), added);
    }

    private static List<IniSection> ParseSections(IReadOnlyList<string> lines)
    {
        var sections = new List<IniSection>();
        var headerIndexes = new List<(int Index, string Name)>();

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();

            // 段头后面可能跟同行注释（很多韩文 WWMI mod 写成 "[TextureOverrideTexture4];; comp 3"），
            // 不能要求最后一个字符是 ']'，否则整份 ini 一个段都解析不出来。
            var closing = trimmed.IndexOf(']');
            if (trimmed.Length >= 2 && trimmed[0] == '[' && closing > 1)
                headerIndexes.Add((i, trimmed[1..closing].Trim()));
        }

        for (var i = 0; i < headerIndexes.Count; i++)
        {
            var (start, name) = headerIndexes[i];
            var end = i + 1 < headerIndexes.Count ? headerIndexes[i + 1].Index : lines.Count;
            var body = new List<string>();
            for (var j = start + 1; j < end; j++)
                body.Add(lines[j]);

            while (body.Count > 0 && string.IsNullOrWhiteSpace(body[^1]))
                body.RemoveAt(body.Count - 1);

            sections.Add(new IniSection(name, body));
        }

        return sections;
    }

    /// <summary>解析 <c>hash = xxxxxxxx</c>；注释/段头/非 8 位十六进制一律返回 null。</summary>
    public static string? TryReadHash(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#' or '[')
            return null;

        var separator = trimmed.IndexOf('=');
        if (separator <= 0)
            return null;

        if (!trimmed[..separator].Trim().Equals("hash", StringComparison.OrdinalIgnoreCase))
            return null;

        return RoleHashAliasLibrary.NormalizeHash(trimmed[(separator + 1)..]);
    }

    private static string LeadingWhitespace(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        return line[..index];
    }

    private sealed record IniSection(string Name, List<string> Body);
}