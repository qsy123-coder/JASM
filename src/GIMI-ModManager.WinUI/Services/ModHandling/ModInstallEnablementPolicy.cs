using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;

namespace GIMI_ModManager.WinUI.Services.ModHandling;

/// <summary>
/// "导入一个 Mod 之后要不要自动启用它"的唯一判据。
///
/// <para>
/// 浮窗的**单选模式**（<see cref="OverlaySettings.MultiSelectMode"/> = false）约定"一次只留一件"。
/// 但导入走的是另一条路：新 Mod 落盘时通常没有 <c>DISABLED_</c> 前缀，也就是**已经启用**，
/// 于是导入完成后列表里会多出好几件勾选 —— 单选模式的独占只在浮窗里勾选时执行，管不到导入。
/// </para>
///
/// <para>
/// 所以所有导入入口都要问这一句：单选模式下新 Mod 保持未启用，当前勾选不动；
/// 多选模式照旧（按安装器自己的"装完启用"设置走）。
/// </para>
/// </summary>
public static class ModInstallEnablementPolicy
{
    /// <summary>
    /// true = 允许导入后自动启用（多选模式）。
    /// 设置读不出来时按**单选**处理：不自动启用最安全，至少不会破坏"一次只留一件"。
    /// </summary>
    public static bool MayAutoEnableNewMod(ILocalSettingsService localSettingsService)
    {
        ArgumentNullException.ThrowIfNull(localSettingsService);

        try
        {
            var overlaySettings = localSettingsService.ReadSetting<OverlaySettings>(
                OverlaySettings.Key, SettingScope.App);

            return overlaySettings?.MultiSelectMode == true;
        }
        catch
        {
            return false;
        }
    }
}
