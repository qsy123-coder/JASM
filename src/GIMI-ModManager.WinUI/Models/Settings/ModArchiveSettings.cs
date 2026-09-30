using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Settings;

internal class ModArchiveSettings
{
    [JsonIgnore] public const string Key = "ModArchiveSettings";
    public int MaxLocalArchiveCacheSizeGb { get; set; } = 10;

    /// <summary>
    /// 记住的压缩包密码（**明文**，用户决策：Mod 密码都是同一个，所以只留一条，不做密码簿）。
    /// 只有密码对话框会写它；「清除已记住的密码」也那个对话框里 —— 设置页没有对应 UI。
    ///
    /// <para>
    /// 它落在 <c>%LOCALAPPDATA%\JASM\ApplicationData\LocalSettings.json</c>。
    /// <b>谁读它谁负责：不得进日志、异常消息或通知。</b>
    /// </para>
    /// </summary>
    public string? ArchivePassword { get; set; }
}