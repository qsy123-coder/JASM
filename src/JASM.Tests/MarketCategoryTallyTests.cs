using GIMI_ModManager.Core.ModMarket;

namespace JASM.Tests;

/// <summary>
/// 聚合视图路径（角色 → 计数）与快照路径（角色名序列）必须产出同一份结果 ——
/// 这个类的意义就是把两条路径的分歧挡在编译单元里。
/// </summary>
public class MarketCategoryTallyTests
{
    private static MarketCategoryTally FromCounts(params (string Key, int Count)[] counts) =>
        MarketCategoryTally.FromCounts(counts.Select(c => new KeyValuePair<string, int>(c.Key, c.Count)));

    [Fact]
    public void Counts_AreExposedPerCharacter()
    {
        var tally = FromCounts(("千咲", 166), ("爱弥斯", 213), ("UI", 70), ("Other/Misc", 0));

        Assert.Equal(449, tally.Total);
        Assert.Equal(166, tally.CountFor("千咲"));
        Assert.Equal(213, tally.CountFor("爱弥斯"));
        Assert.Equal(70, tally.Ui);
    }

    /// <summary>「角色皮肤」是算出来的，不是数出来的：总数减去两个特殊分类。</summary>
    [Fact]
    public void Skins_IsTotalMinusUiAndOtherMisc()
    {
        var tally = FromCounts(("千咲", 166), ("UI", 70), ("Other/Misc", 21));

        Assert.Equal(257, tally.Total);
        Assert.Equal(21, tally.OtherMisc);
        Assert.Equal(166, tally.Skins);
    }

    [Fact]
    public void Skins_WithoutSpecialCategories_EqualsTotal()
    {
        var tally = FromCounts(("千咲", 10), ("琳奈", 5));

        Assert.Equal(15, tally.Skins);
        Assert.Equal(0, tally.Ui);
        Assert.Equal(0, tally.OtherMisc);
    }

    /// <summary>库里的 Other/Misc 计数是 0，但分类页仍要列出它 —— 这里锁住「不存在的键返回 0」。</summary>
    [Fact]
    public void CountFor_UnknownKey_IsZero()
    {
        var tally = FromCounts(("千咲", 1));

        Assert.Equal(0, tally.CountFor(MarketCategoryKeys.OtherMisc));
        Assert.Equal(0, tally.CountFor("不存在的角色"));
    }

    /// <summary>
    /// 三个特殊键都要能被 <see cref="MarketCategoryTally.CountFor"/> 认出来。
    /// Skins 尤其重要：它**不在** ByCharacter 里，是算出来的，直接查字典只会得到 0。
    /// </summary>
    [Fact]
    public void CountFor_ResolvesSpecialCategoryKeys()
    {
        var tally = FromCounts(("千咲", 4), ("UI", 2), ("Other/Misc", 1));

        // "all" 不是特殊键，也不在字典里 —— 「全部」那个数字只能从 Total 取。
        Assert.Equal(0, tally.CountFor(MarketCategoryKeys.All));
        Assert.Equal(7, tally.Total);
        Assert.Equal(4, tally.CountFor("千咲"));
        Assert.Equal(2, tally.CountFor(MarketCategoryKeys.Ui));
        Assert.Equal(1, tally.CountFor(MarketCategoryKeys.OtherMisc));
        Assert.Equal(4, tally.CountFor(MarketCategoryKeys.Skins));
        Assert.False(tally.ByCharacter.ContainsKey(MarketCategoryKeys.Skins));
    }

    [Fact]
    public void CountFor_SkinsFallsBackToTotalWhenNoSpecialCategories()
    {
        var tally = FromCounts(("千咲", 4), ("琳奈", 3));

        Assert.Equal(7, tally.CountFor(MarketCategoryKeys.Skins));
    }

    /// <summary>空白角色名要跳过：聚合视图那边用 <c>btrim(character) &lt;&gt; ''</c>，两侧口径必须一致。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void FromCounts_SkipsBlankCharacterNames(string blank)
    {
        var tally = FromCounts((blank, 7), ("千咲", 3));

        Assert.Equal(3, tally.Total);
        Assert.Single(tally.ByCharacter);
        Assert.Equal(3, tally.Skins); // 空白行既没进 Total，也没进 Ui/OtherMisc 这两个减项
    }

    [Fact]
    public void FromCounts_SkipsNonPositiveCounts()
    {
        var tally = FromCounts(("千咲", 3), ("空分组", 0), ("负数", -5));

        Assert.Equal(3, tally.Total);
        Assert.Single(tally.ByCharacter);
    }

    [Fact]
    public void FromCounts_AccumulatesDuplicateKeys()
    {
        var tally = FromCounts(("千咲", 2), ("千咲", 3));

        Assert.Equal(5, tally.Total);
        Assert.Equal(5, tally.CountFor("千咲"));
    }

    [Fact]
    public void FromCounts_Empty_IsAllZeros()
    {
        var tally = MarketCategoryTally.FromCounts([]);

        Assert.Empty(tally.ByCharacter);
        Assert.Equal(0, tally.Total);
        Assert.Equal(0, tally.Skins);
    }

    /// <summary>两条路径给同一批数据，必须逐项相等 —— 这是 Skins 算术只写一遍的意义。</summary>
    [Fact]
    public void FromCharacters_MatchesFromCounts()
    {
        string[][] cases =
        [
            ["千咲", "千咲", "UI"],
            ["爱弥斯", "UI", "UI", "Other/Misc"],
            ["千咲", "琳奈", "芙露德莉斯"]
        ];

        foreach (var characters in cases)
        {
            var fromCharacters = MarketCategoryTally.FromCharacters(characters);

            // 同一个输入先本地 group by，再喂给聚合视图那条入口。
            var fromCounts = MarketCategoryTally.FromCounts(
                characters.GroupBy(c => c).Select(g => new KeyValuePair<string, int>(g.Key, g.Count())));

            Assert.Equal(fromCounts.Total, fromCharacters.Total);
            Assert.Equal(fromCounts.Ui, fromCharacters.Ui);
            Assert.Equal(fromCounts.OtherMisc, fromCharacters.OtherMisc);
            Assert.Equal(fromCounts.Skins, fromCharacters.Skins);
            Assert.Equal(fromCounts.ByCharacter.OrderBy(p => p.Key), fromCharacters.ByCharacter.OrderBy(p => p.Key));
        }
    }

    /// <summary>快照里可能有空角色名或未发布行，本地计数必须与视图一样跳过它们。</summary>
    [Fact]
    public void FromCharacters_SkipsBlankAndNullNames()
    {
        var tally = MarketCategoryTally.FromCharacters(["千咲", null, "", "  ", "UI"]);

        Assert.Equal(2, tally.Total);
        Assert.Equal(1, tally.Ui);
        Assert.Equal(1, tally.Skins);
    }

    [Fact]
    public void FromCharacters_Empty_IsAllZeros() =>
        Assert.Equal(0, MarketCategoryTally.FromCharacters([]).Total);
}
