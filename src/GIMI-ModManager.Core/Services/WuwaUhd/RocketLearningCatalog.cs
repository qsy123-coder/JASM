using System.Text;

namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>某个角色在 Rocket 学习库里的进度。</summary>
public sealed record WuwaRoleLearningStatus(string Role, int HdReady, int UhdReady);

/// <summary>
/// 定位新版 Rocket，并读取它的 RoleHash 学习进度。
///
/// “新版”的判据是 <c>backing\Config\WW\Lod0_texture.dll</c> 已更新到带 RoleHash 的版本
/// （体积 ≥ 5 MB）。旧的 3.05 MB 版本没有 UHD 学习能力，不能用来做向导。
/// </summary>
public static class RocketLearningCatalog
{
    public const long MinimumRoleHashDllBytes = 5L * 1024 * 1024;
    public const string UpdatedDllRelativePath = @"backing\Config\WW\Lod0_texture.dll";
    public const string LearningDirectoryRelativePath = @"Rocket\Wuwa\AutoLearning";
    public const string StatusFileName = "learning-status.tsv";

    public static string LearningDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LearningDirectoryRelativePath);

    public static string StatusFilePath => Path.Combine(LearningDirectory, StatusFileName);

    /// <summary>
    /// 在一组根目录下做**有深度上限**的搜索，找到带新版 Lod0 DLL 的 Rocket.exe。
    /// 找不到返回 null；搜索失败/无权限的目录直接跳过。
    /// </summary>
    public static string? FindUpdatedRocket(IEnumerable<string>? searchRoots = null, int maxDepth = 3)
    {
        foreach (var root in searchRoots ?? DefaultSearchRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var found = FindUnder(root, maxDepth);
            if (found is not null)
                return found;
        }

        return null;
    }

    public static IEnumerable<string> DefaultSearchRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                yield return drive.RootDirectory.FullName;
        }
    }

    /// <summary>判断某个 Rocket.exe 的姊妹 DLL 是否是带 RoleHash 的新版。</summary>
    public static bool IsUpdatedRocket(string rocketExePath)
    {
        if (!File.Exists(rocketExePath))
            return false;

        var directory = Path.GetDirectoryName(rocketExePath);
        if (string.IsNullOrEmpty(directory))
            return false;

        var dll = Path.Combine(directory, UpdatedDllRelativePath);
        try
        {
            return File.Exists(dll) && new FileInfo(dll).Length >= MinimumRoleHashDllBytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>读取某个角色的学习进度；文件/角色不存在返回 null。</summary>
    public static WuwaRoleLearningStatus? ReadStatus(string roleName, string? statusFilePath = null)
    {
        var path = statusFilePath ?? StatusFilePath;
        if (!File.Exists(path) || string.IsNullOrWhiteSpace(roleName))
            return null;

        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;

                var columns = line.Split('\t');
                if (columns.Length < 7)
                    continue;

                if (!string.Equals(columns[0].Trim(), roleName.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;

                _ = int.TryParse(columns[3], out var hdReady);
                _ = int.TryParse(columns[5], out var uhdReady);
                return new WuwaRoleLearningStatus(columns[0].Trim(), hdReady, uhdReady);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static string? FindUnder(string root, int maxDepth)
    {
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (directory, depth) = queue.Dequeue();

            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "Rocket.exe", SearchOption.TopDirectoryOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (IsUpdatedRocket(file))
                    return file;
            }

            if (depth >= maxDepth)
                continue;

            string[] children;
            try
            {
                children = Directory.GetDirectories(directory);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.Equals("Windows", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    continue;

                queue.Enqueue((child, depth + 1));
            }
        }

        return null;
    }
}
