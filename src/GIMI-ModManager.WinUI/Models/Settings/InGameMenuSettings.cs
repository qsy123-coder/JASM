using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Settings;

/// <summary>
/// 游戏内 Mod 切换菜单（JASM_Menu.ini）的设置。
///
/// 存 <c>SettingScope.App</c>：呼出键与每页行数是这台机器上的使用习惯，换游戏不该各存一份。
///
/// 默认呼出键是单键 <c>F7</c>。实机验证：这个 3DMigoto 构建不认多修饰键组合
/// （`CTRL SHIFT VK_M` 匹配不上，游戏反而响应了 Ctrl+M），所以默认值选单键。
/// </summary>
public class InGameMenuSettings
{
    [JsonIgnore] public const string Key = "InGameMenuSettings";

    /// <summary>总开关。关闭后安装/启动都不会再生成菜单，已有菜单不受影响。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>3DMigoto <c>[Key*]</c> 里的写法，  <c>F7</c>、<c>VK_F8</c>。</summary>
    public string OpenKeyTokens { get; set; } = "F7";

    /// <summary>菜单右上角显示的同一个键，  <c>F7</c>。</summary>
    public string OpenKeyDisplay { get; set; } = "F7";

    /// <summary>一页最多几行；超出的部分用键盘选中自动切页。</summary>
    public int MaxRowsPerPage { get; set; } = 10;

    /// <summary>菜单皮肤 Id（见 <c>MenuThemes</c>），换皮肤后需要重建菜单。</summary>
    public string MenuTheme { get; set; } = "reference-assets";

    /// <summary>
    /// 「参考素材」主题使用的 PNG 目录（参考 Mod 的 <c>GUI\res</c>）。
    /// 为空或缺文件时退回同主题的自绘配色。
    /// </summary>
    public string ReferenceAssetsDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 每个 Mod 的菜单行自定义，键是 Mod 目录名（不含 DISABLED_ 前缀）。
    /// 值为行顺序与隐藏列表；未配置的 Mod 使用 ini 原顺序、全部显示。
    /// </summary>
    public Dictionary<string, InGameMenuRowLayout> RowLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>单个 Mod 的菜单行布局。</summary>
public class InGameMenuRowLayout
{
    /// <summary>行顺序；键是 <c>ini 相对路径|段落名</c>。</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>隐藏的行；键同上。</summary>
    public List<string> Hidden { get; set; } = [];
}
