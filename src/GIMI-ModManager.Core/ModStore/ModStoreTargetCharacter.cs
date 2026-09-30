namespace GIMI_ModManager.Core.ModStore;

/// <summary>
/// 本地的一个角色，只取「对得上 GameBanana 分类名」需要的三段。
///
/// 不直接用 <c>ICharacter</c>：那样这份匹配逻辑就得挂在 <c>GameService</c> 上，
/// 而它需要整个游戏配置初始化完才能构造。这里只吃三个字符串，纯函数、能单测。
/// </summary>
/// <param name="InternalName">本地的内部名（不随语言变，是**主要**的匹配依据）。</param>
/// <param name="DisplayName">显示名（当前语言；中文语言包下是中文名）。</param>
/// <param name="Keys">别名。JASM 的搜索用它兜住拼写差异，这里同样拿来兜。</param>
public readonly record struct ModStoreCharacterCandidate(
    string InternalName,
    string DisplayName,
    IReadOnlyCollection<string> Keys);

/// <summary>
/// 一键部署时「这个 mod 装到哪个角色下面」的判定。
///
/// 依据是 GameBanana 的分类名（商店里那个「角色」标签，来自详情页的 <c>_aCategory</c>）。
/// 它和 JASM 的角色名**不是同一个口径**，所以这里刻意只认「完全相等」而不做模糊匹配：
/// 模糊匹配一旦给出错误答案，用户看到的就是「mod 装进了别的角色」，比不匹配还难发现
/// （不匹配至少会落到「Others」里，用户一眼能看出没归位、可以手动拖走）。
/// </summary>
public static class ModStoreTargetCharacter
{
    /// <summary>
    /// 找出分类名对应的角色下标；找不到返回 <c>-1</c>（调用方按「落 Others」处理）。
    ///
    /// 匹配顺序：内部名 → 显示名 → 别名。**内部名排第一**是因为它不受语言影响 ——
    /// 中文语言包下显示名是中文、`_aCategory` 给的是英文，先比显示名会白跑一轮。
    /// </summary>
    public static int ResolveIndex(string? gameBananaCharacterName,
        IReadOnlyList<ModStoreCharacterCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var name = gameBananaCharacterName?.Trim();
        if (string.IsNullOrEmpty(name))
            return -1;

        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].InternalName, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].DisplayName, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)))
                return i;
        }

        return -1;
    }
}