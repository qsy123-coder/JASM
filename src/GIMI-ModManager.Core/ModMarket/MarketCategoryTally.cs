namespace GIMI_ModManager.Core.ModMarket;

/// <summary>
/// 分类计数的唯一收敛点：聚合视图路径与快照路径都产出本类型，
/// 上层组装 <c>ModMarketCategory</c> 时只此一处消费 —— 两条路径的算术不会漂移。
/// </summary>
public sealed record MarketCategoryTally
{
    private MarketCategoryTally(IReadOnlyDictionary<string, int> byCharacter, int total)
    {
        ByCharacter = byCharacter;
        Total = total;
        // 这里必须查字典而不是走 CountFor：CountFor 的 switch 会读 Ui/OtherMisc 这两个
        // 正在赋值的属性，转成自引用。
        Ui = Lookup(byCharacter, MarketCategoryKeys.Ui);
        OtherMisc = Lookup(byCharacter, MarketCategoryKeys.OtherMisc);
    }

    /// <summary>角色名 → 计数。空白角色名已被剔除。</summary>
    public IReadOnlyDictionary<string, int> ByCharacter { get; }

    public int Total { get; }

    public int Ui { get; }

    public int OtherMisc { get; }

    /// <summary>「角色皮肤」= 总数减去两个特殊分类。这条算术只此一处。</summary>
    public int Skins => Total - Ui - OtherMisc;

    /// <summary>
    /// 取某个分类键的计数。三个特殊键（Skins / UI / Other/Misc）都认，
    /// 其余按角色名精确匹配，不存在的返回 0 —— 上层组装分类列表时只调这一个方法。
    /// </summary>
    public int CountFor(string categoryKey) => categoryKey switch
    {
        MarketCategoryKeys.Skins => Skins,
        MarketCategoryKeys.Ui => Ui,
        MarketCategoryKeys.OtherMisc => OtherMisc,
        _ => Lookup(ByCharacter, categoryKey)
    };

    private static int Lookup(IReadOnlyDictionary<string, int> byCharacter, string key) =>
        byCharacter.TryGetValue(key, out var count) ? count : 0;

    /// <summary>由「角色名 → 计数」构建（聚合视图路径）。重复键累加。</summary>
    public static MarketCategoryTally FromCounts(IEnumerable<KeyValuePair<string, int>> counts)
    {
        var byCharacter = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;

        foreach (var (name, count) in counts)
        {
            // 与视图的 btrim(character) <> '' 口径一致；非正数计数同样丢弃（防御坏数据）。
            if (string.IsNullOrWhiteSpace(name) || count <= 0) continue;

            byCharacter[name] = byCharacter.TryGetValue(name, out var existing) ? existing + count : count;
            total += count;
        }

        return new MarketCategoryTally(byCharacter, total);
    }

    /// <summary>由角色名序列构建（快照路径：本地 group by）。委托给 <see cref="FromCounts"/>，计数规则只写一遍。</summary>
    public static MarketCategoryTally FromCharacters(IEnumerable<string?> characters) =>
        FromCounts(
            characters
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => new KeyValuePair<string, int>(name!, 1)));
}
