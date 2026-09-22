using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="KeyChordGuard"/> —— 从 <c>GameKeySender</c> 搬进 Core 的危险组合键护栏。
/// 这些用例就是「搬家没搬坏」的证据：规则一旦放宽，先红的一定是这里。
/// 纯逻辑，不碰 win32。
/// </summary>
public class KeyChordGuardTests
{
    private const ushort Alt = 0x12;
    private const ushort Ctrl = 0x11;
    private const ushort Shift = 0x10;
    private const ushort LWin = 0x5B;
    private const ushort RWin = 0x5C;
    private const ushort F4 = 0x73;
    private const ushort Tab = 0x09;
    private const ushort Esc = 0x1B;

    // ── 拦：单发 Win 键（没按 Alt 时）─────────────────────────

    [Theory]
    [InlineData(LWin)]
    [InlineData(RWin)]
    public void BlocksBareWindowsKey(ushort virtualKey)
        => Assert.True(KeyChordGuard.IsBlockedChord(virtualKey, []));

    [Theory]
    [InlineData(Ctrl)]
    [InlineData(Shift)]
    public void BlocksWindowsKeyHeldWithOtherModifiersButNoAlt(ushort modifier)
    {
        // 按着 Ctrl / Shift（没 Alt）时 Win 键照样拦 —— 「单发 Win」指的是「没有 Alt」，不是「只有它一个」
        Assert.True(KeyChordGuard.IsBlockedChord(LWin, [modifier]));
        Assert.True(KeyChordGuard.IsBlockedChord(RWin, [modifier]));
    }

    [Fact]
    public void BlocksWindowsKeyWithBothOtherModifiers()
        => Assert.True(KeyChordGuard.IsBlockedChord(RWin, [Ctrl, Shift]));

    // ── 拦：按着 Alt 时的关窗口 / 抢焦点组合 ─────────────────

    [Theory]
    [InlineData(F4)]   // Alt+F4 关游戏
    [InlineData(Tab)]  // Alt+Tab 切走
    [InlineData(Esc)]  // Alt+Esc 切走
    public void BlocksAltKillAndFocusChords(ushort virtualKey)
        => Assert.True(KeyChordGuard.IsBlockedChord(virtualKey, [Alt]));

    [Fact]
    public void BlocksAltKillChordEvenWithExtraModifiers()
        => Assert.True(KeyChordGuard.IsBlockedChord(F4, [Ctrl, Alt]));

    // ── 放行：正常 mod 绑定 ──────────────────────────────────

    [Theory]
    [InlineData(0xBE, Alt)]    // Alt + .（用户最早报「点不了」的那类符号键）
    [InlineData(0x26, Alt)]    // Alt + ↑
    [InlineData(0xBE, Ctrl)]
    [InlineData(0x43, Ctrl)]   // Ctrl + C
    public void AllowsOrdinaryChords(ushort virtualKey, ushort modifier)
        => Assert.False(KeyChordGuard.IsBlockedChord(virtualKey, [modifier]));

    [Fact]
    public void AllowsOrdinaryMultiModifierChord()
        => Assert.False(KeyChordGuard.IsBlockedChord(0x41, [Ctrl, Shift])); // Ctrl+Shift+A

    [Theory]
    [InlineData(F4)]   // 单按 F4 / Tab / Esc 是普通按键，不能拦
    [InlineData(Tab)]
    [InlineData(Esc)]
    [InlineData(0x41)]
    public void AllowsThoseKeysWithoutAlt(ushort virtualKey)
        => Assert.False(KeyChordGuard.IsBlockedChord(virtualKey, []));

    [Fact]
    public void AllowsAltItselfAsMainKey()
        => Assert.False(KeyChordGuard.IsBlockedChord(Alt, []));

    [Fact]
    public void AllowsRestoreKey()
        => Assert.False(KeyChordGuard.IsBlockedChord(0x52, [Alt])); // Alt+R（mod 里很常见）

    // ── 修饰键白名单 ─────────────────────────────────────────

    [Theory]
    [InlineData(Ctrl)]
    [InlineData(Shift)]
    [InlineData(Alt)]
    public void AcceptsOnlyRealModifiers(ushort virtualKey)
        => Assert.True(KeyChordGuard.IsAllowedModifier(virtualKey));

    [Theory]
    [InlineData(0x41)] // A
    [InlineData(LWin)] // Win 不是本协议的「修饰键」（它单独出现要被拦，但不是 modifier 槽）
    [InlineData(Tab)]
    [InlineData(0x01)] // 鼠标左键
    [InlineData(0x0D)] // Enter
    public void RejectsNonModifiers(ushort virtualKey)
        => Assert.False(KeyChordGuard.IsAllowedModifier(virtualKey));

    [Fact]
    public void AcceptsUpToThreeDistinctModifiers()
    {
        Assert.True(KeyChordGuard.IsAllowedModifiers([]));
        Assert.True(KeyChordGuard.IsAllowedModifiers([Ctrl]));
        Assert.True(KeyChordGuard.IsAllowedModifiers([Ctrl, Shift, Alt]));
    }

    [Fact]
    public void RejectsDuplicateModifiers()
        => Assert.False(KeyChordGuard.IsAllowedModifiers([Ctrl, Ctrl]));

    [Fact]
    public void RejectsMoreThanThreeModifiers()
        => Assert.False(KeyChordGuard.IsAllowedModifiers([Ctrl, Shift, Alt, Ctrl]));

    [Fact]
    public void RejectsModifierListContainingNonModifier()
        => Assert.False(KeyChordGuard.IsAllowedModifiers([Ctrl, 0x41]));
}