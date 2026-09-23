using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="KeyHelperProtocol"/> —— 主程序与提权助手之间的行协议。
/// 重点在两件事：① 构造→解析能原样往返（修饰键顺序不能乱，顺序决定按键的按下顺序）；
/// ② **任何不合法的载荷都必须被拒**，因为这条管道的另一端是个提权进程，它只该收到数字。
/// 纯逻辑，不碰管道也不碰 win32。
/// </summary>
public class KeyHelperProtocolTests
{
    private const ushort Alt = 0x12;
    private const ushort Ctrl = 0x11;
    private const ushort Shift = 0x10;

    // ── 往返 ────────────────────────────────────────────────

    [Theory]
    [InlineData((ushort)0x41, 0x1234, new ushort[] { })]
    [InlineData((ushort)0xBE, 0x00000000_00A1B2C3, new[] { Alt })]           // Alt + .
    [InlineData((ushort)0x26, 0x00000000_00F0F0F0, new[] { Alt, Ctrl })]     // Alt + Ctrl + ↑
    [InlineData((ushort)0x52, 0x00000000_7FFFFFFF, new[] { Ctrl, Shift, Alt })]
    public void RoundTripsEveryField(ushort virtualKey, long window, ushort[] modifiers)
    {
        var payload = KeyHelperProtocol.BuildSendKeyPayload(virtualKey, modifiers, (nint)window);

        Assert.Equal(KeyHelperProtocol.SendKeyPayloadLineCount, payload.Length);
        Assert.True(KeyHelperProtocol.TryParseSendKeyRequest(payload[0], payload[1], payload[2],
            out var request, out var failureReason), failureReason);

        Assert.Equal(virtualKey, request.VirtualKey);
        Assert.Equal(modifiers, request.ModifierKeyCodes); // 顺序必须原样保留
        Assert.Equal(window, (long)request.TargetWindow);
    }

    [Fact]
    public void PreservesModifierPressOrder()
    {
        // 按下顺序 = 「先修饰键后主键」，抬起反过来；修饰键内部顺序也得照 ini 来
        var payload = KeyHelperProtocol.BuildSendKeyPayload(0x43, [Shift, Ctrl], (nint)0x100);

        Assert.True(KeyHelperProtocol.TryParseSendKeyRequest(payload[0], payload[1], payload[2],
            out var request, out _));
        Assert.Equal(new ushort[] { Shift, Ctrl }, request.ModifierKeyCodes);
    }

    // ── 命令识别 ────────────────────────────────────────────

    [Theory]
    [InlineData("SENDKEY", KeyHelperCommand.SendKey)]
    [InlineData("sendkey", KeyHelperCommand.SendKey)]
    [InlineData("SENDKEY\r", KeyHelperCommand.SendKey)] // 对面用 CRLF 也认
    [InlineData(" PING ", KeyHelperCommand.Ping)]
    [InlineData("exit", KeyHelperCommand.Exit)]
    [InlineData("FOO", KeyHelperCommand.Unknown)]
    [InlineData("", KeyHelperCommand.Unknown)]
    [InlineData(null, KeyHelperCommand.Unknown)]
    public void ParsesCommands(string? line, KeyHelperCommand expected)
        => Assert.Equal(expected, KeyHelperProtocol.ParseCommand(line));

    // ── 拒：主键 ────────────────────────────────────────────

    [Theory]
    [InlineData("0")]      // 0x00 不是可发的 VK
    [InlineData("255")]    // 0xFF 同上
    [InlineData("256")]    // VK 只有一字节，> 0xFF 一律不是合法键码
    [InlineData("-1")]
    [InlineData("+65")]
    [InlineData(" 65")]    // NumberStyles.None 不接受空白
    [InlineData("65 ")]
    [InlineData("0x41")]   // 只收十进制
    [InlineData("A")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsBadVirtualKey(string? virtualKeyLine)
    {
        Assert.False(KeyHelperProtocol.TryParseSendKeyRequest(virtualKeyLine, "", "10",
            out _, out var failureReason));
        Assert.Equal(KeyHelperProtocol.ReasonBadPayload, failureReason);
    }

    // ── 拒：修饰键 ──────────────────────────────────────────

    [Theory]
    [InlineData("65")]        // 0x41 = A，不是修饰键
    [InlineData("91")]        // 0x5B = LWin，也不是修饰键槽
    [InlineData("1")]         // 鼠标左键
    [InlineData("17,17")]     // 重复
    [InlineData("17,16,18,17")] // 4 个（且重复）
    [InlineData("17, 16")]    // 逗号后的空格
    [InlineData("17,")]       // 空项
    [InlineData(",17")]
    [InlineData("x")]
    public void RejectsBadModifiers(string modifiersLine)
    {
        Assert.False(KeyHelperProtocol.TryParseSendKeyRequest("65", modifiersLine, "10",
            out _, out var failureReason));
        Assert.Equal(KeyHelperProtocol.ReasonBadPayload, failureReason);
    }

    [Fact]
    public void AcceptsEmptyModifierLine()
    {
        Assert.True(KeyHelperProtocol.TryParseSendKeyRequest("65", "", "10", out var request, out _));
        Assert.Empty(request.ModifierKeyCodes);
    }

    // ── 拒：窗口句柄 ────────────────────────────────────────

    [Fact]
    public void ParsesWindowHandleAsHexNotDecimal()
    {
        // 线路上的 hwnd 是十六进制、**不带 0x 前缀**（BuildSendKeyPayload 用 ToString("X") 写）。
        // 这一条把进制钉死：十进制 10 应当是 0x10 = 16，不是 10。
        Assert.True(KeyHelperProtocol.TryParseSendKeyRequest("65", "", "10", out var request, out _));
        Assert.Equal(0x10, (long)request.TargetWindow);

        Assert.True(KeyHelperProtocol.TryParseSendKeyRequest("65", "", "1A2B", out var hex, out _));
        Assert.Equal(0x1A2B, (long)hex.TargetWindow);

        // 带前缀在这条线路里不合法（两端都是我们自己的代码，严格一点更安全）
        Assert.False(KeyHelperProtocol.TryParseSendKeyRequest("65", "", "0x10", out _, out _));
    }

    [Theory]
    [InlineData("0")]   // 空窗口
    [InlineData("-1")]
    [InlineData("xyz")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("FFFFFFFFFFFFFFFF")] // 溢出 long
    public void RejectsBadWindowHandle(string? windowLine)
    {
        Assert.False(KeyHelperProtocol.TryParseSendKeyRequest("65", "", windowLine,
            out _, out var failureReason));
        Assert.Equal(KeyHelperProtocol.ReasonBadPayload, failureReason);
    }

    // ── 换行注入：结构上不成立，用用例把它钉住 ──────────────

    [Fact]
    public void RejectsPayloadSmugglingExtraLines()
    {
        // 想往字段里塞换行、伪造出「多一条命令」的载荷：
        // 真到助手里，ReadLine 会把它切成两行，于是后续字段全部错位 —— 这里模拟错位后的形态。
        Assert.False(KeyHelperProtocol.TryParseSendKeyRequest("65\n66", "0", "10", out _, out var reason));
        Assert.Equal(KeyHelperProtocol.ReasonBadPayload, reason);

        // 修饰键那行塞换行：多出来的部分会顶掉 hwnd 行，于是 hwnd 不再是合法十六进制
        Assert.False(KeyHelperProtocol.TryParseSendKeyRequest("65", "17\nEXIT", "", out _, out var reason2));
        Assert.Equal(KeyHelperProtocol.ReasonBadPayload, reason2);
    }

    // ── 拒：危险组合键（助手侧的第二道防线）──────────────────

    [Theory]
    [InlineData("115", "18")] // Alt + F4
    [InlineData("9", "18")]   // Alt + Tab
    [InlineData("27", "18")]  // Alt + Esc
    [InlineData("91", "")]    // 单发 Win
    [InlineData("92", "17")]  // Win + Ctrl（没 Alt）
    public void RejectsBlockedChords(string virtualKeyLine, string modifiersLine)
    {
        Assert.False(KeyHelperProtocol.TryParseSendKeyRequest(virtualKeyLine, modifiersLine, "10",
            out _, out var failureReason));
        Assert.Equal(KeyHelperProtocol.ReasonBlockedChord, failureReason);
    }

    // ── 回复 ────────────────────────────────────────────────

    [Theory]
    [InlineData("OK")]
    [InlineData("ok")]
    [InlineData("OK\r")]
    public void ParsesOkReply(string line)
    {
        Assert.True(KeyHelperProtocol.TryParseReply(line, out var succeeded, out var reason));
        Assert.True(succeeded);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("FAIL:not-foreground", "not-foreground")]
    [InlineData("FAIL:target-mismatch", "target-mismatch")]
    [InlineData("fail:unauthorized", "unauthorized")]
    public void ParsesFailureReply(string line, string expectedReason)
    {
        Assert.True(KeyHelperProtocol.TryParseReply(line, out var succeeded, out var reason));
        Assert.False(succeeded);
        Assert.Equal(expectedReason, reason);
    }

    [Theory]
    [InlineData("HELLO")]
    [InlineData("FAIL:")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsGarbageReplies(string? line)
        => Assert.False(KeyHelperProtocol.TryParseReply(line, out _, out _));

    [Fact]
    public void BuildsReplies()
    {
        Assert.Equal("OK", KeyHelperProtocol.BuildOkReply());
        Assert.Equal("FAIL:" + KeyHelperProtocol.ReasonNotForeground,
            KeyHelperProtocol.BuildFailureReply(KeyHelperProtocol.ReasonNotForeground));
    }

    [Fact]
    public void FailureReplyNeverCarriesNewlines()
    {
        var reply = KeyHelperProtocol.BuildFailureReply("bad\nreason\r\nEXIT");

        Assert.DoesNotContain('\n', reply);
        Assert.DoesNotContain('\r', reply);
    }

    [Fact]
    public void FailureReplyFallsBackToBadPayload()
    {
        // 空原因会让 TryParseReply 判失败 —— 与其发一条对面看不懂的回复，不如明确说载荷不合法
        Assert.Equal("FAIL:" + KeyHelperProtocol.ReasonBadPayload, KeyHelperProtocol.BuildFailureReply(""));
        Assert.Equal("FAIL:" + KeyHelperProtocol.ReasonBadPayload, KeyHelperProtocol.BuildFailureReply("   "));
    }
}