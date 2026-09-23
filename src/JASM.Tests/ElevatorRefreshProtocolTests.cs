using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="ElevatorRefreshProtocol"/> — the wire format the main app uses to ask the elevated
/// <c>Elevator.exe</c> to restore, focus and F10 the game.
///
/// The version gate is what keeps already-shipped (older) Elevator binaries working: they only understand
/// the historical <c>0</c> command and silently ignore anything else, so the client must be able to tell
/// old from new before sending a targeted command.
/// </summary>
public class ElevatorRefreshProtocolTests
{
    [Fact]
    public void WireCommandsAreFrozenBecauseOldElevatorBinariesOnlyKnowTheLegacyOne()
    {
        // 已发布的旧 Elevator.exe 只认 "0"：这两个字面量是对它们的契约，改了就是静默失效
        Assert.Equal("0", ElevatorRefreshProtocol.LegacyRefreshCommand);
        Assert.Equal("2", ElevatorRefreshProtocol.TargetedRefreshCommand);
    }

    [Theory]
    [InlineData(null, false)]      // 读不到版本信息
    [InlineData("", false)]
    [InlineData("abc", false)]     // 不是版本号
    [InlineData("1.0.0.0", false)] // 旧 Elevator.exe 的默认值
    [InlineData("1.9.9.9", false)]
    [InlineData("2.0.0.0", true)]
    [InlineData("2.0.1.0", true)]
    [InlineData("10.0.0.0", true)] // 按数值逐段比较，不是字符串比较
    public void GatesTheTargetedCommandOnTheFileVersion(string? fileVersion, bool expected)
        => Assert.Equal(expected, ElevatorRefreshProtocol.SupportsTargetedRefresh(fileVersion));

    [Fact]
    public void BuildsCommandThenWindowHandleAsPlainDecimal()
    {
        var payload = ElevatorRefreshProtocol.BuildTargetedRefreshPayload(0x1234);

        Assert.Equal(new[] { "2", "4660" }, payload);
    }

    [Theory]
    [InlineData("OK", ElevatorRefreshReply.Ok)]
    [InlineData("ok", ElevatorRefreshReply.Ok)]
    [InlineData("OK\r", ElevatorRefreshReply.Ok)]
    [InlineData("  OK  ", ElevatorRefreshReply.Ok)]
    [InlineData("FAIL:not-foreground", ElevatorRefreshReply.Failure)]
    [InlineData("fail:BAD-PAYLOAD", ElevatorRefreshReply.Failure)]
    [InlineData("FAIL:", ElevatorRefreshReply.None)]                     // 有前缀没原因 = 认不出
    [InlineData("Waiting for connection...", ElevatorRefreshReply.None)] // 助手的控制台日志混进来了
    [InlineData(null, ElevatorRefreshReply.None)]                        // EOF：旧助手收到 "2" 后直接断连
    public void ParsesReplies(string? line, ElevatorRefreshReply expected)
        => Assert.Equal(expected, ElevatorRefreshProtocol.ParseReply(line, out _));

    [Fact]
    public void ReturnsTheFailureReasonToken()
    {
        ElevatorRefreshProtocol.ParseReply("FAIL:not-foreground", out var reason);

        Assert.Equal(ElevatorRefreshProtocol.ReasonNotForeground, reason);
    }

    [Fact]
    public void HasNoFailureReasonOnSuccess()
    {
        ElevatorRefreshProtocol.ParseReply("OK", out var reason);

        Assert.Null(reason);
    }
}