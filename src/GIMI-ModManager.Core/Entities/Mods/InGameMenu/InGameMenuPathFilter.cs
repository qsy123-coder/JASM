namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>
/// 3DMigoto 不会加载名字以 <c>DISABLED_</c> 开头的目录 / ini 文件（JASM 停用 Mod 就是这样命名）。
/// 菜单解析、门控变量收集、作者菜单检测都必须跳过它们 —— 否则菜单里会出现一堆
/// 「点了没反应」的行（对应的 ini 根本没被游戏加载），或者门控指向一个不存在的命名空间。
/// </summary>
public static class InGameMenuPathFilter
{
    public static bool IsDisabled(string modFolderPath, string filePath)
    {
        var relative = Path.GetRelativePath(modFolderPath, filePath);
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Any(segment =>
            segment.StartsWith("DISABLED_", StringComparison.OrdinalIgnoreCase));
    }
}
