using System.Text;

namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>一个 mod 的 UHD 覆盖情况。</summary>
public sealed record UhdModCoverage(
    string Role,
    string ModName,
    string ModPath,
    int TextureSections,
    int CoveredHashes,
    int TotalHashes)
{
    /// <summary>有 hash 绑定但没被映射覆盖时，UHD 下很可能贴图错误。</summary>
    public bool NeedsLearning => TotalHashes > 0 && CoveredHashes < TotalHashes;
}

/// <summary>一个角色的汇总。</summary>
public sealed record UhdCharacterCoverage(
    string Role,
    string CharacterInternalName,
    IReadOnlyList<UhdModCoverage> Mods)
{
    public int ModCount => Mods.Count;

    public int HashModCount => Mods.Count(mod => mod.TotalHashes > 0);

    public int TotalHashes => Mods.Sum(mod => mod.TotalHashes);

    public int CoveredHashes => Mods.Sum(mod => mod.CoveredHashes);

    /// <summary>该角色还有多少 mod 的贴图 hash 没覆盖；为 0 表示现有映射够用。</summary>
    public int UncoveredMods => Mods.Count(mod => mod.NeedsLearning);

    public bool NeedsLearning => UncoveredMods > 0;
}

/// <summary>
/// UHD 映射覆盖体检：不启动游戏，静态检查每个 mod 的 <c>[TextureOverrideTexture*]</c> 绑定
/// 是否已被当前学习库（Rocket aliases + 内置表 + JASM 自学习）覆盖。
///
/// <para>
/// 判定单位是**角色**：同一角色的 mod 共用游戏贴图 hash，角色映射齐了，它的 mod 就不会错。
/// 「无 hash 绑定」的 mod（按槽位绑定的新式 WWMI）不受 UHD 档位影响。
/// </para>
/// </summary>
public static class UhdCoverageScanner
{
    /// <summary>
    /// 扫描一个角色下所有 mod。mod 入口是 (显示名, 根目录)；只读顶层 *.ini（跳过 .bak/.tmp）。
    /// </summary>
    public static UhdCharacterCoverage ScanCharacter(
        string role,
        string characterInternalName,
        IEnumerable<(string Name, string RootPath)> mods,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> mappings)
    {
        ArgumentNullException.ThrowIfNull(mods);
        ArgumentNullException.ThrowIfNull(mappings);

        var results = new List<UhdModCoverage>();
        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.RootPath) || !Directory.Exists(mod.RootPath))
                continue;

            var (sections, covered, total) = ScanModFiles(mod.RootPath, mappings);
            results.Add(new UhdModCoverage(UhdLearningStore.NormalizeRole(role), mod.Name, mod.RootPath,
                sections, covered, total));
        }

        return new UhdCharacterCoverage(UhdLearningStore.NormalizeRole(role),
            characterInternalName ?? string.Empty, results);
    }

    /// <summary>扫描单个 mod 目录：返回 (纹理段数, 已覆盖 hash 数, hash 总数)。</summary>
    public static (int TextureSections, int CoveredHashes, int TotalHashes) ScanModFiles(
        string modRootPath,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> mappings)
    {
        var sections = 0;
        var covered = 0;
        var total = 0;

        foreach (var iniPath in UhdActiveModManifestBuilder.EnumerateIniFiles(modRootPath))
        {
            string text;
            try
            {
                text = File.ReadAllText(iniPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var (name, hash) in ParseTextureBindings(text))
            {
                sections++;
                if (hash is null)
                    continue;

                total++;
                if (mappings.ContainsKey(hash))
                    covered++;
            }
        }

        return (sections, covered, total);
    }

    /// <summary>
    /// 解析顶层 ini 里的 <c>[TextureOverrideTexture*]</c> 段（排除 <c>_UHD_</c> 生成段），
    /// 返回 (段名, 首个 hash 或 null)。
    /// </summary>
    public static IEnumerable<(string Name, string? Hash)> ParseTextureBindings(string iniText)
    {
        if (string.IsNullOrEmpty(iniText))
            yield break;

        string? currentName = null;
        string? currentHash = null;

        foreach (var raw in iniText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
                continue;

            // 段头可能带同行注释（韩文 WWMI mod）：只取第一个 ']' 之前。
            var closing = trimmed.IndexOf(']');
            if (trimmed[0] == '[' && closing > 1)
            {
                if (currentName is not null)
                    yield return (currentName, currentHash);

                var name = trimmed[1..closing].Trim();
                currentName = name.StartsWith("TextureOverrideTexture", StringComparison.OrdinalIgnoreCase)
                              && !name.Contains("_UHD_", StringComparison.OrdinalIgnoreCase)
                    ? name
                    : null;
                currentHash = null;
                continue;
            }

            if (currentName is null || currentHash is not null)
                continue;

            var hash = UhdOverrideGenerator.TryReadHash(trimmed);
            if (hash is not null)
                currentHash = hash;
        }

        if (currentName is not null)
            yield return (currentName, currentHash);
    }

    /// <summary>生成可直接保存的体检报告（UTF-8 文本）。</summary>
    public static string FormatReport(IReadOnlyList<UhdCharacterCoverage> characters,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(characters);

        var builder = new StringBuilder();
        builder.AppendLine("# JASM UHD 覆盖体检");
        builder.AppendLine("# 说明：未覆盖只表示「学习库里没有该 HD hash 的 UHD 配对」，不等于 UHD 下一定坏 ——");
        builder.AppendLine("#       游戏对部分贴图仍绑定 HD 版本，mod 的 HD 覆盖照样生效。以实际画面为准。");
        builder.AppendLine($"# 生成时间: {generatedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"# 角色: {characters.Count}，有未覆盖 hash: {characters.Count(c => c.NeedsLearning)}");
        builder.AppendLine();
        builder.AppendLine("角色\t内部名\tmod 数\thash mod 数\t覆盖\t未覆盖 mod\t状态");

        foreach (var character in characters.OrderByDescending(c => c.UncoveredMods)
                     .ThenByDescending(c => c.TotalHashes)
                     .ThenBy(c => c.Role, StringComparer.OrdinalIgnoreCase))
        {
            var status = character.NeedsLearning ? "待核对" : "OK";
            builder.AppendLine(
                $"{character.Role}\t{character.CharacterInternalName}\t{character.ModCount}\t{character.HashModCount}\t{character.CoveredHashes}/{character.TotalHashes}\t{character.UncoveredMods}\t{status}");
        }

        builder.AppendLine();
        builder.AppendLine("# 明细（仅列出有未覆盖 hash 的角色）");

        foreach (var character in characters.Where(c => c.NeedsLearning)
                     .OrderByDescending(c => c.UncoveredMods))
        {
            builder.AppendLine($"[{character.Role}] {character.CharacterInternalName}");
            foreach (var mod in character.Mods.Where(m => m.NeedsLearning)
                         .OrderByDescending(m => m.TotalHashes - m.CoveredHashes))
            {
                builder.AppendLine(
                    $"  {mod.ModName}\t覆盖 {mod.CoveredHashes}/{mod.TotalHashes}\t{mod.ModPath}");
            }
        }

        return builder.ToString();
    }
}