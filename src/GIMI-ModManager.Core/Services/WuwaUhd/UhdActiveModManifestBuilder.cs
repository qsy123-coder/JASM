namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>一份 ini 文件的内容。测试直接喂文本，生产走 <see cref="UhdActiveModManifestBuilder.LoadMod"/>。</summary>
public sealed record UhdIniSource(string FilePath, string Text);

/// <summary>生成清单的 mod 输入：显示名 + 根目录 + 已读入的 ini。</summary>
public sealed record UhdActiveModInput(string Name, string RootPath, IReadOnlyList<UhdIniSource> IniFiles);

/// <summary>
/// 从 mod 的 3DMigoto ini 里解析「HD hash → 资源 → DDS 路径」，生成
/// <see cref="UhdActiveModManifest"/> 供原生映射 DLL 消费。
///
/// 只做**静态解析**：不执行 if/else 条件、不判断 mod 是否真的生效。解析规则与
/// <see cref="UhdOverrideGenerator"/> 保持一致（同一个 <c>TryReadHash</c>），两处对
/// 「哪一行算 hash」不会有分歧。
///
/// <para>
/// UHD 配对**只取 Rocket 学习库（aliases）**，不读 ini 里已生成的
/// <c>..._UHD_&lt;hash&gt;</c> 段。原因是这些段是历史产物、可能来自旧的学习数据：
/// 实机见过 <c>[TextureOverrideTexture8_UHD_7ad4d90b]</c> 把 HD=739ef6e9（NO 贴图）
/// 的 UHD hash 挂到了 Texture8 的 albedo 资源上，以及 <c>Texture1_UHD_6b454451</c>
/// 把共享 DLightmap 映射成 mod 贴图。原生 DLL 比 3DMigoto 更严格地信任这份清单，
/// 带上坏配对会直接把共享贴图换掉。
/// </para>
///
/// 这些段仍然会作为 3DMigoto 侧的绑定保留在 ini 里；本条只影响 JASM 注入路径。
/// </summary>
public static class UhdActiveModManifestBuilder
{
    private const string TextureOverridePrefix = "TextureOverrideTexture";
    private const string GeneratedUhdMarker = "_UHD_";
    private const string ResourcePrefix = "Resource";

    /// <summary>从内存里的 ini 文本构建清单（不碰文件系统，便于单测）。</summary>
    public static UhdActiveModManifest Build(
        string role,
        string roleInternalName,
        IReadOnlyList<UhdActiveModInput> mods,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? hdToUhdHashes = null,
        DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var entries = new List<UhdActiveModEntry>();
        foreach (var mod in mods)
            entries.Add(BuildModEntry(mod, hdToUhdHashes));

        return new UhdActiveModManifest
        {
            GeneratedAtUtc = generatedAtUtc ?? DateTimeOffset.UtcNow,
            Role = role?.Trim() ?? string.Empty,
            RoleInternalName = roleInternalName?.Trim() ?? string.Empty,
            Mods = entries
        };
    }

    /// <summary>
    /// 从磁盘构建：每个 mod 只读**顶层** <c>*.ini</c>（与「导入 Rocket UHD 映射」的
    /// 导入范围一致），读失败的文件跳过。
    /// </summary>
    public static UhdActiveModManifest BuildFromDirectory(
        string role,
        string roleInternalName,
        IEnumerable<(string Name, string RootPath)> mods,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? hdToUhdHashes = null,
        DateTimeOffset? generatedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(mods);

        var inputs = mods.Select(m => LoadMod(m.Name, m.RootPath)).ToArray();
        return Build(role, roleInternalName, inputs, hdToUhdHashes, generatedAtUtc);
    }

    /// <summary>读入一个 mod 的顶层 ini；目录不存在时返回空 IniFiles（不抛）。</summary>
    public static UhdActiveModInput LoadMod(string name, string rootPath)
    {
        var sources = new List<UhdIniSource>();

        foreach (var iniPath in EnumerateIniFiles(rootPath))
        {
            try
            {
                sources.Add(new UhdIniSource(iniPath, File.ReadAllText(iniPath)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 单个 ini 读不了不影响其它文件；DLL 侧拿不到该文件的映射而已
            }
        }

        return new UhdActiveModInput(name, rootPath, sources);
    }

    /// <summary>顶层 *.ini，排除备份（.bak）与写入中的临时文件（.tmp）。</summary>
    public static IReadOnlyList<string> EnumerateIniFiles(string modRootPath)
    {
        if (string.IsNullOrWhiteSpace(modRootPath) || !Directory.Exists(modRootPath))
            return [];

        try
        {
            return Directory.EnumerateFiles(modRootPath, "*.ini", SearchOption.TopDirectoryOnly)
                .Where(p => !p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static UhdActiveModEntry BuildModEntry(
        UhdActiveModInput mod,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? hdToUhdHashes)
    {
        var sections = mod.IniFiles
            .SelectMany(source => ParseSections(source.Text, source.FilePath))
            .ToArray();

        var resourceFiles = BuildResourceFileMap(sections);

        var textures = new List<UhdActiveModTexture>();
        var seen = new HashSet<(string Hash, string Resource)>(new HashResourceComparer());

        foreach (var section in sections)
        {
            if (!IsBaseTextureOverride(section.Name))
                continue;

            var hdHash = section.Body.Select(UhdOverrideGenerator.TryReadHash).FirstOrDefault(h => h is not null);
            if (hdHash is null)
                continue;

            var resources = section.Body
                .Select(line => TryReadKey(line, "this"))
                .Where(value => value is not null && value.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (resources.Length == 0)
                continue;

            var uhdHashes = CollectKnownUhdHashes(hdHash, hdToUhdHashes);

            foreach (var resource in resources)
            {
                if (!seen.Add((hdHash, resource)))
                    continue;

                resourceFiles.TryGetValue(resource, out var fileName);

                textures.Add(new UhdActiveModTexture
                {
                    HdHash = hdHash,
                    ResourceSection = resource,
                    DdsPath = ResolveResourcePath(fileName, mod.RootPath),
                    OverrideSection = section.Name,
                    UhdHashes = uhdHashes,
                    IniFile = section.FilePath
                });
            }
        }

        return new UhdActiveModEntry
        {
            Name = mod.Name,
            RootPath = mod.RootPath,
            IniFiles = mod.IniFiles.Select(s => s.FilePath).ToArray(),
            Textures = textures
        };
    }

    /// <summary>资源段名 → <c>filename</c>（跨 ini 全局查；同名以先出现的为准）。</summary>
    private static Dictionary<string, string?> BuildResourceFileMap(IEnumerable<IniSection> sections)
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in sections)
        {
            if (!section.Name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var fileName = section.Body
                .Select(line => TryReadKey(line, "filename"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (!map.ContainsKey(section.Name))
                map[section.Name] = Unquote(fileName);
        }

        return map;
    }

    /// <summary>
    /// 只从 Rocket 学习库取 UHD 配对。ini 里已有的 <c>..._UHD_&lt;hash&gt;</c> 段不参与
    /// （原因见类注释：它们可能来自旧学习数据，把共享贴图或别的贴图挂到本条目上）。
    /// </summary>
    private static IReadOnlyList<string> CollectKnownUhdHashes(
        string hdHash,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? hdToUhdHashes)
    {
        var result = new List<string>();

        if (hdToUhdHashes is null || !hdToUhdHashes.TryGetValue(hdHash, out var fromAliases))
            return result;

        foreach (var hash in fromAliases)
        {
            var normalized = RoleHashAliasLibrary.NormalizeHash(hash);
            if (normalized is not null && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                result.Add(normalized);
        }

        return result;
    }

    private static bool IsBaseTextureOverride(string sectionName) =>
        sectionName.StartsWith(TextureOverridePrefix, StringComparison.OrdinalIgnoreCase)
        && !sectionName.Contains(GeneratedUhdMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// <c>filename = Texture\foo.dds</c> → 绝对路径。相对路径按 mod 根目录解析；
    /// 非法路径/过长路径返回 null（DLL 侧跳过这条，不影响其它贴图）。
    /// </summary>
    public static string? ResolveResourcePath(string? fileName, string modRootPath)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var normalized = fileName.Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        try
        {
            var full = Path.IsPathRooted(normalized)
                ? normalized
                : Path.Combine(modRootPath, normalized);
            return Path.GetFullPath(full);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    /// <summary>读 <c>key = value</c>；注释行、段头、其它键返回 null。</summary>
    private static string? TryReadKey(string line, string key)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] is ';' or '#' or '[')
            return null;

        var separator = trimmed.IndexOf('=');
        if (separator <= 0)
            return null;

        if (!trimmed[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            return null;

        return trimmed[(separator + 1)..].Trim();
    }

    private static string? Unquote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim();
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            text = text[1..^1].Trim();

        return text.Length == 0 ? null : text.Replace('/', '\\');
    }

    private static IEnumerable<IniSection> ParseSections(string text, string filePath)
    {
        IniSection? current = null;

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = raw.Trim();

            // 段头可能带同行注释："[TextureOverrideTexture4];; comp 3"（韩文 WWMI mod 常见）。
            // 只认第一个 ']' 之前的部分，否则整份 ini 的段都会被漏掉。
            var closing = trimmed.IndexOf(']');
            if (trimmed.Length >= 2 && trimmed[0] == '[' && closing > 1)
            {
                if (current is not null)
                    yield return current;

                current = new IniSection(trimmed[1..closing].Trim(), new List<string>(), filePath);
                continue;
            }

            current?.Body.Add(raw);
        }

        if (current is not null)
            yield return current;
    }

    private sealed record IniSection(string Name, List<string> Body, string FilePath);

    private sealed class HashResourceComparer : IEqualityComparer<(string Hash, string Resource)>
    {
        public bool Equals((string Hash, string Resource) x, (string Hash, string Resource) y) =>
            string.Equals(x.Hash, y.Hash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Resource, y.Resource, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Hash, string Resource) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Hash),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Resource));
    }
}