using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Settings;

/// <summary>
/// 鸣潮「内置注入启动」设置。默认关闭：关闭时仍走 XXMI Launcher，打开后才由 JASM 直启 +
/// 提权助手注入 WWMI 的 d3d11.dll（Phase 1 不注入映射 DLL）。
/// </summary>
public class GameLaunchInjectionSettings
{
    [JsonIgnore] public const string Key = "GameLaunchInjectionSettings";

    public bool Enabled { get; set; }

    /// <summary>额外启动参数（可选）。<c>-dx11</c> 会由服务自动补上，资源档位参数也会自动拼。</summary>
    public string LaunchArguments { get; set; } = string.Empty;
}
