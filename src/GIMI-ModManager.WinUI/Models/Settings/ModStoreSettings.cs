using Newtonsoft.Json;

namespace GIMI_ModManager.WinUI.Models.Settings;

/// <summary>
/// Mod 商店自己的设置（PRD Phase 1 第 9 项的「隐藏成人内容」开关）。
///
/// 存 <c>SettingScope.App</c> 而不是 <c>Game</c>：这是个「我不想看到成人内容」的偏好，
/// 换了游戏照样成立，与同样是单游戏专属功能的浮窗（<see cref="OverlaySettings"/>）同一取舍。
/// 另一层原因是商店的内容跟着**当前选中的游戏**走，而游戏级设置文件是
/// ApplicationData_&lt;游戏&gt; —— 存游戏级的话，玩家切一次游戏这个开关就「丢了」，
/// 看起来就像开关失灵（切游戏要重启，回来一看开关回到默认，很难归因到「存错了地方」）。
/// </summary>
public class ModStoreSettings
{
    [JsonIgnore] public const string Key = "ModStoreSettings";

    /// <summary>
    /// 隐藏成人内容。**默认 true**（PRD 的硬性决定：默认不看到）。
    ///
    /// 极性与用户看到的文案一致（勾上 = 隐藏），而不是与
    /// <c>ModStoreService.IncludeAdultContent</c> 一致 —— 设置文件是给人看的，
    /// 存一份语义反转的「IncludeXxx」会让每个读它的人各转一次脑子。
    ///
    /// 老设置文件里**没有**这个字段时，Newtonsoft 只覆盖文件里出现过的键，
    /// 属性初始化值留得住，所以拿到的就是 true —— 正是要的默认值，不需要额外迁移。
    /// </summary>
    public bool HideAdultContent { get; set; } = true;
}