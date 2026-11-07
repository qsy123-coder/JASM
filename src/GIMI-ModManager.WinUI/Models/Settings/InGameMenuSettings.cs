using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Settings;

/// <summary>
/// 游戏内 Mod 切换菜单（JASM_Menu.ini）的设置。
///
/// 存 <c>SettingScope.App</c>：呼出键与每页行数是这台机器上的使用习惯，换游戏不该各存一份。
///
/// 默认呼出键是单键 <c>F8</c>。实机验证：这个 3DMigoto 构建不认多修饰键组合
/// （`CTRL SHIFT VK_M` 匹配不上，游戏反而响应了 Ctrl+M），所以默认值选单键。
/// </summary>
public class InGameMenuSettings
{
    [JsonIgnore] public const string Key = "InGameMenuSettings";

    /// <summary>总开关。关闭后安装/启动都不会再生成菜单，已有菜单不受影响。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>3DMigoto <c>[Key*]</c> 里的写法，如 <c>F8</c>、<c>VK_F8</c>。</summary>
    public string OpenKeyTokens { get; set; } = "F8";

    /// <summary>菜单右上角显示的同一个键，如 <c>F8</c>。</summary>
    public string OpenKeyDisplay { get; set; } = "F8";

    /// <summary>一页最多几行；超出的部分用键盘选中自动切页。</summary>
    public int MaxRowsPerPage { get; set; } = 10;
}
