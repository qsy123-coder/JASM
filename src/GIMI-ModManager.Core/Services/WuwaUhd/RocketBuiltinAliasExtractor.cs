using System.Text;

namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>
/// 从 Rocket 的 <c>Lod0_texture.dll</c> 里提取**内嵌的 RoleHash 别名表**。
///
/// Rocket 运行时并不只依赖本机学习：DLL 里内嵌了官方发布的 <c># RoleHash aliases v1</c>
/// 表（与 <c>RoleHash.aliases.tsv</c> 同样的 25 列格式，角色名为 UTF-8），
/// 覆盖了它支持的角色。装了 mod 就能在 UHD 下正常显示，靠的就是这份表。
///
/// JASM 只在**用户本机安装的 Rocket** 上读取这份表（不随 JASM 分发），
/// 结果并进 <see cref="UhdLearningStore"/>，与本地 aliases / 自学习结果合并使用。
/// </summary>
public static class RocketBuiltinAliasExtractor
{
    private const string AliasesHeader = "# RoleHash aliases v1";
    private const int MaxLineBytes = 4096;
    private const int MaxRows = 200_000;

    /// <summary>一个角色的全部 HD→UHD 配对。</summary>
    public sealed record RoleAliases(string Role, IReadOnlyList<(string HdHash, string UhdHash)> Pairs);

    /// <summary>读取 DLL 文件并提取；文件读不了返回空列表（绝不抛）。</summary>
    public static IReadOnlyList<RoleAliases> ExtractFromDll(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];

        try
        {
            return Extract(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>从内存字节里提取内嵌别名表（便于单测）。</summary>
    public static IReadOnlyList<RoleAliases> Extract(ReadOnlySpan<byte> bytes)
    {
        var header = Encoding.ASCII.GetBytes(AliasesHeader);
        var pairsByRole = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string Role, string Hd, string Uhd)>(
            new RolePairComparer());

        var searchFrom = 0;
        var total = 0;

        while (searchFrom < bytes.Length && total < MaxRows)
        {
            var headerIndex = bytes[searchFrom..].IndexOf(header);
            if (headerIndex < 0)
                break;
            headerIndex += searchFrom;
            searchFrom = headerIndex + header.Length;

            // 跳过表头自身那一行
            var position = IndexOfNewline(bytes, searchFrom);
            if (position < 0)
                break;
            position++;

            while (position < bytes.Length && total < MaxRows)
            {
                var lineEnd = IndexOfNewline(bytes, position);
                if (lineEnd < 0 || lineEnd - position > MaxLineBytes)
                    break;

                var line = bytes[position..lineEnd];
                position = lineEnd + 1;

                if (line.Length == 0 || line[0] == (byte)'#')
                    continue;

                if (!TryParseAliasRow(line, out var role, out var hd, out var uhd))
                {
                    // 表在遇到第一行非别名数据时结束
                    break;
                }

                var key = (role, hd, uhd);
                if (!seen.Add(key))
                    continue;

                if (!pairsByRole.TryGetValue(role, out var list))
                {
                    list = [];
                    pairsByRole[role] = list;
                }

                list.Add((hd, uhd));
                total++;
            }
        }

        return pairsByRole
            .Select(pair => new RoleAliases(pair.Key, pair.Value))
            .OrderByDescending(item => item.Pairs.Count)
            .ToArray();
    }

    /// <summary>把提取结果整体合并进学习库，返回新增配对数。</summary>
    public static int MergeIntoLibrary(IReadOnlyList<RoleAliases> aliases, string? libraryPath = null)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        var added = 0;
        foreach (var role in aliases)
        {
            if (role.Pairs.Count == 0 && string.IsNullOrWhiteSpace(role.Role))
                continue;

            added += UhdLearningStore.Merge(role.Role, role.Pairs, libraryPath);
        }

        return added;
    }

    private static bool TryParseAliasRow(ReadOnlySpan<byte> line, out string role, out string hd,
        out string uhd)
    {
        role = string.Empty;
        hd = string.Empty;
        uhd = string.Empty;

        var columns = SplitTabs(line);
        if (columns.Count < 25)
            return false;

        var roleBytes = columns[0];
        if (roleBytes.Length == 0)
            return false;

        string parsedRole;
        try
        {
            parsedRole = Encoding.UTF8.GetString(roleBytes.Span);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var hdText = Encoding.ASCII.GetString(columns[21].Span);
        var uhdText = Encoding.ASCII.GetString(columns[13].Span);

        if (!IsHash(hdText) || !IsHash(uhdText)
            || string.Equals(hdText, "00000000", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uhdText, "00000000", StringComparison.OrdinalIgnoreCase))
            return false;

        role = parsedRole.Trim();
        hd = hdText.ToLowerInvariant();
        uhd = uhdText.ToLowerInvariant();
        return role.Length > 0;
    }

    private static bool IsHash(string text)
    {
        if (text.Length != 8)
            return false;

        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        return true;
    }

    private static List<ReadOnlyMemory<byte>> SplitTabs(ReadOnlySpan<byte> line)
    {
        // 这里只分割、不拷贝：用 ReadOnlyMemory 需要数组，退化为 List<byte[]> 也行；
        // 行很短（<4KB），直接拷贝可接受。
        var columns = new List<ReadOnlyMemory<byte>>();
        var start = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != (byte)'\t')
                continue;

            columns.Add(line.Slice(start, i - start).ToArray());
            start = i + 1;
        }

        columns.Add(line[start..].ToArray());
        return columns;
    }

    private static int IndexOfNewline(ReadOnlySpan<byte> bytes, int from)
    {
        if (from >= bytes.Length)
            return -1;

        var index = bytes[from..].IndexOf((byte)'\n');
        return index < 0 ? -1 : index + from;
    }

    private sealed class RolePairComparer : IEqualityComparer<(string Role, string Hd, string Uhd)>
    {
        public bool Equals((string Role, string Hd, string Uhd) x, (string Role, string Hd, string Uhd) y) =>
            string.Equals(x.Role, y.Role, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Hd, y.Hd, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Uhd, y.Uhd, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Role, string Hd, string Uhd) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Role),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Hd),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Uhd));
    }
}