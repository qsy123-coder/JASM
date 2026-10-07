using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="UnelevatedRelaunchProtocol"/> —— 去提权重启时那张「交接凭条」的格式与时效判定。
///
/// 这张凭条是**唯一**能让新进程认得出「我是来接班的、不是重复启动」的东西：去提权只能请 explorer
/// 代起进程，而 <c>explorer.exe "路径"</c> 传不了参数，新旧两份的映像路径与命令行完全相同。
/// 判错的两种后果都很具体 —— 认不出来 = 新进程被单实例检查挡回去（用户看到「点了重启，什么都没发生」）；
/// 认过头 = 两份 JASM 同时跑，共用 %LOCALAPPDATA%\JASM 互相覆盖。
/// </summary>
public class UnelevatedRelaunchProtocolTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FormatsAndParsesThePredecessorProcessId()
    {
        var content = UnelevatedRelaunchProtocol.FormatMarker(4242);

        Assert.True(UnelevatedRelaunchProtocol.TryParseMarker(content, out var processId));
        Assert.Equal(4242, processId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("4.2")]
    [InlineData("123 456")]
    public void RefusesToGuessAPidFromGarbage(string? content)
    {
        // 宁可当作「没有凭条」，也不要拿一个瞎猜的 pid 去等 —— 等一个不存在的进程会让启动白等一轮，
        // 而"没有凭条"只是退回单实例检查的老行为
        Assert.False(UnelevatedRelaunchProtocol.TryParseMarker(content, out var processId));
        Assert.Equal(0, processId);
    }

    [Fact]
    public void TreatsAFreshMarkerAsAPendingHandoff()
    {
        var content = UnelevatedRelaunchProtocol.FormatMarker(777);

        Assert.True(UnelevatedRelaunchProtocol.IsHandoffPending(content, Now.AddSeconds(-5), Now, out var processId));
        Assert.Equal(777, processId);
    }

    [Fact]
    public void IgnoresAMarkerLeftOverFromAnEarlierLaunch()
    {
        var content = UnelevatedRelaunchProtocol.FormatMarker(777);

        // 隔了几分钟的凭条只可能是上次启动失败留下的垃圾，拿它去等一个早就不在的 pid 只会让启动白等
        var staleWritten = Now - UnelevatedRelaunchProtocol.MarkerValidity - TimeSpan.FromSeconds(1);

        Assert.False(UnelevatedRelaunchProtocol.IsHandoffPending(content, staleWritten, Now, out _));
    }

    [Fact]
    public void AcceptsAMarkerWhenTheClockWentBackwards()
    {
        var content = UnelevatedRelaunchProtocol.FormatMarker(777);

        // 时钟被往回调过（写入时间"在未来"）：宁可多等一轮，也别因为改过系统时间就把交接丢掉
        Assert.True(UnelevatedRelaunchProtocol.IsHandoffPending(content, Now.AddMinutes(5), Now, out _));
    }

    [Fact]
    public void AStaleMarkerIsNotRescuedByBeingUnparseable()
    {
        Assert.False(UnelevatedRelaunchProtocol.IsHandoffPending("garbage", Now, Now, out _));
    }
}