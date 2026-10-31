using System.Globalization;
using System.Text;

namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>
/// JASM 自研的 UHD 学习结果（不依赖 Rocket）。
///
/// 原生 <c>Jasm.UhdMapper.dll</c> 在 <c>learn=1</c> 模式下把「draw 身份 + PS 槽位 → 贴图 hash」
/// 记录到 <c>%LOCALAPPDATA%\JASM\UhdRuntime\learn.tsv</c>。HD 与 UHD 两档位各跑一次后，
/// 本类按 <c>(ibHash, firstIndex, slot)</c> 做内连接，得到 HD hash → UHD hash 的配对，
/// 存成 <c>learned-aliases.tsv</c>（每行：角色、HD hash、UHD hash）。
///
/// 它与 Rocket 的 aliases 是同一份数据语义，可并存：读取时两者合并（见
/// <see cref="LoadMappingsForRole"/>）。
/// </summary>
public static class UhdLearningStore
{
    public const string ObservationsFileName = "learn.tsv";
    public const string LibraryFileName = "learned-aliases.tsv";

    /// <summary>与原生 DLL 的观察文件放在同一目录（<c>%LOCALAPPDATA%\JASM\UhdRuntime</c>）。</summary>
    public static string DefaultDirectory => UhdActiveModStore.RuntimeDirectory;

    public static string LibraryPath => Path.Combine(DefaultDirectory, LibraryFileName);

    /// <summary>一次观察采样：某个 draw 的某个 PS 槽位绑定了哪张贴图。</summary>
    public sealed record Observation(string IbHash, uint FirstIndex, ushort Slot, string TextureHash);

    /// <summary>
    /// 解析原生 <c>learn.tsv</c>（列：ib_hash、first_index、slot、texture_hash、count）。
    /// 注释/空行/畸形行一律跳过，读的是外部进程写的文件，绝不抛异常。
    /// </summary>
    public static IReadOnlyList<Observation> ParseObservations(string path)
    {
        var result = new List<Observation>();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return result;

        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var columns = line.Split('\t');
                if (columns.Length < 4)
                    continue;

                var ibHash = RoleHashAliasLibrary.NormalizeHash(columns[0]);
                var textureHash = RoleHashAliasLibrary.NormalizeHash(columns[3]);
                if (ibHash is null || textureHash is null)
                    continue;

                if (!uint.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var firstIndex))
                    continue;
                if (!ushort.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out var slot))
                    continue;

                result.Add(new Observation(ibHash, firstIndex, slot, textureHash));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 读不到就当没有观察结果
        }

        return result;
    }

    /// <summary>
    /// 按 <c>(ibHash, firstIndex, slot)</c> 内连接 HD 与 UHD 观察，返回去重后的配对。
    /// 同一 key 在任一侧出现多个不同 hash 时全部保留（由调用方/生成器再决定取舍）。
    /// </summary>
    public static IReadOnlyList<(string HdHash, string UhdHash)> Join(
        IReadOnlyList<Observation> hdObservations,
        IReadOnlyList<Observation> uhdObservations)
    {
        ArgumentNullException.ThrowIfNull(hdObservations);
        ArgumentNullException.ThrowIfNull(uhdObservations);

        var uhdByKey = new Dictionary<(string IbHash, uint FirstIndex, ushort Slot),
            HashSet<string>>(KeyComparer.Instance);

        foreach (var observation in uhdObservations)
        {
            var key = (observation.IbHash, observation.FirstIndex, observation.Slot);
            if (!uhdByKey.TryGetValue(key, out var hashes))
            {
                hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                uhdByKey[key] = hashes;
            }

            hashes.Add(observation.TextureHash);
        }

        var pairs = new List<(string HdHash, string UhdHash)>();
        var seen = new HashSet<(string, string)>(PairComparer.Instance);

        foreach (var observation in hdObservations)
        {
            var key = (observation.IbHash, observation.FirstIndex, observation.Slot);
            if (!uhdByKey.TryGetValue(key, out var uhdHashes))
                continue;

            foreach (var uhdHash in uhdHashes)
            {
                if (string.Equals(observation.TextureHash, uhdHash, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (seen.Add((observation.TextureHash, uhdHash)))
                    pairs.Add((observation.TextureHash, uhdHash));
            }
        }

        return pairs;
    }

    /// <summary>读两份观察文件并配对。</summary>
    public static IReadOnlyList<(string HdHash, string UhdHash)> JoinFiles(string hdObservationsPath,
        string uhdObservationsPath)
        => Join(ParseObservations(hdObservationsPath), ParseObservations(uhdObservationsPath));

    /// <summary>
    /// 读取 <c>learned-aliases.tsv</c>：角色 → (HD hash → UHD hash 集合)。
    /// 文件不存在/畸形返回空字典。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>> Load(
        string? path = null)
    {
        var result = new Dictionary<string, Dictionary<string, HashSet<string>>>(
            StringComparer.OrdinalIgnoreCase);

        var filePath = path ?? LibraryPath;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>();

        try
        {
            foreach (var line in File.ReadLines(filePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var columns = line.Split('\t');
                if (columns.Length < 3)
                    continue;

                var role = columns[0].Trim();
                var hd = RoleHashAliasLibrary.NormalizeHash(columns[1]);
                var uhd = RoleHashAliasLibrary.NormalizeHash(columns[2]);
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
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyCollection<string>>>();
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, IReadOnlyCollection<string>>)pair.Value.ToDictionary(
                m => m.Key,
                m => (IReadOnlyCollection<string>)m.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把角色的一批配对合并进 <c>learned-aliases.tsv</c>（原子写）。返回新增的配对数。
    /// </summary>
    public static int Merge(string role, IEnumerable<(string HdHash, string UhdHash)> pairs,
        string? path = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var normalizedRole = NormalizeRole(role);
        if (normalizedRole.Length == 0)
            return 0;

        var existing = Load(path);
        var merged = new Dictionary<string, Dictionary<string, HashSet<string>>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var (roleName, mappings) in existing)
        {
            var target = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (hd, hashes) in mappings)
                target[hd] = new HashSet<string>(hashes, StringComparer.OrdinalIgnoreCase);
            merged[roleName] = target;
        }

        if (!merged.TryGetValue(normalizedRole, out var roleMappings))
        {
            roleMappings = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            merged[normalizedRole] = roleMappings;
        }

        var added = 0;
        foreach (var (hdHash, uhdHash) in pairs)
        {
            var hd = RoleHashAliasLibrary.NormalizeHash(hdHash);
            var uhd = RoleHashAliasLibrary.NormalizeHash(uhdHash);
            if (hd is null || uhd is null || string.Equals(hd, uhd, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!roleMappings.TryGetValue(hd, out var variants))
            {
                variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                roleMappings[hd] = variants;
            }

            if (variants.Add(uhd))
                added++;
        }

        if (added == 0)
            return 0;

        var filePath = path ?? LibraryPath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? DefaultDirectory);

        var builder = new StringBuilder();
        builder.AppendLine("# JASM UHD learned aliases v1");
        builder.AppendLine("# role\thd_hash\tuhd_hash");
        foreach (var (roleName, mappings) in merged.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (hd, hashes) in mappings.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var uhd in hashes.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
                    builder.Append(roleName).Append('\t').Append(hd).Append('\t').Append(uhd)
                        .AppendLine();
            }
        }

        var temp = filePath + ".tmp";
        File.WriteAllText(temp, builder.ToString(), new UTF8Encoding(false));
        File.Move(temp, filePath, overwrite: true);
        return added;
    }

    /// <summary>
    /// 取某角色的合并映射：Rocket aliases（可缺）+ JASM 自学习结果（可缺）。
    /// 两边都缺时返回空字典。生成覆盖段与写清单都用这一份。
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyCollection<string>> LoadMappingsForRole(
        string role, string? rocketAliasesPath = null, string? learnedPath = null)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        var rocketPath = rocketAliasesPath ?? Path.Combine(RoleHashAliasLibrary.DefaultDirectory,
            RoleHashAliasLibrary.AliasesFileName);
        if (File.Exists(rocketPath))
        {
            var fromRocket = RoleHashAliasLibrary.GetForRole(RoleHashAliasLibrary.Load(rocketPath), role);
            foreach (var (hd, hashes) in fromRocket)
            {
                if (!result.TryGetValue(hd, out var target))
                {
                    target = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[hd] = target;
                }

                foreach (var hash in hashes)
                    target.Add(hash);
            }
        }

        var learned = Load(learnedPath);
        if (learned.TryGetValue(role.Trim(), out var learnedMappings)
            || learned.TryGetValue(role, out learnedMappings))
        {
            foreach (var (hd, hashes) in learnedMappings)
            {
                if (!result.TryGetValue(hd, out var target))
                {
                    target = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    result[hd] = target;
                }

                foreach (var hash in hashes)
                    target.Add(hash);
            }
        }

        return result.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyCollection<string>)pair.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>清理角色名里的制表符/换行（它要写进 TSV 的第一列）。</summary>
    public static string NormalizeRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return string.Empty;

        return role.Trim().Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    private sealed class KeyComparer : IEqualityComparer<(string IbHash, uint FirstIndex, ushort Slot)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string IbHash, uint FirstIndex, ushort Slot) x,
            (string IbHash, uint FirstIndex, ushort Slot) y) =>
            x.FirstIndex == y.FirstIndex && x.Slot == y.Slot
            && string.Equals(x.IbHash, y.IbHash, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string IbHash, uint FirstIndex, ushort Slot) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.IbHash), obj.FirstIndex, obj.Slot);
    }

    private sealed class PairComparer : IEqualityComparer<(string Hd, string Uhd)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string Hd, string Uhd) x, (string Hd, string Uhd) y) =>
            string.Equals(x.Hd, y.Hd, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Uhd, y.Uhd, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Hd, string Uhd) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Hd),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Uhd));
    }
}