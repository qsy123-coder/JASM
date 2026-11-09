using System.Text.RegularExpressions;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>
/// 判断一个 Mod 是否已经自带「可点击的」游戏内菜单。
///
/// 判定只看 3DMigoto 的原语组合，不看文件名：
/// 同一个 ini 里同时出现 <c>[Present]</c>、鼠标坐标（<c>cursor_x/y</c> 或 <c>$my_mouse_x</c>）
/// 与鼠标键（<c>VK_LBUTTON</c> 等）或命中变量，才算能点。
/// 只画不点的菜单（只有 [Present] + 绘制、没有坐标/鼠标键）会被判定为「不可点击」，仍然补菜单。
/// </summary>
public static partial class ClickableMenuDetector
{
    public static ClickableMenuDetection Detect(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
            return new ClickableMenuDetection(false, null, "Mod 目录不存在");

        foreach (var file in Directory
                     .EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(file), "JASM_Menu.ini", StringComparison.OrdinalIgnoreCase))
                continue;

            // DISABLED_ 文件 / 目录不会被游戏加载，里面的菜单不构成"作者已有可点击菜单"
            if (InGameMenuPathFilter.IsDisabled(modFolderPath, file))
                continue;

            string text;
            try
            {
                text = IniFileText.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (!PresentRegex().IsMatch(text))
                continue;

            var hasCursor = CursorRegex().IsMatch(text);
            var hasMouse = MouseKeyRegex().IsMatch(text) || MouseHitRegex().IsMatch(text);

            if (hasCursor && hasMouse)
            {
                return new ClickableMenuDetection(
                    HasClickableMenu: true,
                    MatchedFile: Path.GetRelativePath(modFolderPath, file),
                    Reason: "检测到 [Present] + 鼠标坐标 + 鼠标点击");
            }
        }

        return new ClickableMenuDetection(false, null, "未检测到可点击菜单");
    }

    [GeneratedRegex(@"(?im)^\s*\[Present\]\s*$")]
    private static partial Regex PresentRegex();

    [GeneratedRegex(@"\bcursor_x\b|\bcursor_y\b|\$my_mouse_x\b|\$my_mouse_y\b")]
    private static partial Regex CursorRegex();

    [GeneratedRegex(@"\bVK_LBUTTON\b|\bVK_RBUTTON\b|\bVK_MBUTTON\b", RegexOptions.IgnoreCase)]
    private static partial Regex MouseKeyRegex();

    [GeneratedRegex(@"\bis_cursorInRect\b|\bisMouseClicked\b|\bisMouseButtonDown\b", RegexOptions.IgnoreCase)]
    private static partial Regex MouseHitRegex();
}

/// <summary>检测结果。失败/跳过时 <see cref="Reason"/> 用于写进 Mod 状态与日志。</summary>
public sealed record ClickableMenuDetection(bool HasClickableMenu, string? MatchedFile, string Reason);
