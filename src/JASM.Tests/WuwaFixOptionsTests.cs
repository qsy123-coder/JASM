using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// 五个开关的互斥与参数映射。互斥写错时的现象是「两个都传、规则互相覆盖」，
/// 用户只会看到修完还是花屏，很难归因，所以在这一层钉死。
/// </summary>
public class WuwaFixOptionsTests
{
    [Fact]
    public void DefaultsAreAllOff()
    {
        var options = new WuwaFixOptions();

        Assert.False(options.DerivedHashes);
        Assert.False(options.StableTexture);
        Assert.False(options.Rendering33);
        Assert.False(options.AemeathMech);
        Assert.Equal(0, options.AeroFixMode);
        Assert.Empty(options.ToArguments());
    }

    [Fact]
    public void DerivedAndStableAreMutuallyExclusive()
    {
        var normalized = new WuwaFixOptions(DerivedHashes: true, StableTexture: true).Normalize();

        Assert.False(normalized.DerivedHashes);
        Assert.True(normalized.StableTexture);
        Assert.Equal(["--stable-texture"], normalized.ToArguments());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(9, 2)]
    public void AeroModeIsClamped(int input, int expected)
    {
        Assert.Equal(expected, new WuwaFixOptions(AeroFixMode: input).Normalize().AeroFixMode);
    }

    [Fact]
    public void AeroModeZeroProducesNoArgument()
    {
        Assert.DoesNotContain("--aero-fix", new WuwaFixOptions(AeroFixMode: 0).ToArguments());
    }

    [Fact]
    public void FullOptionsMapToExpectedArgumentsInOrder()
    {
        var options = new WuwaFixOptions(
            DerivedHashes: true,
            StableTexture: false,
            Rendering33: true,
            AemeathMech: true,
            AeroFixMode: 2);

        Assert.Equal(
            ["--derived-hashes", "--rendering-33", "--aemeath-mech", "--aero-fix", "2"],
            options.ToArguments());
    }

    [Fact]
    public void StableTextureWinsOverDerivedWithoutDuplicateFlags()
    {
        var arguments = new WuwaFixOptions(DerivedHashes: true, StableTexture: true).ToArguments();

        Assert.Contains("--stable-texture", arguments);
        Assert.DoesNotContain("--derived-hashes", arguments);
    }
}