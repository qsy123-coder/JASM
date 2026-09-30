using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.GamesService.Models;
using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="CharacterNameMatcher"/> —— 「这个包是哪个角色的」。
///
/// <para>
/// 用例里的包名**逐字抄自真实的 Mod SFX 文件名**（开发者机器桌面上那批，见 PRD「实测数据」），
/// 不是为了测试编出来的字符串 —— 中文社区的分发命名习惯（<c>角色名-标题（备注）by 作者</c>）就是这个样子，
/// 而这条链路判错一次，Mod 就落到别的角色文件夹里去了。
/// </para>
///
/// <para>
/// 角色数据也照着**运行时**（本地化之后）的样子造：中文名不在 <c>characters.json</c> 里，而是
/// <c>Languages/zh-cn/characters.json</c> 在加载时把 <c>DisplayName</c> 覆盖成中文、并把中文并进 <c>Keys</c>
/// （实测 WuWa 的 Aemeath 最终是 <c>Keys = ["aemeath", "爱弥斯"]</c>、<c>DisplayName = "爱弥斯"</c>）。
/// 所以这里同时钉住两条路：靠 <c>Keys</c> 里的中文命中（爱弥斯），和只靠 <c>DisplayName</c> 命中（清宵）。
/// </para>
/// </summary>
public class CharacterNameMatcherTests
{
    /// <summary>三个伪角色的内部名（真实来源是 <c>IGameService.OtherCharacterInternalName</c> 等）。</summary>
    private static readonly string[] PseudoCharacters = { "Others", "Gliders", "Weapons" };

    [Theory]
    [InlineData("爱弥斯-誓约（0）by 晨星.exe", "Aemeath")]
    [InlineData("千咲-大凤兔女郎(【】切换）.exe", "Chisa")]
    [InlineData("清宵-和服.exe", "Qingxiao")]
    [InlineData("莫宁-姓感老师full(F5)by Arelewd.exe", "Mornye")]
    [InlineData("椿-和服.exe", "Camellya")] // 单字别名，只有整词命中才认
    public void ARealModPackageNameResolvesToItsCharacter(string fileName, string expectedInternalName)
    {
        var ranked = CharacterNameMatcher.Rank(Roster(), new[] { fileName }, PseudoCharacters);

        Assert.Equal(expectedInternalName, TopOf(ranked), ignoreCase: true);
        Assert.True(CharacterNameMatcher.IsConfident(ranked));
    }

    [Theory]
    [InlineData("RabbitFX反虚化+发光前置v74（内附使用说明）.exe")]
    [InlineData("科考卡车-爱 琳 莫 nsfw痛车（内附使用说明）.exe")] // 单字词「爱」「琳」「莫」不许命中「莫宁」「琳奈」
    [InlineData("洛瑟菈-兔女郎v1.1（4~90【，切换）Invalid.exe")]
    public void AModPackageAboutNobodyResolvesToNobody(string fileName)
    {
        // 认不出来是**正常结果**（交给候选框），认错才是事故 —— 所以「不该命中的不许命中」要单独钉
        var ranked = CharacterNameMatcher.Rank(Roster(), new[] { fileName }, PseudoCharacters);

        Assert.Empty(ranked);
    }

    [Theory]
    [InlineData("Xuanling_Pyroath", "YangyangXuanling")] // 包内顶层目录名（下划线分隔）
    [InlineData("Aemeath", "Aemeath")] // 已经是内部名
    [InlineData("aemeath", "Aemeath")] // 大小写不敏感
    [InlineData("玄翎", "YangyangXuanling")] // Keys 里的中文，2 个字
    public void AModFolderNameResolvesToItsCharacter(string folderName, string expectedInternalName)
    {
        var ranked = CharacterNameMatcher.Rank(Roster(), new[] { folderName }, PseudoCharacters);

        Assert.Equal(expectedInternalName, TopOf(ranked), ignoreCase: true);
        Assert.True(CharacterNameMatcher.IsConfident(ranked));
    }

    [Fact]
    public void TheSameCharacterIsReportedOnceEvenWhenMatchedBySeveralClues()
    {
        // 文件名与包内目录名常常都命中同一个角色，结果里只能有一条
        var ranked = CharacterNameMatcher.Rank(Roster(),
            new[] { "爱弥斯-誓约（0）by 晨星", "Aemeath" }, PseudoCharacters);

        var match = Assert.Single(ranked);
        Assert.Equal("Aemeath", match.InternalName, ignoreCase: true);
        Assert.Equal(CharacterNameMatcher.ExactAliasScore, match.Score);
    }

    [Fact]
    public void PseudoCharactersAreExcludedBecauseTheirKeysAreGenericWords()
    {
        // 「Weapons」这个伪角色的 Keys 是 weapon/sword/bow 这种通用词（中文名干脆就叫「武器」），
        // 谁带这些词谁就被认成它 —— 所以调用方必须排掉这三个，这条测试就是钉这个必要性
        var clues = new[] { "Mornye sword skin.exe", "武器皮肤" };

        var withPseudo = CharacterNameMatcher.Rank(Roster(), clues);
        Assert.Contains(withPseudo, match => IsNamed(match, "Weapons"));

        var withoutPseudo = CharacterNameMatcher.Rank(Roster(), clues, PseudoCharacters);
        Assert.DoesNotContain(withoutPseudo, match => IsNamed(match, "Weapons"));
        Assert.Equal("Mornye", TopOf(withoutPseudo), ignoreCase: true);
        Assert.True(CharacterNameMatcher.IsConfident(withoutPseudo));
    }

    [Fact]
    public void TwoEquallyLikelyCandidatesAreNotConfident()
    {
        // 同分（或分差不足）就得弹候选框让人选，不能自动落地
        var characters = new ICharacter[]
        {
            MakeCharacter("Foo", "福", "幽灵"),
            MakeCharacter("Bar", "巴", "幽灵")
        };

        var ranked = CharacterNameMatcher.Rank(characters, new[] { "幽灵-兔女郎.exe" });

        Assert.Equal(2, ranked.Count);
        Assert.False(CharacterNameMatcher.IsConfident(ranked));
    }

    [Fact]
    public void AStrongWinnerWithEnoughOfAMarginIsConfident()
    {
        // 100 分（整词命中）领先 85 分（词里含别名）刚好 15 分 —— 这个余量是阈值，不能差一个数就跑偏
        var characters = new ICharacter[]
        {
            MakeCharacter("Xuanling", "玄翎", "xuanling"),
            MakeCharacter("Pyroath", "派罗", "Pyroath")
        };

        var ranked = CharacterNameMatcher.Rank(characters, new[] { "Xuanling_PyroathWing" });

        Assert.Equal("Xuanling", TopOf(ranked), ignoreCase: true);
        Assert.Equal(CharacterNameMatcher.ExactAliasScore, ranked[0].Score);
        Assert.Equal(CharacterNameMatcher.AliasInsideTokenScore, ranked[1].Score);
        Assert.True(CharacterNameMatcher.IsConfident(ranked));
    }

    [Fact]
    public void ShortAsciiAliasesOnlyMatchWholeWords()
    {
        // Genshin 的 Hu Tao 有个两字母别名「hu」：整词「hu」该认，
        // 但要是让它参与包含匹配，「Hutao」里那段 hu 也会命中（而这是 ASCII 别名的长度守卫要挡的）。
        // 这里用一个「只有 hu 这一个别名」的角色，免得内部名/ModFilesName 把结果盖掉
        var characters = new ICharacter[] { MakeCharacter("HuTaoAlt", "胡桃Alt", "hu") };

        Assert.Equal("HuTaoAlt", TopOf(CharacterNameMatcher.Rank(characters, new[] { "hu-x.exe" })), ignoreCase: true);

        var ranked = CharacterNameMatcher.Rank(characters, new[] { "Hutao" });
        Assert.Empty(ranked);
    }

    [Fact]
    public void TwoCharacterChineseAliasesDoMatchInsideLongerWords()
    {
        // 中文两字名（数据里绝大多数）没有「字母子串」问题，在长词里出现就该认
        var characters = new ICharacter[] { MakeCharacter("Changli", "长离", "changli") };

        var ranked = CharacterNameMatcher.Rank(characters, new[] { "长离歌姬皮肤.exe" });

        Assert.Equal("Changli", TopOf(ranked), ignoreCase: true);
        Assert.Equal(CharacterNameMatcher.AliasInsideTokenScore, ranked[0].Score);
    }

    [Fact]
    public void ASingleCharacterChineseAliasNeverMatchesInsideLongerWords()
    {
        // 「椿」这类单字别名只认整词：否则「香椿」这种词也会命中
        var characters = new ICharacter[] { MakeCharacter("Camellya", "椿", "camellya") };

        Assert.Empty(CharacterNameMatcher.Rank(characters, new[] { "香椿炒蛋.exe" }));
    }

    [Fact]
    public void APartOfAMultiWordAliasIsNotAMatch()
    {
        // 「泱泱・玄翎」这种带分隔符的别名：只拿到其中一个词（玄翎）不算命中 ——
        // 词里含别名那条规则只认「整条别名被一个词包住」，词里不可能含分隔符，所以它在这条规则上天然不生效。
        // 这类角色得靠它别的别名，数据里正好给了 xuanling/玄翎
        var multiWordOnly = new ICharacter[] { MakeCharacter("YangyangXuanling", "泱泱・玄翎") };

        Assert.Empty(CharacterNameMatcher.Rank(multiWordOnly, new[] { "玄翎" }));
    }

    [Fact]
    public void ANameWrittenWithDifferentSeparatorsStillMatches()
    {
        // 同一个名字，数据里是 HuTao、包内目录里写成 Hu Tao / 泱泱・玄翎 写成 泱泱玄翎 —— 都得认。
        // 这条靠的是「整条线索去掉分隔符后与别名相等」，不是切词
        var characters = new ICharacter[] { MakeCharacter("HuTao", "Hu Tao", "hutao") };

        Assert.Equal("HuTao", TopOf(CharacterNameMatcher.Rank(characters, new[] { "Hu Tao" })), ignoreCase: true);
        Assert.Equal("YangyangXuanling", TopOf(CharacterNameMatcher.Rank(
            new ICharacter[] { MakeCharacter("YangyangXuanling", "泱泱・玄翎") }, new[] { "泱泱玄翎" })), ignoreCase: true);
    }

    [Fact]
    public void GarbageInputNeverThrows()
    {
        // 这条链路的输入来自用户拖进来的文件名，什么都有；认不出就返回空，不许抛
        Assert.Empty(CharacterNameMatcher.Rank(null, new[] { "爱弥斯.exe" }));
        Assert.Empty(CharacterNameMatcher.Rank(Roster(), null));
        Assert.Empty(CharacterNameMatcher.Rank(Roster(), new[] { null, "", "   ", "---", "（）【】" }));
        Assert.Empty(CharacterNameMatcher.Rank(new ICharacter[] { MakeCharacter("NoKeys", "无别名") },
            new[] { "   " }));

        // 别名来源被置空（数据异常 / 自定义角色）也不能炸
        var broken = new Character("Broken", null!) { ModFilesName = null!, Keys = null! };
        Assert.Empty(CharacterNameMatcher.Rank(new ICharacter[] { broken }, new[] { "坏的.exe" }));
    }

    [Fact]
    public void AnEmptyRankingIsNotConfident()
    {
        Assert.False(CharacterNameMatcher.IsConfident(null));
        Assert.False(CharacterNameMatcher.IsConfident(Array.Empty<CharacterNameMatch>()));
    }

    [Fact]
    public void ALowScoreAloneIsNotEnoughToBeConfident()
    {
        // 只有一条 85 分（词里含别名）也算认出来了 —— 真实包名「千咲大凤兔女郎」就是这种
        var ranked = CharacterNameMatcher.Rank(Roster(), new[] { "千咲大凤兔女郎.exe" }, PseudoCharacters);

        Assert.Equal("Chisa", TopOf(ranked), ignoreCase: true);
        Assert.True(CharacterNameMatcher.IsConfident(ranked));
    }

    /// <summary>
    /// 测试用的角色名单。名字与别名照着运行时（本地化之后）的真实数据造，
    /// 覆盖四种别名来源：<c>Keys</c> 里的中文、只有 <c>DisplayName</c> 有中文、单字中文、两字母 ASCII。
    /// </summary>
    private static List<ICharacter> Roster() => new()
    {
        MakeCharacter("Aemeath", "爱弥斯", "aemeath", "爱弥斯"),
        MakeCharacter("Chisa", "千咲", "chisa", "千咲"),
        MakeCharacter("Qingxiao", "清宵", "qingxiao"), // 中文名只在 DisplayName 上
        MakeCharacter("Mornye", "莫宁", "mornye"),
        MakeCharacter("Camellya", "椿", "camellya"),
        MakeCharacter("YangyangXuanling", "泱泱・玄翎", "yangyangxuanling", "yangyang", "xuanling", "玄翎"),
        MakeCharacter("Sanhua", "散华", "sanhua"),
        // 伪角色：Keys 是通用词，中文名是「武器」这种两字词
        MakeCharacter("Others", "其他角色", "others", "unknown"),
        MakeCharacter("Weapons", "武器", "weapon", "claymore", "sword", "polearm", "catalyst", "bow")
    };

    /// <summary>造一个角色；<c>ModFilesName</c> 跟随内部名（真实数据里绝大多数就是这样）。</summary>
    private static Character MakeCharacter(string internalName, string displayName, params string[] keys) =>
        new(internalName, displayName) { ModFilesName = internalName, Keys = keys };

    /// <summary>
    /// 断言一律用 <c>ignoreCase</c>：<c>InternalName.Id</c> 在构造时就被转成小写了
    /// （见 <c>InternalName</c> 的构造函数），测试里照真实数据写名字更好读。
    /// </summary>
    private static string TopOf(IReadOnlyList<CharacterNameMatch> ranked) =>
        ranked.Count == 0 ? string.Empty : ranked[0].InternalName;

    private static bool IsNamed(CharacterNameMatch match, string internalName) =>
        string.Equals(match.InternalName, internalName, StringComparison.OrdinalIgnoreCase);
}