using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace JASM.Tests;

/// <summary>
/// 检测规则只回答一个问题：这个 Mod 的菜单能不能点？
/// 漏判会导致「本来能点的菜单被我们盖一层」，误判会导致「没法点的 Mod 永远得不到菜单」，
/// 所以这里把三种边界都钉死：完整命中、只画不点、只有鼠标键没有坐标。
/// </summary>
public class ClickableMenuDetectorTests
{
    [Fact]
    public void DetectsClickableMenuWithCursorAndLeftButton()
    {
        var detection = Detect("""
            namespace = HsinNait3D

            [KeyMouseClick]
            key = VK_LBUTTON
            type = hold

            [Present]
            $my_mouse_x = cursor_x * res_width
            run = CommandListDrawUI
            """);

        Assert.True(detection.HasClickableMenu);
        Assert.Contains("menu.ini", detection.MatchedFile);
    }

    [Fact]
    public void DrawOnlyMenuIsNotConsideredClickable()
    {
        var detection = Detect("""
            [Present]
            run = CommandListDrawUI

            [CommandListDrawUI]
            ps-t100 = ResourceTitle
            """);

        Assert.False(detection.HasClickableMenu);
    }

    [Fact]
    public void MouseKeyWithoutCursorCoordinatesIsNotClickable()
    {
        var detection = Detect("""
            [KeyAttack]
            key = VK_LBUTTON
            type = hold

            [Present]
            run = CommandListDrawUI
            """);

        Assert.False(detection.HasClickableMenu);
    }

    private static ClickableMenuDetection Detect(string iniContent)
    {
        var directory = Path.Combine(Path.GetTempPath(), "jasm-menu-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllText(Path.Combine(directory, "menu.ini"), iniContent);
            return ClickableMenuDetector.Detect(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
