using GIMI_ModManager.Core.Entities.Mods.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="VirtualKeyMap"/>, which replaced the old display-only MapKeyName switch.
///
/// The display half of this table is a <b>regression suite</b>: every literal below was copied verbatim
/// from the old ModIniKeyBindingParser.MapKeyName switch, and the arrow-key badges / hint panel render
/// these strings directly. If one of these fails, the UI text changed — that is a bug, not a test to fix.
/// The virtual-key half is what "click the badge to send the key to the game" relies on.
/// </summary>
public class VirtualKeyMapTests
{
    // ── 显示回归：逐字照搬旧 MapKeyName 的字面量 ─────────────────

    [Theory]
    [InlineData("VK_RIGHT", "→", true)]
    [InlineData("RIGHT", "→", true)]
    [InlineData("VK_LEFT", "←", true)]
    [InlineData("VK_UP", "↑", true)]
    [InlineData("VK_DOWN", "↓", true)]
    [InlineData("VK_LBUTTON", "鼠标左键", false)]
    [InlineData("VK_RBUTTON", "鼠标右键", false)]
    [InlineData("VK_MBUTTON", "鼠标中键", false)]
    [InlineData("VK_F10", "F10", false)]
    [InlineData("F1", "F1", false)]
    [InlineData("VK_SPACE", "空格", false)]
    [InlineData("SPACE", "空格", false)]
    [InlineData("VK_RETURN", "回车", false)]
    [InlineData("ENTER", "回车", false)]
    [InlineData("VK_TAB", "Tab", false)]
    [InlineData("VK_ESCAPE", "Esc", false)]
    [InlineData("ESC", "Esc", false)]
    [InlineData("VK_BACK", "退格", false)]
    [InlineData("BACKSPACE", "退格", false)]
    [InlineData("VK_DELETE", "Delete", false)]
    [InlineData("VK_HOME", "Home", false)]
    [InlineData("VK_END", "End", false)]
    [InlineData("VK_PRIOR", "PgUp", false)]
    [InlineData("PAGEDOWN", "PgDn", false)]
    [InlineData("VK_NUMPAD6", "小键盘6", false)]
    [InlineData("NUMPAD0", "小键盘0", false)]
    [InlineData("VK_5", "5", false)]
    [InlineData("H", "H", false)]
    [InlineData("VK_OEM_PERIOD", ".", false)]
    [InlineData("OEM_COMMA", ",", false)]
    [InlineData("VK_OEM_MINUS", "-", false)]
    [InlineData("VK_OEM_PLUS", "=", false)]
    [InlineData("VK_OEM_1", ";", false)]
    [InlineData("VK_OEM_2", "/", false)]
    [InlineData("VK_OEM_3", "`", false)]
    [InlineData("VK_OEM_4", "[", false)]
    [InlineData("VK_OEM_5", "\\", false)]
    [InlineData("VK_OEM_6", "]", false)]
    [InlineData("VK_OEM_7", "'", false)]
    [InlineData("NONE", "(无)", false)]
    [InlineData("DISABLED", "(无)", false)]
    public void Resolve_KeepsTheOriginalDisplayText(string keyName, string expectedDisplay, bool expectedIsArrow)
    {
        var definition = VirtualKeyMap.Resolve(keyName);

        Assert.Equal(expectedDisplay, definition.Display);
        Assert.Equal(expectedIsArrow, definition.IsArrow);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("end")]
    [InlineData("OEM_1")]
    [InlineData("Blorp")]
    public void Resolve_DoesNotGrowTheBareAliasSet(string keyName)
    {
        // 旧表只给 VK_HOME / VK_END / VK_OEM_1..7 注册了带前缀的形式；裸别名必须继续原样回退。
        // 这条最容易在加别名时被写坏。
        // ⚠️ 别把 "oem_period" 这类**已注册别名的小写**写进来：字典比较器是 OrdinalIgnoreCase，
        // 它命中 "OEM_PERIOD" 是正确行为（旧 MapKeyName 也是先 ToUpperInvariant 再 switch）。
        var definition = VirtualKeyMap.Resolve(keyName);

        Assert.Equal(keyName, definition.Display);
        Assert.Null(definition.VirtualKeyCode);
        Assert.False(definition.CanSend);
    }

    [Fact]
    public void Resolve_MatchesRegisteredAliasesCaseInsensitively()
    {
        Assert.Equal(".", VirtualKeyMap.Resolve("vK_oem_period").Display);
        Assert.Equal(".", VirtualKeyMap.Resolve("oem_period").Display);
        Assert.Equal((ushort)0xBE, VirtualKeyMap.Resolve("oem_period").VirtualKeyCode);
    }

    [Fact]
    public void Resolve_FallsBackToTheUntrimmedOriginal()
    {
        // 旧 MapKeyName 的兜底分支返回的是原始入参（未 trim），这里保持
        Assert.Equal(" xyz ", VirtualKeyMap.Resolve(" xyz ").Display);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")] // 全空白**不算**空输入：trim 后查不到 → 原样回退，与旧 MapKeyName 一致
    public void Resolve_NeverSendsBlankInput(string? keyName, string expectedDisplay)
    {
        var definition = VirtualKeyMap.Resolve(keyName);

        Assert.Equal(expectedDisplay, definition.Display);
        Assert.Null(definition.VirtualKeyCode);
        Assert.False(definition.CanSend);
    }

    // ── 虚拟键码 ────────────────────────────────────────────────

    [Theory]
    [InlineData("VK_RIGHT", 0x27)]
    [InlineData("VK_LEFT", 0x25)]
    [InlineData("VK_UP", 0x26)]
    [InlineData("VK_DOWN", 0x28)]
    [InlineData("UP", 0x26)]
    [InlineData("VK_SPACE", 0x20)]
    [InlineData("VK_RETURN", 0x0D)]
    [InlineData("VK_TAB", 0x09)]
    [InlineData("VK_ESCAPE", 0x1B)]
    [InlineData("VK_BACK", 0x08)]
    [InlineData("VK_DELETE", 0x2E)]
    [InlineData("VK_HOME", 0x24)]
    [InlineData("VK_END", 0x23)]
    [InlineData("VK_PRIOR", 0x21)]
    [InlineData("VK_NEXT", 0x22)]
    [InlineData("VK_INSERT", 0x2D)]
    [InlineData("VK_F1", 0x70)]
    [InlineData("VK_F10", 0x79)]
    [InlineData("VK_F12", 0x7B)]
    [InlineData("VK_NUMPAD0", 0x60)]
    [InlineData("VK_NUMPAD6", 0x66)]
    [InlineData("VK_NUMPAD9", 0x69)]
    [InlineData("VK_A", 0x41)]
    [InlineData("A", 0x41)]
    [InlineData("VK_Z", 0x5A)]
    [InlineData("VK_0", 0x30)]
    [InlineData("VK_9", 0x39)]
    [InlineData("VK_OEM_PERIOD", 0xBE)]
    [InlineData("VK_OEM_COMMA", 0xBC)]
    [InlineData("VK_OEM_MINUS", 0xBD)]
    [InlineData("VK_OEM_PLUS", 0xBB)]
    [InlineData("VK_OEM_1", 0xBA)]
    [InlineData("VK_OEM_2", 0xBF)]
    [InlineData("VK_OEM_3", 0xC0)]
    [InlineData("VK_OEM_4", 0xDB)]
    [InlineData("VK_OEM_5", 0xDC)]
    [InlineData("VK_OEM_6", 0xDD)]
    [InlineData("VK_OEM_7", 0xDE)]
    public void Resolve_MapsVirtualKeyCodes(string keyName, int expectedVirtualKeyCode)
    {
        var definition = VirtualKeyMap.Resolve(keyName);

        Assert.Equal((ushort)expectedVirtualKeyCode, definition.VirtualKeyCode);
        Assert.True(definition.CanSend);
    }

    [Theory]
    [InlineData("vK_f1")]
    [InlineData("vk_F1")]
    [InlineData("f1")]
    public void Resolve_IsCaseInsensitive(string keyName)
    {
        var definition = VirtualKeyMap.Resolve(keyName);

        Assert.Equal("F1", definition.Display);
        Assert.Equal((ushort)0x70, definition.VirtualKeyCode);
    }

    // ── 不可发送 ────────────────────────────────────────────────

    [Theory]
    [InlineData("VK_LBUTTON")] // 鼠标键：本功能只发键盘
    [InlineData("VK_RBUTTON")]
    [InlineData("VK_MBUTTON")]
    [InlineData("NONE")]       // 「无按键」
    [InlineData("DISABLED")]
    [InlineData("$swapvar")]   // 不是按键名
    [InlineData("VK_LAUNCH_APP1")] // 词表里没有
    public void Resolve_MarksNonKeyboardKeysAsUnsendable(string keyName)
    {
        var definition = VirtualKeyMap.Resolve(keyName);

        Assert.Null(definition.VirtualKeyCode);
        Assert.False(definition.CanSend);
    }

    // ── 修饰键 ─────────────────────────────────────────────────

    [Theory]
    [InlineData("ctrl", "Ctrl", 0x11)]
    [InlineData("CTRL", "Ctrl", 0x11)]
    [InlineData("shift", "Shift", 0x10)]
    [InlineData("alt", "Alt", 0x12)]
    public void TryResolveModifier_ResolvesCtrlShiftAlt(string token, string expectedDisplay, int expectedVirtualKeyCode)
    {
        Assert.True(VirtualKeyMap.TryResolveModifier(token, out var definition));
        Assert.Equal(expectedDisplay, definition.Display);
        Assert.Equal((ushort)expectedVirtualKeyCode, definition.VirtualKeyCode);
    }

    [Theory]
    [InlineData("no_ctrl")] // 「仅当没按下时触发」的禁止条件，不是要按下的键
    [InlineData("no_shift")]
    [InlineData("NO_ALT")]
    [InlineData("VK_UP")]
    [InlineData("")]
    [InlineData(null)]
    public void TryResolveModifier_RejectsNonModifierTokens(string? token)
        => Assert.False(VirtualKeyMap.TryResolveModifier(token, out _));

    [Fact]
    public void Resolve_LeavesBareCtrlUnmapped()
    {
        // 旧 MapKeyName 里 "CTRL" 走的是原样回退；修饰键不能进主表，否则显示会悄悄变成 "Ctrl"
        var definition = VirtualKeyMap.Resolve("CTRL");

        Assert.Equal("CTRL", definition.Display);
        Assert.Null(definition.VirtualKeyCode);
    }
}