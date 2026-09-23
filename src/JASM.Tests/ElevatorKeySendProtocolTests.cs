using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="ElevatorKeySendProtocol"/> — the wire format the main app uses to ask the elevated
/// <c>Elevator.exe</c> to synthesize a key chord into a game that runs at a higher integrity level than JASM.
///
/// Two things are worth locking down here: the version gate (an already-built 2.0.0.0 Elevator knows the
/// targeted refresh but NOT this command, and an old Elevator silently ignores commands it does not know,
/// so the version is the only deterministic discriminator) and the payload codec, which is deliberately
/// borrowed from <see cref="KeyHelperProtocol"/> rather than reimplemented.
/// </summary>
public class ElevatorKeySendProtocolTests
{
    [Fact]
    public void WireCommandAndVersionMarkerAreFrozen()
    {
        // 命令字与版本下限是对已构建出的助手二进制的契约，改了就是静默失效
        Assert.Equal("3", ElevatorKeySendProtocol.SendKeyCommand);
        Assert.Equal("3.0.0.0", ElevatorKeySendProtocol.MinimumFileVersionForKeySend);
    }

    [Theory]
    [InlineData(null, false)]        // 版本读不出来：保守按「不认识」处理
    [InlineData("", false)]
    [InlineData("abc", false)]       // 不是版本号
    [InlineData("1.0.0.0", false)]   // 旧 Elevator.exe 的默认值（没有版本信息时读出来就是这个）
    [InlineData("2.0.0.0", false)]   // 认识带目标的刷新 "2"，但**不认识**送键 "3"
    [InlineData("2.9.9.9", false)]
    [InlineData("3.0.0.0", true)]
    [InlineData("3.0.0.1", true)]
    [InlineData("10.0.0.0", true)]   // 按数值逐段比较，不是字符串比较
    public void GatesTheSendKeyCommandOnTheFileVersion(string? fileVersion, bool expected)
        => Assert.Equal(expected, ElevatorKeySendProtocol.SupportsKeySend(fileVersion));

    [Fact]
    public void BuildsCommandThenVirtualKeyThenModifiersThenWindowHandle()
    {
        var payload = ElevatorKeySendProtocol.BuildSendKeyPayload(0x41, [0x11, 0x10], 0x1234);

        // 逐行、每个字段都是数字（与 ElevatorRefreshProtocol 同一条约定）。
        // 注意进制不是统一的：vk 与修饰键是**十进制**，窗口句柄是**十六进制** ——
        // 这是 KeyHelperProtocol 既有的取舍（它解析时用 AllowHexSpecifier），这里沿用不改。
        Assert.Equal(new[] { "3", "65", "17,16", "1234" }, payload);
    }

    [Fact]
    public void BuildsAnEmptyModifierLineWhenThereAreNoModifiers()
    {
        var payload = ElevatorKeySendProtocol.BuildSendKeyPayload(0x41, [], 0x10);

        Assert.Equal(new[] { "3", "65", "", "10" }, payload);

        // 空行必须能被解析回「没有修饰键」，而不是被当成缺行
        Assert.True(KeyHelperProtocol.TryParseSendKeyRequest(payload[1], payload[2], payload[3],
            out var request, out _));
        Assert.Empty(request.ModifierKeyCodes);
    }

    [Fact]
    public void ThePayloadRoundTripsThroughTheBorrowedCodec()
    {
        // 编解码委托给 KeyHelperProtocol 是刻意的：护栏与「字段全是数字」的约束只有一份实现。
        // 这条测试就是那个委托关系的看门人 —— 谁把两边的格式改岔了，这里立刻红。
        var payload = ElevatorKeySendProtocol.BuildSendKeyPayload(0x52, [0x12], 0xABCD);

        Assert.True(KeyHelperProtocol.TryParseSendKeyRequest(payload[1], payload[2], payload[3],
            out var request, out var failureReason));
        Assert.Null(failureReason);
        Assert.Equal((ushort)0x52, request.VirtualKey);
        Assert.Equal(new ushort[] { 0x12 }, request.ModifierKeyCodes);
        Assert.Equal((nint)0xABCD, request.TargetWindow);
    }

    [Fact]
    public void ResponsePayloadIsRejectedByTheTurnedAroundSide()
    {
        // 反过来把「回复行」当载荷解析也必须失败：防止用错行导致发出一个 wildcard 键
        Assert.False(KeyHelperProtocol.TryParseSendKeyRequest(KeyHelperProtocol.OkReply, null, null,
            out _, out _));
    }

    [Theory]
    [InlineData("OK", ElevatorKeySendReply.Ok)]
    [InlineData("ok", ElevatorKeySendReply.Ok)]
    [InlineData("OK\r", ElevatorKeySendReply.Ok)]                              // 对面用 CRLF 也认
    [InlineData("FAIL:not-foreground", ElevatorKeySendReply.Failure)]
    [InlineData("fail:injection-failed", ElevatorKeySendReply.Failure)]
    [InlineData("FAIL:", ElevatorKeySendReply.None)]                           // 有前缀没原因 = 认不出
    [InlineData("", ElevatorKeySendReply.None)]
    [InlineData(null, ElevatorKeySendReply.None)]                              // EOF：助手可能已退出
    [InlineData("something else", ElevatorKeySendReply.None)]
    public void ParsesTheAssistantsReply(string? line, ElevatorKeySendReply expected)
        => Assert.Equal(expected, ElevatorKeySendProtocol.ParseReply(line, out _));

    [Fact]
    public void SurfacesTheFailureReasonToken()
    {
        Assert.Equal(ElevatorKeySendReply.Failure,
            ElevatorKeySendProtocol.ParseReply("FAIL:not-foreground", out var reason));
        Assert.Equal(KeyHelperProtocol.ReasonNotForeground, reason);
    }

    [Theory]
    [InlineData(KeyHelperProtocol.ReasonBadPayload)]
    [InlineData(KeyHelperProtocol.ReasonBlockedChord)]
    [InlineData(KeyHelperProtocol.ReasonNotForeground)]
    [InlineData(KeyHelperProtocol.ReasonTargetMismatch)]
    [InlineData(KeyHelperProtocol.ReasonInjectionFailed)]
    [InlineData(KeyHelperProtocol.ReasonUnauthorized)]
    public void DescribesEveryKnownFailureReasonInUserTerms(string reasonToken)
    {
        var described = ElevatorKeySendProtocol.DescribeFailure(reasonToken);

        // 每个已知 token 都得有一句给用户看的话，且不能把 token 原样漏给用户
        Assert.False(string.IsNullOrWhiteSpace(described));
        Assert.DoesNotContain(reasonToken, described);
    }

    [Fact]
    public void FallsBackForMissingOrUnknownReasons()
    {
        Assert.False(string.IsNullOrWhiteSpace(ElevatorKeySendProtocol.DescribeFailure(null)));
        Assert.False(string.IsNullOrWhiteSpace(ElevatorKeySendProtocol.DescribeFailure("")));

        // 没见过的 token 原样带出，不吞掉 —— 助手将来加了原因，至少还能看到原文
        Assert.Contains("brand-new-reason", ElevatorKeySendProtocol.DescribeFailure("brand-new-reason"));
    }
}