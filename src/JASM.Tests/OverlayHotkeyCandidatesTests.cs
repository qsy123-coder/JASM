using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="OverlayHotkeyCandidates"/> — 候选热键的挑选与回退。
///
/// <c>RegisterHotKey</c> 是**独占**的：注册上了这个组合就被系统拦下、不再传给任何程序（包括游戏）。
/// 所以"首选被别人占了"是常态而非意外，必须有确定的回退行为 —— 而失败又不像别的 API 那样会抛异常，
/// 只会返回 false。这段逻辑因此值得用测试钉住。
/// </summary>
public class OverlayHotkeyCandidatesTests
{
    private const ushort VkJ = 0x4A;
    private const ushort VkM = 0x4D;

    private static OverlayHotkeyCandidate CtrlAltJ
        => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, VkJ, "J");

    private static OverlayHotkeyCandidate CtrlAltM
        => new(HotkeyModifiers.Control | HotkeyModifiers.Alt, VkM, "M");

    [Fact]
    public void DefaultsStartWithCtrlAltJBecauseThatIsTheChosenDefault()
    {
        Assert.Equal("Ctrl+Alt+J", OverlayHotkeyCandidates.CreateDefaults()[0].FriendlyName);
    }

    [Fact]
    public void DefaultsAreNotASharedMutableInstance()
    {
        // 拿到的是 IReadOnlyList，但底层若共用同一个数组，一次强转回数组的误改会污染之后所有调用
        Assert.NotSame(OverlayHotkeyCandidates.CreateDefaults(), OverlayHotkeyCandidates.CreateDefaults());
    }

    [Fact]
    public void FormatsModifiersInAFixedOrderRegardlessOfBitOrder()
    {
        // Alt 的位(0x1)比 Ctrl(0x2)低 —— 若按位序拼会得到 "Alt+Ctrl+J"，所以这条测试真的在钉顺序
        Assert.Equal("Ctrl+Alt+J",
            OverlayHotkeyCandidates.Describe(HotkeyModifiers.Control | HotkeyModifiers.Alt, "J"));

        Assert.Equal("Ctrl+Alt+Shift+Win+F9",
            OverlayHotkeyCandidates.Describe(
                HotkeyModifiers.Win | HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Control, "F9"));
    }

    [Fact]
    public void FormatsAKeyWithoutModifiersAsJustTheKey()
        => Assert.Equal("F9", OverlayHotkeyCandidates.Describe(HotkeyModifiers.None, "F9"));

    [Fact]
    public void DefaultCandidatesAllCarryAtLeastOneModifier()
    {
        // 单键（F8 之类）太容易和游戏或输入法抢 —— 候选表里不该出现
        Assert.All(OverlayHotkeyCandidates.CreateDefaults(),
            c => Assert.NotEqual(HotkeyModifiers.None, c.Modifiers));
    }

    [Theory]
    [InlineData(HotkeyModifiers.None)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win)]
    public void AlwaysSetsNoRepeatSoHoldingTheKeyDoesNotFlickerTheOverlay(HotkeyModifiers modifiers)
    {
        var flags = OverlayHotkeyCandidates.ToRegisterHotKeyFlags(modifiers);

        // 漏了 MOD_NOREPEAT 不会报错，只会在用户按住热键时表现为浮窗疯狂闪 —— 所以必须钉住
        Assert.Equal(OverlayHotkeyCandidates.NoRepeatFlag, flags & OverlayHotkeyCandidates.NoRepeatFlag);

        // 且只加了这一个位，原来给的修饰键原样保留
        Assert.Equal((uint)modifiers, flags & ~OverlayHotkeyCandidates.NoRepeatFlag);
    }

    [Fact]
    public void PicksTheFirstCandidateThatRegistersAndStopsTrying()
    {
        var candidates = new[] { CtrlAltJ, CtrlAltM, new(HotkeyModifiers.Control, 0x4B, "K") };
        var tried = new List<string>();

        var selection = OverlayHotkeyCandidates.TrySelect(candidates, candidate =>
        {
            tried.Add(candidate.FriendlyName);
            return candidate.FriendlyName == "Ctrl+Alt+M"
                ? HotkeyRegistrationAttempt.Ok
                : HotkeyRegistrationAttempt.Failed("热键已注册");
        });

        Assert.True(selection.IsRegistered);
        Assert.Equal("Ctrl+Alt+M", selection.Candidate!.FriendlyName);
        Assert.Equal(new[] { "Ctrl+Alt+J", "Ctrl+Alt+M" }, tried); // 拿到可用的就停手，不往下试
        Assert.Single(selection.Failures);
        Assert.True(selection.IsFallback);
    }

    [Fact]
    public void UsesTheFirstCandidateWithoutReportingAFallbackWhenItWorks()
    {
        var selection = OverlayHotkeyCandidates.TrySelect([CtrlAltJ], _ => HotkeyRegistrationAttempt.Ok);

        Assert.Equal("Ctrl+Alt+J", selection.Candidate!.FriendlyName);
        Assert.False(selection.IsFallback);
        Assert.Empty(selection.Failures);
    }

    [Fact]
    public void ReportsNothingSelectedWhenEveryCandidateIsTaken()
    {
        var selection = OverlayHotkeyCandidates.TrySelect(
            [CtrlAltJ, CtrlAltM],
            _ => HotkeyRegistrationAttempt.Failed("热键已注册"));

        Assert.False(selection.IsRegistered);
        Assert.Null(selection.Candidate);
        Assert.False(selection.IsFallback);
        Assert.Equal(2, selection.Failures.Count);
    }

    [Fact]
    public void ReportsNothingSelectedWhenThereAreNoCandidatesAtAll()
    {
        var selection = OverlayHotkeyCandidates.TrySelect([], _ => HotkeyRegistrationAttempt.Ok);

        Assert.False(selection.IsRegistered);
        Assert.Empty(selection.Failures);
    }

    [Fact]
    public void SubstitutesAReasonWhenTheSystemGivesNone()
    {
        var selection = OverlayHotkeyCandidates.TrySelect(
            [CtrlAltJ],
            _ => HotkeyRegistrationAttempt.Failed("   "));

        Assert.Equal("系统没有给出原因", Assert.Single(selection.Failures).Reason);
    }

    [Fact]
    public void DescribeFailureListsEveryCandidateWithItsOwnReason()
    {
        var selection = OverlayHotkeyCandidates.TrySelect(
            [CtrlAltJ, CtrlAltM],
            candidate => HotkeyRegistrationAttempt.Failed(
                candidate.FriendlyName == "Ctrl+Alt+J" ? "已被别的程序占用" : "权限不足"));

        var text = OverlayHotkeyCandidates.DescribeFailure(selection.Failures);

        // 用户需要知道"是哪几个键不行、各自为什么"，才能自己换一组
        Assert.Contains("Ctrl+Alt+J", text);
        Assert.Contains("已被别的程序占用", text);
        Assert.Contains("Ctrl+Alt+M", text);
        Assert.Contains("权限不足", text);
    }

    [Fact]
    public void DescribeFailureSaysSoWhenThereWasNothingToTry()
        => Assert.Equal("没有可用的候选热键。", OverlayHotkeyCandidates.DescribeFailure([]));
}