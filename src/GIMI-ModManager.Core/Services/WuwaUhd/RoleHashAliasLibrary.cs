using System.Text;

namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>
/// Rocket 新版 <c>Lod0_texture.dll</c> 的 RoleHash 学习结果。
///
/// 它把运行时观察到的「角色 / 组件 / 槽位」的 HD 与 UHD 贴图 hash 写成
/// <c>%LOCALAPPDATA%\Rocket\Wuwa\AutoLearning\RoleHash.aliases.tsv</c>。
/// JASM 不注入该 DLL，只读取这份学习结果，离线把 mod 的 HD 覆盖段补成 UHD 覆盖段。
///
/// 文件是制表符分隔、UTF-8，列位置（0 基）：
/// <code>
/// 0 角色名（中文）  1 组件 hash  2 ?  3 match_first_index  4 match_index_count
/// 5 ?  6/7 draw key  8-10 0  11 "ps"  12 槽位
/// 13 UHD hash（主）  14 格式  15 宽  16 高  17 mips …
/// 21 HD/original hash  22 UHD 变体 hash（可为 0 或与 13 相同）  23 data hash
/// </code>
/// 只认「HD → UHD」这一方向；HD 为空/全 0 的行跳过。
/// </summary>
public static class RoleHashAliasLibrary
{
    public const string AliasesFileName = "RoleHash.aliases.tsv";
    public const string LearningDirectoryRelativePath = @"Rocket\Wuwa\AutoLearning";

    /// <summary>默认学习目录：<c>%LOCALAPPDATA%\Rocket\Wuwa\AutoLearning</c>。</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LearningDirectoryRelativePath);

    /// <summary>
    /// 读取 aliases 文件，返回「角色 → (HD hash → UHD hash 集合)」。
    /// 文件不存在、行格式不对、hash 非法都跳过，绝不抛异常（读的是外部工具的输出）。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> Load(
        string aliasesFilePath)
    {
        var result = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(aliasesFilePath))
            return new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>();

        foreach (var line in File.ReadLines(aliasesFilePath, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            var columns = line.Split('\t');
            if (columns.Length < 23)
                continue;

            var role = columns[0].Trim();
            var uhd = NormalizeHash(columns[13]);
            var hd = NormalizeHash(columns[21]);
            var uhdVariant = NormalizeHash(columns[22]);

            if (role.Length == 0 || hd is null || uhd is null)
                continue;

            if (!result.TryGetValue(role, out var mappings))
            {
                mappings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                result[role] = mappings;
            }

            if (!mappings.TryGetValue(hd, out var variants))
            {
                variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                mappings[hd] = variants;
            }

            variants.Add(uhd);
            if (uhdVariant is not null && !string.Equals(uhdVariant, hd, StringComparison.OrdinalIgnoreCase))
                variants.Add(uhdVariant);
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, IReadOnlyCollection<string>>)pair.Value.ToDictionary(
                m => m.Key,
                m => (IReadOnlyCollection<string>)m.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>按角色取映射；角色名精确匹配（忽略大小写/首尾空白），找不到返回空字典。</summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> GetForRole(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> library,
        string roleName)
    {
        if (library.TryGetValue(roleName.Trim(), out var exact))
            return exact;

        foreach (var pair in library)
        {
            if (string.Equals(pair.Key.Trim(), roleName.Trim(), StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return new Dictionary<string, IReadOnlyCollection<string>>();
    }

    /// <summary>可用的角色名列表（给界面做兜底选择）。</summary>
    public static IReadOnlyList<string> GetRoleNames(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> library) =>
        library.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>8 位十六进制、非全 0 才算有效 hash；其它一律 null。</summary>
    public static string? NormalizeHash(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text) || text.Length != 8)
            return null;

        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
                return null;
        }

        return text == "00000000" ? null : text;
    }
}
