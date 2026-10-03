using GIMI_ModManager.Core.ModStore;

namespace JASM.Tests;

/// <summary>
/// 一键部署的「装到哪个角色」判定。这里锁的是**不做模糊匹配**这条决定：
/// 分类名和本地角色名对不上就该返回 -1（调用方落 Others），而不是猜一个最像的。
/// </summary>
public class ModStoreTargetCharacterTests
{
    private static readonly ModStoreCharacterCandidate[] Candidates =
    [
        new("Jinhsi", "今汐", ["jinhsi"]),
        new("Qingxiao", "青霄", ["qingxiao", "xs"]),
        new("Others", "Others", ["others", "unknown"])
    ];

    [Fact]
    public void MatchesTheInternalName() =>
        Assert.Equal(0, ModStoreTargetCharacter.ResolveIndex("Jinhsi", Candidates));

    [Fact]
    public void MatchingIgnoresCaseAndSurroundingWhitespace() =>
        Assert.Equal(0, ModStoreTargetCharacter.ResolveIndex("  jINHSI ", Candidates));

    [Fact]
    public void MatchesTheDisplayNameWhenTheInternalNameDiffers()
    {
        // 中文语言包下显示名是中文，而 GameBanana 的分类名是英文 —— 这条兜的是
        // 「上游哪天改用显示名」的情况。
        Assert.Equal(1, ModStoreTargetCharacter.ResolveIndex("青霄", Candidates));
    }

    [Fact]
    public void MatchesAnAlias() =>
        Assert.Equal(1, ModStoreTargetCharacter.ResolveIndex("xs", Candidates));

    [Fact]
    public void InternalNameWinsOverAnEarlierAliasMatch()
    {
        // 别名是模糊的（有人把某个角色的别名起成别人的内部名也不奇怪），
        // 所以顺序是内部名 → 显示名 → 别名，不能反过来。
        ModStoreCharacterCandidate[] candidates =
        [
            new("Jinhsi", "今汐", ["qingxiao"]),
            new("Qingxiao", "青霄", [])
        ];

        Assert.Equal(1, ModStoreTargetCharacter.ResolveIndex("Qingxiao", candidates));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("YangyangXuanling")] // 上游真有的合并角色名，与本地任何一个都不相等
    [InlineData("SomeoneNew")] // 本地还没实装的角色
    public void ReturnsMinusOneWhenNothingMatchesExactly(string? name) =>
        Assert.Equal(-1, ModStoreTargetCharacter.ResolveIndex(name, Candidates));

    [Fact]
    public void EmptyCandidateListIsNotAnError() =>
        Assert.Equal(-1, ModStoreTargetCharacter.ResolveIndex("Jinhsi", []));
}