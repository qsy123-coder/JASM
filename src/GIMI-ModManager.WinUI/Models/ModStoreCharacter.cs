namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// 商店左侧栏的一项 —— 一个角色，或者最上面那条「全部」。
///
/// <see cref="GbName"/> 是 GameBanana 的子分类名（也就是 GameBanana 上的角色名，实测 <c>Jinhsi</c>
/// / <c>Qingxiao</c>），有两个用途：跟卡片上的 <see cref="ModStoreItem.Character"/> 比对、
/// 以及当搜索接口的关键词。它跟 <see cref="DisplayName"/> 往往**不是一个字符串** ——
/// 后者是本地化的「今汐」。
///
/// 「全部」用 <see cref="GbName"/> = null 表示：空串会和「名字没解析出来」撞在一起。
/// </summary>
public sealed class ModStoreCharacter
{
    public string? GbName { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>左侧栏第一项，不筛任何角色。</summary>
    public static ModStoreCharacter All { get; } = new() { GbName = null, DisplayName = "全部" };
}