using GIMI_ModManager.Core.ModMarket;

namespace JASM.Tests;

/// <summary>
/// 锁住 PostgREST <c>title=ilike.*q*</c> 的复刻：搜索词是 **LIKE 模式**而不是字面量子串。
/// 线上把用户输入夹在 <c>*…*</c> 之间后交给 Postgres，所以 <c>%</c> / <c>_</c> / <c>\</c>
/// 在库里是元字符 —— 用 <c>string.Contains</c> 会直接不等价（库里 30 个标题含下划线）。
/// </summary>
public class MarketLikePatternTests
{
    [Theory]
    [InlineData("心月狐", "心月狐 by kuzan")]
    [InlineData("kuzan", "心月狐 by kuzan")]
    [InlineData("KUZAN", "心月狐 by kuzan")]
    [InlineData("月", "心月狐 by kuzan")]
    public void MatchesAsSubstring_CaseInsensitively(string pattern, string title) =>
        Assert.True(MarketLikePattern.Create(pattern).IsMatch(title));

    [Fact]
    public void DoesNotMatch_UnrelatedTitle() =>
        Assert.False(MarketLikePattern.Create("心月狐").IsMatch("哥伦比娅 by woju"));

    /// <summary>空/空白模式在线上不会出现（只有 search 非空白时才加这个过滤），取「全部匹配」。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptyPattern_MatchesEverything(string? pattern)
    {
        Assert.True(MarketLikePattern.Create(pattern).IsMatch("任意标题"));
        Assert.True(MarketLikePattern.Create(pattern).IsMatch(string.Empty));
    }

    [Fact]
    public void NullValue_NeverMatches() =>
        Assert.False(MarketLikePattern.Create("a").IsMatch(null));

    /// <summary><c>*</c> 在 PostgREST 里被换成 <c>%</c>，两级都要当通配符。</summary>
    [Theory]
    [InlineData("100%")]
    [InlineData("100*")]
    public void Wildcard_MatchesAnyLength(string pattern)
    {
        var like = MarketLikePattern.Create(pattern);
        Assert.True(like.IsMatch("Percent 100% Wildcard"));
        Assert.True(like.IsMatch("100abc"));
        Assert.False(like.IsMatch("10"));
    }

    /// <summary>
    /// 最容易写错的一条：<c>_</c> 是「任意单字符」而不是字面下划线。
    /// fixture 里 "a_b underscore" 与 "axb underscore" 成对存在正是为了锁这个。
    /// </summary>
    [Fact]
    public void Underscore_MatchesAnySingleCharacter_NotLiteralUnderscore()
    {
        var like = MarketLikePattern.Create("a_b");

        Assert.True(like.IsMatch("a_b underscore"));
        Assert.True(like.IsMatch("axb underscore"));
        Assert.False(like.IsMatch("ab underscore")); // 少一个字符就不该中
    }

    /// <summary>真实标题里的 <c>by _eldarC</c>：线上 <c>%_eldarC%</c> 会命中（下划线匹配自身）。</summary>
    [Fact]
    public void Underscore_MatchesRealTitle()
    {
        Assert.True(MarketLikePattern.Create("_eldarC").IsMatch("可凯露（ctrl+num+1234）by _eldarC"));
        Assert.True(MarketLikePattern.Create("_eldarC").IsMatch("by XeldarC"));
    }

    /// <summary><c>\x</c> 转义成字面量 —— 这是唯一能表达「真的要一个下划线」的方式。</summary>
    [Fact]
    public void Backslash_EscapesNextCharacterToLiteral()
    {
        var literal = MarketLikePattern.Create(@"\_eldarC");

        Assert.True(literal.IsMatch("by _eldarC"));
        Assert.False(literal.IsMatch("by XeldarC"));
    }

    /// <summary>转义反斜杠自身：模式里的两个反斜杠表示一个字面反斜杠。</summary>
    [Fact]
    public void Backslash_EscapesBackslash()
    {
        var like = MarketLikePattern.Create(@"\\");

        Assert.True(like.IsMatch(@"Back\slash Title"));
        Assert.False(like.IsMatch("Back/slash Title"));
    }

    /// <summary>正则元字符必须被当字面量，否则一个 "(" 就能让匹配行为跑偏。</summary>
    [Theory]
    [InlineData("(")]
    [InlineData(")")]
    [InlineData("[")]
    [InlineData("+")]
    [InlineData("?")]
    [InlineData("^")]
    [InlineData("$")]
    [InlineData("|")]
    public void RegexMetacharacters_AreLiteral(string pattern)
    {
        var like = MarketLikePattern.Create(pattern);

        Assert.True(like.IsMatch($"x{pattern}y"));
        Assert.False(like.IsMatch("xy"));
    }

    /// <summary>结尾孤立的反斜杠：Postgres 会直接报错，本地取宽松（当字面量）。</summary>
    [Fact]
    public void TrailingBackslash_IsTreatedAsLiteral() =>
        Assert.True(MarketLikePattern.Create(@"a\").IsMatch(@"xa\y"));

    /// <summary>NonBacktracking 的意义：这种模式在线性引擎下必须立刻返回，而不是回溯爆炸。</summary>
    [Fact]
    public void LongPatternWithManyWildcards_CompletesQuickly()
    {
        var like = MarketLikePattern.Create(new string('*', 200) + "zzz");

        Assert.False(like.IsMatch(new string('a', 5000)));
        Assert.True(like.IsMatch(new string('a', 5000) + "zzz"));
    }
}
