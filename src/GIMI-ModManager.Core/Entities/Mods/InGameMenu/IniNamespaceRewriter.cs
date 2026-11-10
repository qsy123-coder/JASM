using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>一次命名空间改名的记录；写进菜单清单，便于排查「作者 ini 为什么变了」。</summary>
public sealed record IniNamespaceRewrite(string File, string Original, string Renamed);

/// <summary>
/// 把 3DMigoto 表达式写不进去的命名空间改写成 ASCII 安全名。
///
/// 作者常写 <c>namespace = Mods\Xin-HunSha</c>：连字符不在 3DMigoto 变量 token 的合法
/// 字符集里（见 <see cref="IniVariableSyntax"/>），菜单里任何 <c>$\Mods\Xin-HunSha\var</c>
/// 都是语法错误；更糟的是无法识别的 <c>if</c> 会被丢掉、让里面的语句无条件执行，
/// 门控于是永远「在场」。桥接变量要额外的中转文件与逐帧同步，改动面大；
/// 而这里只做一件事：把命名空间换成 <c>JasmNsXXXXXXXX</c>（同一 Mod 内保持一致），
/// 菜单就能像普通 Mod 一样直接读写变量。
///
/// 每个被改名的文件在 <c>namespace</c> 行上方留一行标记
/// <c>; JASM: renamed namespace from &lt;原名&gt;</c>，<see cref="Restore"/> 靠它还原；
/// 引用的改写（含 <c>$\Ns\var</c> 与 <c>run = CommandList\Ns\Name</c>）覆盖整个 Mod 目录。
/// </summary>
public static class IniNamespaceRewriter
{
    /// <summary>改名标记行前缀；后面跟原始命名空间。</summary>
    public const string RenamedMarkerPrefix = "; JASM: renamed namespace from ";

    /// <summary>由原命名空间推出稳定的 ASCII 安全名（只取决于名字，不取决于 Mod 路径）。</summary>
    public static string BuildSafeNamespace(string original)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(original.Trim().ToUpperInvariant()));
        return "JasmNs" + Convert.ToHexString(hash)[..8];
    }

    /// <summary>
    /// 扫描 <paramref name="modFolderPath"/> 下所有 ini：发现表达式写不进的命名空间就改名，
    /// 并把所有引用（变量与命令列表）一起改写。返回本次涉及的改名记录。
    ///
    /// 只动 Mod 自己的 ini；JASM 生成的菜单/资源目录与 <c>DISABLED_</c> 之外的文件不跳过
    /// （DISABLED_ 里的同名命名空间也要跟着改，否则启用后两个命名空间会分家）。
    /// </summary>
    public static IReadOnlyList<IniNamespaceRewrite> Apply(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
            return [];

        var files = EnumerateIniFiles(modFolderPath);
        if (files.Count == 0)
            return [];

        // 1) 上次生成留下的改名标记：safe → original（重复生成时只需要继续替换新出现的引用）
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rewrites = new List<IniNamespaceRewrite>();
        var rewriteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var text = TryRead(file);
            if (text is null)
                continue;

            foreach (var (safe, original) in ReadMarkers(text))
            {
                if (!renamed.TryAdd(safe, original))
                    continue;

                var key = original + "|" + safe;
                if (rewriteKeys.Add(key))
                    rewrites.Add(new IniNamespaceRewrite(Path.GetRelativePath(modFolderPath, file), original, safe));
            }
        }

        // 2) 还没改过的非法命名空间
        var fresh = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var text = TryRead(file);
            if (text is null)
                continue;

            foreach (var ns in ReadNamespaceDirectives(text))
            {
                if (!IniVariableSyntax.IsExpressibleName(ns))
                    fresh.TryAdd(ns, BuildSafeNamespace(ns));
            }
        }

        if (fresh.Count == 0 && renamed.Count == 0)
            return [];

        // 原名 → 安全名（含上次已改名的映射，作者更新 Mod 后重新出现的引用也要改）
        var originalToSafe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (original, safe) in fresh)
            originalToSafe[original] = safe;
        foreach (var (safe, original) in renamed)
            originalToSafe.TryAdd(original, safe);

        foreach (var file in files)
        {
            var originalText = TryRead(file);
            if (originalText is null)
                continue;

            var text = RewriteDirectives(originalText, fresh, modFolderPath, file, rewrites, rewriteKeys);
            foreach (var (original, safe) in originalToSafe)
                text = ReplaceReferences(text, original, safe);

            if (string.Equals(text, originalText, StringComparison.Ordinal))
                continue;

            try
            {
                IniFileText.WriteAllTextPreservingEncoding(file, text);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(e, "[InGameMenu] 命名空间改写失败：{File}", file);
            }
        }

        return rewrites;
    }

    /// <summary>
    /// 还原所有改名（「移除菜单」时调用）：去掉标记行、把 namespace 行换回原名、
    /// 并把引用改回去。返回是否有文件被改动。
    /// </summary>
    public static bool Restore(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
            return false;

        var files = EnumerateIniFiles(modFolderPath);
        if (files.Count == 0)
            return false;

        var safeToOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;

        // 第一遍：去标记、还原 namespace 行
        foreach (var file in files)
        {
            var text = TryRead(file);
            if (text is null)
                continue;

            var lines = text.Split('\n');
            var result = new List<string>(lines.Length);
            string? pendingOriginal = null;
            var fileChanged = false;

            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();

                if (trimmed.StartsWith(RenamedMarkerPrefix, StringComparison.Ordinal))
                {
                    pendingOriginal = trimmed[RenamedMarkerPrefix.Length..].Trim();
                    fileChanged = true;
                    continue;
                }

                if (pendingOriginal is not null && TryReadNamespaceDirective(trimmed, out var safe))
                {
                    safeToOriginal[safe] = pendingOriginal;
                    result.Add("namespace = " + pendingOriginal + (raw.EndsWith('\r') ? "\r" : string.Empty));
                    pendingOriginal = null;
                    fileChanged = true;
                    continue;
                }

                result.Add(raw);
            }

            if (!fileChanged)
                continue;

            if (TryWrite(file, string.Join('\n', result)))
                changed = true;
        }

        if (safeToOriginal.Count == 0)
            return changed;

        // 第二遍：引用还原
        foreach (var file in files)
        {
            var text = TryRead(file);
            if (text is null)
                continue;

            var updated = text;
            foreach (var (safe, original) in safeToOriginal)
                updated = ReplaceReferences(updated, safe, original);

            if (string.Equals(updated, text, StringComparison.Ordinal))
                continue;

            if (TryWrite(file, updated))
                changed = true;
        }

        return changed;
    }

    /// <summary>
    /// 供内容哈希使用：把改名痕迹抵消掉（去标记、命名空间换回原名、引用换回原名），
    /// 这样油光补丁记录的哈希不会因为菜单生成改名而失配。
    /// </summary>
    public static string NormalizeForHash(string text)
    {
        if (!text.Contains(RenamedMarkerPrefix, StringComparison.Ordinal))
            return text;

        var lines = text.Split('\n');
        var result = new List<string>(lines.Length);
        var safeToOriginal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pendingOriginal = null;

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();

            if (trimmed.StartsWith(RenamedMarkerPrefix, StringComparison.Ordinal))
            {
                pendingOriginal = trimmed[RenamedMarkerPrefix.Length..].Trim();
                continue;
            }

            if (pendingOriginal is not null && TryReadNamespaceDirective(trimmed, out var safe))
            {
                safeToOriginal[safe] = pendingOriginal;
                result.Add("namespace = " + pendingOriginal + (raw.EndsWith('\r') ? "\r" : string.Empty));
                pendingOriginal = null;
                continue;
            }

            result.Add(raw);
        }

        var normalized = string.Join('\n', result);
        foreach (var (safe, original) in safeToOriginal)
            normalized = ReplaceReferences(normalized, safe, original);

        return normalized;
    }

    private static List<string> EnumerateIniFiles(string modFolderPath)
        => Directory
            .EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFileName(file), InGameMenuGenerator.MenuIniFileName,
                StringComparison.OrdinalIgnoreCase))
            .Where(file => !IsUnderJasmAssets(modFolderPath, file))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsUnderJasmAssets(string modFolderPath, string file)
    {
        var relative = Path.GetRelativePath(modFolderPath, file);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment =>
            string.Equals(segment, InGameMenuGenerator.AssetsDirectoryName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryRead(string file)
    {
        try
        {
            return IniFileText.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryWrite(string file, string text)
    {
        try
        {
            IniFileText.WriteAllTextPreservingEncoding(file, text);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(e, "[InGameMenu] 命名空间改写失败：{File}", file);
            return false;
        }
    }

    /// <summary>读过文本里的改名标记（标记行 + 紧随其后的 namespace 行）。</summary>
    private static IEnumerable<(string Safe, string Original)> ReadMarkers(string text)
    {
        string? pendingOriginal = null;

        foreach (var raw in text.Split('\n'))
        {
            var trimmed = raw.Trim();

            if (trimmed.StartsWith(RenamedMarkerPrefix, StringComparison.Ordinal))
            {
                pendingOriginal = trimmed[RenamedMarkerPrefix.Length..].Trim();
                continue;
            }

            if (pendingOriginal is null)
                continue;

            if (TryReadNamespaceDirective(trimmed, out var safe))
            {
                yield return (safe, pendingOriginal);
                pendingOriginal = null;
            }
            else if (trimmed.Length > 0 && !trimmed.StartsWith(';') && !trimmed.StartsWith('/'))
            {
                pendingOriginal = null;
            }
        }
    }

    /// <summary>文本里所有 <c>namespace = X</c> 行的值（跳过注释）。</summary>
    private static IEnumerable<string> ReadNamespaceDirectives(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            if (TryReadNamespaceDirective(raw.Trim(), out var value))
                yield return value;
        }
    }

    /// <summary>解析 <c>namespace = X</c>（容忍引号与行内注释）。</summary>
    private static bool TryReadNamespaceDirective(string trimmedLine, out string value)
    {
        value = string.Empty;

        if (trimmedLine.Length == 0 || trimmedLine[0] is ';' or '/')
            return false;

        const string keyword = "namespace";
        if (!trimmedLine.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = trimmedLine[keyword.Length..];
        if (rest.Length == 0 || (rest[0] != '=' && !char.IsWhiteSpace(rest[0])))
            return false;

        var eq = rest.IndexOf('=');
        if (eq < 0)
            return false;

        var raw = rest[(eq + 1)..];
        var semicolon = raw.IndexOf(';');
        if (semicolon >= 0)
            raw = raw[..semicolon];

        value = raw.Trim().Trim('"');
        return value.Length > 0;
    }

    /// <summary>把新的非法命名空间声明改成安全名，并在其上方插入标记行。</summary>
    private static string RewriteDirectives(
        string text,
        IReadOnlyDictionary<string, string> fresh,
        string modFolderPath,
        string file,
        List<IniNamespaceRewrite> rewrites,
        HashSet<string> rewriteKeys)
    {
        if (fresh.Count == 0)
            return text;

        var lines = text.Split('\n');
        var result = new List<string>(lines.Length + 4);
        // 行按 '\n' 切分，CRLF 文件的行尾 '\r' 要自己补回去
        var cr = IniFileText.UsesCrLf(text) ? "\r" : string.Empty;

        foreach (var raw in lines)
        {
            var trimmed = raw.TrimEnd('\r');
            if (!TryReadNamespaceDirective(trimmed, out var value) || !fresh.TryGetValue(value, out var safe))
            {
                result.Add(raw);
                continue;
            }

            var previous = result.Count > 0 ? result[^1].TrimStart() : string.Empty;
            if (!previous.StartsWith(RenamedMarkerPrefix, StringComparison.Ordinal))
                result.Add(RenamedMarkerPrefix + value + cr);

            result.Add("namespace = " + safe + cr);

            var key = value + "|" + safe;
            if (rewriteKeys.Add(key))
                rewrites.Add(new IniNamespaceRewrite(Path.GetRelativePath(modFolderPath, file), value, safe));
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// 把 <c>\Namespace\</c> 引用替换成新名字。变量引用（<c>$\Ns\var</c>）与命令列表引用
    /// （<c>run = CommandList\Ns\Name</c>）都靠反斜杠包围来定位，所以同一个替换同时覆盖两者。
    /// </summary>
    private static string ReplaceReferences(string text, string from, string to)
        => ReferenceRegex(from).Replace(text, "\\" + to + "\\");

    private static Regex ReferenceRegex(string ns)
        => new(@"\\" + Regex.Escape(ns) + @"\\", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}