namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>菜单配色（非预乘 BGRA，与 DDS 写出时的通道顺序一致）。</summary>
public readonly record struct MenuColor(byte R, byte G, byte B, byte A)
{
    public static MenuColor Rgb(byte r, byte g, byte b, byte a = 255) => new(r, g, b, a);
}

/// <summary>
/// 一套菜单皮肤。所有颜色都由 JASM 自绘（不复制第三方素材），
/// 生成纹理时按这里的配色渲染；切换主题后需要重建菜单。
/// </summary>
public sealed record MenuTheme
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    // 面板
    public required MenuColor PanelFill { get; init; }
    public required MenuColor PanelBorder { get; init; }

    // 行底条（常态 / 悬停或选中）
    public required MenuColor RowTop { get; init; }
    public required MenuColor RowBottom { get; init; }
    public required MenuColor RowTopActive { get; init; }
    public required MenuColor RowBottomActive { get; init; }
    public required MenuColor RowBorder { get; init; }
    public required MenuColor RowBorderActive { get; init; }
    public required MenuColor RowInner { get; init; }

    // 页签
    public required MenuColor TabActive { get; init; }
    public required MenuColor TabInactive { get; init; }
    public required MenuColor TabBorder { get; init; }
    public required MenuColor TabAccent { get; init; }

    // 左侧竖栏与标题下划线
    public required MenuColor SidebarFill { get; init; }
    public required MenuColor SidebarBorder { get; init; }
    public required MenuColor Underline { get; init; }
}

/// <summary>内置皮肤。Id 会写进设置文件，改名会丢用户选择。</summary>
public static class MenuThemes
{
    public const string DefaultId = "default";

    /// <summary>深蓝 + 青色描边的原版风格。</summary>
    public static readonly MenuTheme Default = new()
    {
        Id = DefaultId,
        DisplayName = "深蓝青（默认）",
        PanelFill = MenuColor.Rgb(18, 22, 30, 236),
        PanelBorder = MenuColor.Rgb(96, 176, 224, 190),
        RowTop = MenuColor.Rgb(10, 28, 46),
        RowBottom = MenuColor.Rgb(5, 14, 26),
        RowTopActive = MenuColor.Rgb(20, 58, 90),
        RowBottomActive = MenuColor.Rgb(8, 26, 44),
        RowBorder = MenuColor.Rgb(90, 210, 250),
        RowBorderActive = MenuColor.Rgb(140, 235, 255),
        RowInner = MenuColor.Rgb(80, 170, 210, 150),
        TabActive = MenuColor.Rgb(40, 92, 128, 235),
        TabInactive = MenuColor.Rgb(8, 22, 38, 205),
        TabBorder = MenuColor.Rgb(90, 210, 250, 190),
        TabAccent = MenuColor.Rgb(160, 240, 255, 230),
        SidebarFill = MenuColor.Rgb(8, 12, 26, 210),
        SidebarBorder = MenuColor.Rgb(60, 120, 170, 170),
        Underline = MenuColor.Rgb(90, 220, 255),
    };

    /// <summary>暗红 + 暖金描边（《清宵·绯花令》的色感）。</summary>
    public static readonly MenuTheme Crimson = new()
    {
        Id = "crimson",
        DisplayName = "绯红金",
        PanelFill = MenuColor.Rgb(32, 16, 20, 236),
        PanelBorder = MenuColor.Rgb(214, 154, 82, 190),
        RowTop = MenuColor.Rgb(54, 20, 26),
        RowBottom = MenuColor.Rgb(28, 10, 14),
        RowTopActive = MenuColor.Rgb(96, 34, 36),
        RowBottomActive = MenuColor.Rgb(52, 18, 20),
        RowBorder = MenuColor.Rgb(226, 168, 92),
        RowBorderActive = MenuColor.Rgb(255, 210, 130),
        RowInner = MenuColor.Rgb(200, 120, 80, 150),
        TabActive = MenuColor.Rgb(128, 48, 44, 235),
        TabInactive = MenuColor.Rgb(38, 14, 18, 205),
        TabBorder = MenuColor.Rgb(214, 154, 82, 190),
        TabAccent = MenuColor.Rgb(255, 220, 150, 230),
        SidebarFill = MenuColor.Rgb(28, 10, 14, 210),
        SidebarBorder = MenuColor.Rgb(170, 110, 60, 170),
        Underline = MenuColor.Rgb(255, 196, 110),
    };

    /// <summary>黑底 + 白灰描边的极简风格。</summary>
    public static readonly MenuTheme Ink = new()
    {
        Id = "ink",
        DisplayName = "水墨黑",
        PanelFill = MenuColor.Rgb(12, 12, 12, 236),
        PanelBorder = MenuColor.Rgb(210, 210, 210, 180),
        RowTop = MenuColor.Rgb(34, 34, 34),
        RowBottom = MenuColor.Rgb(18, 18, 18),
        RowTopActive = MenuColor.Rgb(62, 62, 62),
        RowBottomActive = MenuColor.Rgb(30, 30, 30),
        RowBorder = MenuColor.Rgb(190, 190, 190),
        RowBorderActive = MenuColor.Rgb(240, 240, 240),
        RowInner = MenuColor.Rgb(150, 150, 150, 140),
        TabActive = MenuColor.Rgb(70, 70, 70, 235),
        TabInactive = MenuColor.Rgb(24, 24, 24, 205),
        TabBorder = MenuColor.Rgb(200, 200, 200, 190),
        TabAccent = MenuColor.Rgb(245, 245, 245, 230),
        SidebarFill = MenuColor.Rgb(18, 18, 18, 210),
        SidebarBorder = MenuColor.Rgb(150, 150, 150, 170),
        Underline = MenuColor.Rgb(235, 235, 235),
    };

    public static IReadOnlyList<MenuTheme> All { get; } = [Default, Crimson, Ink];

    public static MenuTheme Get(string? id)
        => All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? Default;
}
