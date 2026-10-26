namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 路径护栏：回滚只允许动「当前角色 Mod 根目录」里的文件。
///
/// 备份文件的名字来自磁盘扫描，目标路径由「备份名去掉时间戳」推导出来；如果某个名字里带
/// <c>..</c>、或者 Mod 目录里混进了指向别处的 junction，照着重命名就能把文件写到根外。
/// 扫描、预检、执行三个阶段都要过这一关（扫描后到执行前仍可能被替换）。
/// </summary>
public static class WuwaPathGuard
{
    /// <summary>
    /// 归一化为绝对路径并去掉末尾分隔符。比较统一走它，避免 <c>C:\Mods\</c> 与 <c>C:\Mods</c>
    /// 被当成两个不同的根。
    /// </summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return full;

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// 目标是否在根内（含根本身）。兄弟目录不算：<c>C:\Mods2</c> 不在 <c>C:\Mods</c> 里，
    /// 所以比较的是「根 + 分隔符」前缀，而不是裸字符串前缀。
    /// </summary>
    public static bool IsWithinRoot(string path, string root)
    {
        try
        {
            var target = Normalize(path);
            var baseRoot = Normalize(root);

            if (target.Length == 0 || baseRoot.Length == 0)
                return false;

            if (string.Equals(target, baseRoot, StringComparison.OrdinalIgnoreCase))
                return true;

            return target.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                   || target.StartsWith(baseRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException
                                      or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从根到目标逐段检查重解析点（符号链接 / junction）。目标还不存在时检查最近的已存在祖先：
    /// 回滚会新建文件，新建动作也必须落在一个真实目录里。
    /// </summary>
    public static bool ContainsReparsePoint(string path, string root)
    {
        try
        {
            var baseRoot = Normalize(root);
            var target = Normalize(path);

            if (!IsWithinRoot(target, baseRoot))
                return true;

            if (Directory.Exists(baseRoot) && IsReparsePoint(baseRoot))
                return true;

            var relative = Path.GetRelativePath(baseRoot, target);
            if (relative == ".")
                return false;

            var current = baseRoot;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);

                if (!Directory.Exists(current) && !File.Exists(current))
                    break;

                if (IsReparsePoint(current))
                    return true;
            }

            return false;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException
                                      or IOException or UnauthorizedAccessException)
        {
            // 路径本身已经不可信：按「不安全」处理，宁可不回滚也不写到别处
            return true;
        }
    }

    /// <summary>根内 + 无重解析点，才算可以安全读写。</summary>
    public static bool IsSafeTarget(string path, string root) =>
        IsWithinRoot(path, root) && !ContainsReparsePoint(path, root);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}