using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="ElevatorForegroundHandbackProtocol"/> — the wire format the main app uses to ask the
/// elevated <c>Elevator.exe</c> to hand the foreground back to a chosen window.
///
/// This command exists because of an asymmetry the rest of the app cannot work around: after the helper
/// synthesizes F10, the helper is the "last input" process (injected input is attributed to the injector),
/// and it is the only process that can put the foreground back — JASM is at a lower integrity level than the
/// elevated game, so both <c>SetForegroundWindow</c> and the usual "inject a neutral input first" trick fail
/// there. The tests below lock down the two things that would silently break it on a user's machine: the
/// version gate (an old Elevator silently ignores unknown commands, so only the version discriminates) and
/// the handle line, which must be byte-identical to the one the send-key command writes.
/// </summary>
public class ElevatorForegroundHandbackProtocolTests
{
    [Fact]
    public void WireCommandAndVersionMarkerAreFrozen()
    {
        // 命令字与版本下限是对已构建出的助手二进制的契约，改了就是静默失效
        Assert.Equal("4", ElevatorForegroundHandbackProtocol.HandbackCommand);
        Assert.Equal("4.0.0.0", ElevatorForegroundHandbackProtocol.MinimumFileVersionForForegroundHandback);
    }

    [Theory]
    [InlineData(null, false)]        // 版本读不出来：保守按「不认识」处理
    [InlineData("", false)]
    [InlineData("abc", false)]       // 不是版本号
    [InlineData("1.0.0.0", false)]   // 旧 Elevator.exe 的默认值（没有版本信息时读出来就是这个）
    [InlineData("3.0.0.0", false)]   // 认识送键 "3"，但**不认识**归还前台 "4"
    [InlineData("3.9.9.9", false)]
    [InlineData("4.0.0.0", true)]
    [InlineData("4.0.0.1", true)]
    [InlineData("10.0.0.0", true)]   // 按数值逐段比较，不是字符串比较
    public void GatesTheHandbackCommandOnTheFileVersion(string? fileVersion, bool expected)
        => Assert.Equal(expected, ElevatorForegroundHandbackProtocol.SupportsForegroundHandback(fileVersion));

    [Fact]
    public void BuildsCommandThenTheWindowHandle()
    {
        var payload = ElevatorForegroundHandbackProtocol.BuildPayload(0x1234);

        // 句柄那一行是**十六进制、不带 0x 前缀**，与送键命令的第三行同一条约定
        Assert.Equal(new[] { "4", "1234" }, payload);
    }

    [Fact]
    public void TheWindowHandleLineIsTheSameFormatAsTheSendKeyCommandUses()
    {
        // 这条是「只有一份句柄格式」的看门人：助手那边读 [1] 用的是同一个 KeyHelperProtocol.TryParseWindowLine，
        // 谁把两边的格式改岔了这里立刻红 —— 那种岔子在真机上只表现为「助手说句柄不合法」。
        var handback = ElevatorForegroundHandbackProtocol.BuildPayload(0xABCD);
        var sendKey = ElevatorKeySendProtocol.BuildSendKeyPayload(0x79, [], 0xABCD);

        Assert.Equal(sendKey[3], handback[1]);

        Assert.True(KeyHelperProtocol.TryParseWindowLine(handback[1], out var window));
        Assert.Equal((nint)0xABCD, window);
    }

    [Theory]
    [InlineData("0", false)]         // 0 不是窗口
    [InlineData("", false)]
    [InlineData("0x10", false)]      // 带 0x 前缀不是本协议的写法
    [InlineData("not a handle", false)]
    [InlineData(null, false)]
    [InlineData("10", true)]
    public void RejectsWindowLinesThatAreNotPositiveHexHandles(string? line, bool expected)
        => Assert.Equal(expected, KeyHelperProtocol.TryParseWindowLine(line, out _));

    [Theory]
    [InlineData("OK", ElevatorForegroundHandbackReply.Ok)]
    [InlineData("ok", ElevatorForegroundHandbackReply.Ok)]
    [InlineData("OK\r", ElevatorForegroundHandbackReply.Ok)]                        // 对面用 CRLF 也认
    [InlineData("FAIL:foreground-not-taken", ElevatorForegroundHandbackReply.Failure)]
    [InlineData("FAIL:", ElevatorForegroundHandbackReply.None)]                     // 有前缀没原因 = 认不出
    [InlineData("", ElevatorForegroundHandbackReply.None)]
    [InlineData(null, ElevatorForegroundHandbackReply.None)]                        // EOF：旧助手不认识 "4"，压根不回话
    [InlineData("something else", ElevatorForegroundHandbackReply.None)]
    public void ParsesTheAssistantsReply(string? line, ElevatorForegroundHandbackReply expected)
        => Assert.Equal(expected, ElevatorForegroundHandbackProtocol.ParseReply(line, out _));

    [Fact]
    public void SurfacesTheFailureReasonToken()
    {
        // 交还失败只在日志里出现（尽力而为，不给用户弹文案），所以这里只保证 token 原样带出来
        Assert.Equal(ElevatorForegroundHandbackReply.Failure,
            ElevatorForegroundHandbackProtocol.ParseReply("FAIL:foreground-not-taken", out var reason));
        Assert.Equal(KeyHelperProtocol.ReasonForegroundNotTaken, reason);
    }
}