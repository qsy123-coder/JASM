using GIMI_ModManager.Core.Services.WuwaModFixer;

namespace JASM.Tests;

/// <summary>
/// NDJSON 解析必须对上游宽进：空行、坏行、未知事件都不能让修复停摆。
/// </summary>
public class WuwaModCliEventTests
{
    [Fact]
    public void ParsesStartEvent()
    {
        Assert.True(WuwaModCliEvent.TryParse("""{"event":"start","mode":"fix","total":3}""", out var evt));

        Assert.NotNull(evt);
        Assert.Equal(WuwaModCliEventKind.Start, evt.Kind);
        Assert.Equal("fix", evt.Mode);
        Assert.Equal(3, evt.Total);
    }

    [Fact]
    public void ParsesProgressEvent()
    {
        Assert.True(WuwaModCliEvent.TryParse(
            """{"event":"progress","current":2,"total":5,"percent":40.5}""", out var evt));

        Assert.NotNull(evt);
        Assert.Equal(WuwaModCliEventKind.Progress, evt.Kind);
        Assert.Equal(2, evt.Current);
        Assert.Equal(5, evt.Total);
        Assert.Equal(40.5, evt.Percent);
    }

    [Fact]
    public void ParsesLogEvent()
    {
        Assert.True(WuwaModCliEvent.TryParse(
            """{"event":"log","level":"info","message":"Matching Objects Feixue"}""", out var evt));

        Assert.NotNull(evt);
        Assert.Equal(WuwaModCliEventKind.Log, evt.Kind);
        Assert.Equal("info", evt.Level);
        Assert.Equal("Matching Objects Feixue", evt.Message);
    }

    [Fact]
    public void ParsesFinishEvent()
    {
        Assert.True(WuwaModCliEvent.TryParse(
            """{"event":"finish","success":true,"message":"done"}""", out var evt));

        Assert.NotNull(evt);
        Assert.Equal(WuwaModCliEventKind.Finish, evt.Kind);
        Assert.True(evt.Success);
        Assert.Equal("done", evt.Message);
    }

    [Fact]
    public void AcceptsBooleanEncodedAsString()
    {
        Assert.True(WuwaModCliEvent.TryParse("""{"event":"finish","success":"false"}""", out var evt));
        Assert.False(evt!.Success);
    }

    [Fact]
    public void UnknownEventsBecomeUnknownInsteadOfFailing()
    {
        Assert.True(WuwaModCliEvent.TryParse("""{"event":"brand_new","foo":1}""", out var evt));

        Assert.NotNull(evt);
        Assert.Equal(WuwaModCliEventKind.Unknown, evt.Kind);
        Assert.Contains("brand_new", evt.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("""{"event":""}""")]
    [InlineData("""{"no_event":1}""")]
    [InlineData("[]")]
    public void RejectsMalformedLines(string line)
    {
        Assert.False(WuwaModCliEvent.TryParse(line, out _));
    }

    [Fact]
    public void ToleratesBomAndCrLf()
    {
        Assert.True(WuwaModCliEvent.TryParse(
            "﻿{\"event\":\"finish\",\"success\":true}\r\n", out var evt));

        Assert.Equal(WuwaModCliEventKind.Finish, evt!.Kind);
    }

    [Fact]
    public void MissingFieldsStayNull()
    {
        Assert.True(WuwaModCliEvent.TryParse("""{"event":"progress"}""", out var evt));

        Assert.Null(evt!.Current);
        Assert.Null(evt.Total);
        Assert.Null(evt.Percent);
    }
}