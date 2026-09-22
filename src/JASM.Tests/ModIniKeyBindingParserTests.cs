using GIMI_ModManager.Core.Entities.Mods.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers the structured half of <see cref="ModIniKeyBindingParser"/>: which 3dmigoto key lines produce
/// an entry, and whether that entry can be synthesized back into a real keypress
/// (<see cref="ModIniKeyBindingEntry.KeyCode"/> / <see cref="ModIniKeyBindingEntry.ModifierKeyCodes"/>).
///
/// The samples below are trimmed-down copies of real mod ini files. The most important distinction
/// encoded here is <c>no_ctrl</c> vs <c>ctrl</c>: the <c>no_*</c> prefixes are 3dmigoto "do NOT hold this"
/// conditions, so they must never end up in <c>ModifierKeyCodes</c> — sending Ctrl because the mod said
/// <c>no_ctrl</c> would invert the binding.
/// </summary>
public class ModIniKeyBindingParserTests
{
    [Fact]
    public void ParsesArrowBindingFromModifierLine()
    {
        var entry = Assert.Single(Parse("""
            [KeyHold]
            no_ctrl no_shift VK_RIGHT
            """));

        Assert.Equal("[KeyHold]", entry.SectionName);
        Assert.Equal("→", entry.KeyValue);
        Assert.Equal("长按", entry.ActionLabel);
        Assert.True(entry.IsArrowKey);
        Assert.Equal((ushort)0x27, entry.KeyCode);
        Assert.Empty(entry.ModifierKeyCodes); // no_ctrl / no_shift 是禁止条件，不是要按下的键
        Assert.True(entry.CanSendKey);
    }

    [Fact]
    public void KeepsExplicitModifiersInOrder()
    {
        var entry = Assert.Single(Parse("""
            [KeyToggle]
            ctrl shift H
            """));

        Assert.Equal("Ctrl+Shift+H", entry.KeyValue);
        Assert.Equal((ushort)0x48, entry.KeyCode);
        Assert.Equal(new ushort[] { 0x11, 0x10 }, entry.ModifierKeyCodes);
        Assert.False(entry.IsArrowKey);
    }

    [Fact]
    public void ParsesModifiersFromKeyValueForm()
    {
        // [KeySwap] 里的 key = 是真实 mod 最常见的写法之一
        var entry = Assert.Single(Parse("""
            [KeySwap]
            key = NO_CTRL NO_ALT VK_UP
            """));

        Assert.Equal("↑", entry.KeyValue);
        Assert.Equal("切换", entry.ActionLabel);
        Assert.Equal((ushort)0x26, entry.KeyCode);
        Assert.Empty(entry.ModifierKeyCodes);
    }

    [Fact]
    public void SendsTheAltFromAltArrow()
    {
        // 用户给的例子：点「Alt+↑」= 真按 Alt 和上箭头
        var entry = Assert.Single(Parse("""
            [KeySwapHat]
            key = alt VK_UP
            """));

        Assert.Equal("Alt+↑", entry.KeyValue);
        Assert.Equal((ushort)0x26, entry.KeyCode);
        Assert.Equal(new ushort[] { 0x12 }, entry.ModifierKeyCodes);
        Assert.True(entry.CanSendKey);
    }

    [Fact]
    public void ParsesNumpadBindingInBothForms()
    {
        var entries = Parse("""
            [KeySwapShoes]
            no_ctrl no_shift VK_NUMPAD6

            [KeySwapHair]
            key = NUMPAD7
            """);

        Assert.Equal(2, entries.Count);
        Assert.Equal("小键盘6", entries[0].KeyValue);
        Assert.Equal((ushort)0x66, entries[0].KeyCode);
        Assert.Equal("小键盘7", entries[1].KeyValue);
        Assert.Equal((ushort)0x67, entries[1].KeyCode);
    }

    [Fact]
    public void KeepsDescriptionFromTheCommentAboveTheBinding()
    {
        var entry = Assert.Single(Parse("""
            [KeySwapHair]
            ; 切换头发
            no_ctrl no_shift VK_NUMPAD7
            """));

        Assert.Equal("切换头发", entry.Description);
    }

    [Fact]
    public void ParsesPlainKeyValueOutsideKeySections()
    {
        // 非 [Key*] 段落里的 key = 也是既有行为：ActionLabel 退化为段名本身
        var entry = Assert.Single(Parse("""
            [TextureOverrideBody]
            key = VK_F10
            """));

        Assert.Equal("[TextureOverrideBody]", entry.ActionLabel);
        Assert.Equal("F10", entry.KeyValue);
        Assert.Equal((ushort)0x79, entry.KeyCode);
    }

    // ── 不可发送的三种情况 ──────────────────────────────────────

    [Fact]
    public void MarksMouseBindingsAsUnsendable()
    {
        // 决策：本功能只发键盘，鼠标键徽章显示但灰掉
        var entry = Assert.Single(Parse("""
            [KeySwapTextures]
            no_ctrl no_shift VK_LBUTTON
            """));

        Assert.Equal("鼠标左键", entry.KeyValue);
        Assert.Null(entry.KeyCode);
        Assert.False(entry.CanSendKey);
    }

    [Fact]
    public void MarksUnmappedKeyValueAsUnsendable()
    {
        // ⚠️ key = $swapvar 时 ParseKeyActionLine **不会**返回 null（它把 $swapvar 当成键名走了回退），
        // 所以「不可发送」不能挂在 parsed is null 上，只能看 KeyCode 有没有解出来
        var entry = Assert.Single(Parse("""
            [KeySwap]
            key = $swapvar
            """));

        Assert.Equal("$swapvar", entry.KeyValue);
        Assert.Null(entry.KeyCode);
        Assert.False(entry.CanSendKey);
    }

    [Fact]
    public void MarksModifierOnlyLineAsUnsendable()
    {
        // 整行只有修饰键：没有主键可发，显示回退成原样
        var entry = Assert.Single(Parse("""
            [KeySwap]
            key = ctrl
            """));

        Assert.Equal("ctrl", entry.KeyValue);
        Assert.Null(entry.KeyCode);
        Assert.Empty(entry.ModifierKeyCodes);
        Assert.False(entry.CanSendKey);
    }

    // ── 跳过逻辑回归 ───────────────────────────────────────────

    [Fact]
    public void SkipsNonKeyIniKeys()
    {
        var entries = Parse("""
            [KeySwapTexture]
            condition = $object_detected
            type = ps-t0
            $swapvar = 0
            run = CommandList\Toggle
            back = CommandList\Toggle
            key = VK_F10
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("F10", entry.KeyValue);
    }

    [Fact]
    public void IgnoresBareKeyLineInKeySection()
    {
        // 既有行为：IsModifierKeyLine 要求「含 VK_」或「修饰符 + 短词」，
        // 所以 [Key*] 段里孤零零一行裸 H 今天就被丢弃。这是既有行为，不是回归。
        Assert.Empty(Parse("""
            [KeyToggle]
            H
            """));
    }

    [Fact]
    public void IgnoresLinesBeforeAnySection()
    {
        Assert.Empty(Parse("key = VK_F10"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNothingForBlankInput(string iniContent)
        => Assert.Empty(ModIniKeyBindingParser.ParseKeyBindingsFromText(iniContent));

    [Fact]
    public void HandlesCrlfAndLfIdentically()
    {
        var crlf = Parse("[KeyHold]\r\nno_ctrl no_shift VK_RIGHT\r\n");
        var lf = Parse("[KeyHold]\nno_ctrl no_shift VK_RIGHT\n");

        var a = Assert.Single(crlf);
        var b = Assert.Single(lf);
        Assert.Equal(a.KeyValue, b.KeyValue);
        Assert.Equal(a.KeyCode, b.KeyCode);
    }

    // ── `key =` 行写裸符号键 / 裸显示名（实测 161 个 ini 里的主流写法）──

    [Fact]
    public void ParsesBareSymbolKeyWithModifier()
    {
        // 用户报的「Alt + . 点不了」就是这条：ini 裸写 '.'，而词表里只有 VK_OEM_PERIOD，
        // 于是解析退化成 KeyCode=null、徽章灰掉。
        var entry = Assert.Single(Parse("""
            [KeyArmThing]
            condition = $object_detected
            key = alt .
            type = cycle
            """));

        Assert.Equal("[KeyArmThing]", entry.SectionName);
        Assert.Equal("Alt+.", entry.KeyValue);
        Assert.False(entry.IsArrowKey);
        Assert.Equal((ushort)0xBE, entry.KeyCode);
        Assert.Equal(new ushort[] { 0x12 }, entry.ModifierKeyCodes);
        Assert.True(entry.CanSendKey);
    }

    [Theory]
    [InlineData("key = .", ".", 0xBE)]
    [InlineData("key = /", "/", 0xBF)]
    [InlineData("key = ,", ",", 0xBC)]
    [InlineData("key = ;", ";", 0xBA)]
    [InlineData("key = [", "[", 0xDB)]
    [InlineData("key = ]", "]", 0xDD)]
    [InlineData("key = \\", "\\", 0xDC)]
    [InlineData("key = '", "'", 0xDE)]
    [InlineData("key = Home", "Home", 0x24)]
    [InlineData("key = END", "End", 0x23)]
    public void ParsesBareSymbolAndNamedKeys(string line, string expectedKeyValue, int expectedKeyCode)
    {
        var entry = Assert.Single(Parse($"""
            [KeyThing]
            {line}
            """));

        Assert.Equal(expectedKeyValue, entry.KeyValue);
        Assert.Equal((ushort)expectedKeyCode, entry.KeyCode);
        Assert.Empty(entry.ModifierKeyCodes);
        Assert.True(entry.CanSendKey);
    }

    [Fact]
    public void ParsesNoModifiersPrefixWithoutTreatingItAsAModifier()
    {
        // `no_modifiers` 既不是 ctrl/shift/alt，也不是 no_ctrl/no_shift/no_alt。
        // 它只是「不按修饰键」的说明词，不能进 ModifierKeyCodes（否则会凭空按下修饰键）。
        var entry = Assert.Single(Parse("""
            [KeyBoobsize]
            key = no_modifiers /
            """));

        Assert.Equal("/", entry.KeyValue);
        Assert.Equal((ushort)0xBF, entry.KeyCode);
        Assert.Empty(entry.ModifierKeyCodes);
        Assert.True(entry.CanSendKey);
    }

    [Fact]
    public void ParsesEqualsKeyWhenTheValueItselfContainsAnEqualsSign()
    {
        // `key = no_modifiers =` 是绑 '=' 键。值里含 '='，GetIniValue 只按第一个 '=' 切才不会吞掉它。
        var entry = Assert.Single(Parse("""
            [KeySwitch]
            key = no_modifiers =
            """));

        Assert.Equal("=", entry.KeyValue);
        Assert.Equal((ushort)0xBB, entry.KeyCode);
        Assert.Empty(entry.ModifierKeyCodes);
        Assert.True(entry.CanSendKey);
    }

    [Fact]
    public void MarkedUnsendableWhenTheLineHasOnlyModifiers()
    {
        // `key = ctrl alt` 里没有主键，合成不出一次按键 —— 保持不可发送，但显示照旧。
        var entry = Assert.Single(Parse("""
            [KeyHelp]
            key = ctrl alt
            """));

        Assert.Equal("ctrl alt", entry.KeyValue);
        Assert.Null(entry.KeyCode);
        Assert.False(entry.CanSendKey);
    }

    [Fact]
    public void MarkedUnsendableForMouseButtons()
    {
        // 鼠标键是**有意**不发的：合成点击会打在光标当前位置，风险比发键盘大。
        // 显示与修饰键仍要正确，用户才知道这个 mod 期望的是 Alt+左键。
        var entry = Assert.Single(Parse("""
            [KeyClick]
            key = no_ctrl no_shift alt VK_LBUTTON
            """));

        Assert.Equal("Alt+鼠标左键", entry.KeyValue);
        Assert.Null(entry.KeyCode);
        Assert.Equal(new ushort[] { 0x12 }, entry.ModifierKeyCodes);
        Assert.False(entry.CanSendKey);
    }

    private static List<ModIniKeyBindingEntry> Parse(string iniContent)
        => ModIniKeyBindingParser.ParseKeyBindingsFromText(iniContent);
}