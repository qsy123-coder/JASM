using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Settings;

internal class ModArchiveSettings
{
    [JsonIgnore] public const string Key = "ModArchiveSettings";
    public int MaxLocalArchiveCacheSizeGb { get; set; } = 10;

    // 这里曾经有一条 ArchivePassword（记住用户输入的密码，明文落盘）。
    // 用户决策（2026-10-01）改成内置常量后它就没了 —— 密码现在只在代码里，一份都不落设置，
    // 见 ModHandling.ModArchivePassword。
}