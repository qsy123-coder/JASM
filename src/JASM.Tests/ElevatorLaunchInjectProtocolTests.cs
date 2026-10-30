using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

public class ElevatorLaunchInjectProtocolTests
{
    private const string Exe = @"F:\Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64\Wuthering Waves.exe";
    private const string Dll = @"C:\XXMI\WWMI\d3d11.dll";
    private const string Mapper = @"C:\Users\Wuthering\AppData\Local\JASM\UhdRuntime\Jasm.UhdMapper.dll";
    private const string Cwd = @"F:\Wuthering Waves\Wuthering Waves Game\Client\Binaries\Win64";

    [Fact]
    public void WireCommandAndVersionAreFrozen()
    {
        Assert.Equal("5", ElevatorLaunchInjectProtocol.LaunchInjectCommand);
        Assert.Equal("5.0.0.0", ElevatorLaunchInjectProtocol.MinimumFileVersionForLaunchInject);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("4.0.0.0", false)]
    [InlineData("5.0.0.0", true)]
    [InlineData("5.0.0.1", true)]
    public void GatesOnTheElevatorFileVersion(string? version, bool expected)
        => Assert.Equal(expected, ElevatorLaunchInjectProtocol.SupportsLaunchInject(version));

    [Fact]
    public void BuildsSixLinesWithOptionalMapper()
    {
        var withoutMapper = ElevatorLaunchInjectProtocol.BuildPayload(
            new ElevatorLaunchInjectRequest(Exe, Dll, Cwd, "-dx11"));
        Assert.Equal(6, withoutMapper.Length);
        Assert.Equal("", withoutMapper[3]);
        Assert.Equal("-dx11", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(withoutMapper[5])));

        var withMapper = ElevatorLaunchInjectProtocol.BuildPayload(
            new ElevatorLaunchInjectRequest(Exe, Dll, Cwd, "", Mapper));
        Assert.Equal(Mapper, withMapper[3]);
    }

    [Fact]
    public void PayloadRoundTripsWithMapper()
    {
        var payload = ElevatorLaunchInjectProtocol.BuildPayload(
            new ElevatorLaunchInjectRequest(Exe, Dll, Cwd, "--foo \"bar\"", Mapper));

        Assert.True(ElevatorLaunchInjectProtocol.TryParsePayload(payload[1..], out var request, out var reason));
        Assert.Null(reason);
        Assert.NotNull(request);
        Assert.Equal(Exe, request.GameExecutablePath);
        Assert.Equal(Dll, request.DllPath);
        Assert.Equal(Mapper, request.MapperDllPath);
        Assert.Equal("--foo \"bar\"", request.Arguments);
    }

    [Theory]
    [InlineData(@"C:\XXMI\WWMI\evil.dll", null)]                       // 不是 d3d11
    [InlineData(@"C:\XXMI\Other\d3d11.dll", null)]                     // 不在 WWMI 目录
    [InlineData(@"\\server\share\WWMI\d3d11.dll", null)]               // UNC
    [InlineData(Dll, @"C:\Temp\UhdRuntime\Other.dll")]                 // mapper 文件名错
    [InlineData(Dll, @"C:\Temp\Other\Jasm.UhdMapper.dll")]             // mapper 目录错
    public void RejectsPathsOutsideTheWhitelist(string dll, string? mapper)
    {
        var exe = Exe;
        if (dll.StartsWith(@"\\"))
            exe = @"\\server\share\WWMI\Wuthering Waves.exe";

        Assert.False(ElevatorLaunchInjectProtocol.TryValidatePaths(exe, dll, Cwd, mapper, out var reason));
        Assert.Equal(ElevatorLaunchInjectProtocol.ReasonPathNotAllowed, reason);
    }

    [Fact]
    public void RejectsUnknownGameExecutable()
    {
        Assert.False(ElevatorLaunchInjectProtocol.TryValidatePaths(
            @"C:\Windows\notepad.exe", Dll, Cwd, null, out var reason));
        Assert.Equal(ElevatorLaunchInjectProtocol.ReasonPathNotAllowed, reason);
    }

    [Fact]
    public void ParsesReplies()
    {
        Assert.Equal(ElevatorLaunchInjectReply.Ok, ElevatorLaunchInjectProtocol.ParseReply("OK 1234").Kind);
        Assert.Equal(1234, ElevatorLaunchInjectProtocol.ParseReply("OK 1234").ProcessId);

        var failure = ElevatorLaunchInjectProtocol.ParseReply("FAIL:inject-timeout");
        Assert.Equal(ElevatorLaunchInjectReply.Failure, failure.Kind);
        Assert.Equal(ElevatorLaunchInjectProtocol.ReasonInjectTimeout, failure.FailureReason);

        Assert.Equal(ElevatorLaunchInjectReply.None, ElevatorLaunchInjectProtocol.ParseReply("").Kind);
        Assert.Equal(ElevatorLaunchInjectReply.None, ElevatorLaunchInjectProtocol.ParseReply("OK 0").Kind);
    }
}
