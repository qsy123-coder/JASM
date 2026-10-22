using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="OverlayNsfwMarks"/> — 浮窗右键打的那份「NSFW 名单」。
///
/// 三条要害：切换是**幂等配对**的（点两下回到原样）、**能扛住被手改坏的设置文件**、
/// 以及**存盘再读回来不丢东西**（名单是给人拿去下架的，丢了就是白审一轮）。
/// </summary>
public class OverlayNsfwMarksTests
{
    private static readonly Guid ModA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ModB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void ToggleMarksAnUnmarkedModAndReportsTheNewState()
    {
        var marks = new OverlayNsfwMarks();

        Assert.True(marks.Toggle(ModA, "changli", "长离-黑丝"));
        Assert.True(marks.IsMarked(ModA));
        Assert.Equal(1, marks.Count);
    }

    [Fact]
    public void ToggleOnAMarkedModUnmarksIt()
    {
        var marks = new OverlayNsfwMarks();
        marks.Toggle(ModA, "changli", "长离-黑丝");

        Assert.False(marks.Toggle(ModA, "changli", "长离-黑丝"));
        Assert.False(marks.IsMarked(ModA));
        Assert.Equal(0, marks.Count);
    }

    [Fact]
    public void MarkingIsPerModNotPerCharacter()
    {
        var marks = new OverlayNsfwMarks();
        marks.Toggle(ModA, "changli", "长离-黑丝");

        Assert.True(marks.IsMarked(ModA));
        Assert.False(marks.IsMarked(ModB));
    }

    [Fact]
    public void IsMarkedIsFalseForAnEmptyList()
    {
        Assert.False(new OverlayNsfwMarks().IsMarked(ModA));
    }

    /// <summary>
    /// 设置文件被手改成 <c>"Entries": null</c> 时反序列化出来就是 null。
    /// 这时标记必须还能用（补一张空表），而不是一次空引用把浮窗的右键彻底弄哑。
    /// </summary>
    [Fact]
    public void ToggleSurvivesANullEntriesList()
    {
        var marks = new OverlayNsfwMarks { Entries = null! };

        Assert.Equal(0, marks.Count);
        Assert.False(marks.IsMarked(ModA));
        Assert.True(marks.Toggle(ModA, "changli", "长离-黑丝"));
        Assert.Equal(1, marks.Count);
    }

    /// <summary>
    /// 存盘 → 读回不丢：这条锁住的是「审一轮的结论能活过重启」。
    /// 顺带锁住 <c>Key</c> 不落进 JSON（它是 <c>const</c>，注释里写了 <c>[JsonIgnore]</c>，
    /// 但真正让它不进文件的是"常量字段不被序列化"这件事 —— 换成属性就会出事）。
    /// </summary>
    [Fact]
    public async Task RoundTripsThroughTheSettingsSerializer()
    {
        var marks = new OverlayNsfwMarks();
        marks.Toggle(ModA, "changli", "长离-黑丝");
        marks.Toggle(ModB, "jiyan", "忌炎-XX");

        var json = await Json.StringifyAsync(marks);

        Assert.DoesNotContain(nameof(OverlayNsfwMarks.Key), json, StringComparison.Ordinal);

        var restored = await Json.ToObjectAsync<OverlayNsfwMarks>(json);

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Count);
        Assert.True(restored.IsMarked(ModA));
        Assert.True(restored.IsMarked(ModB));
        Assert.Equal("长离-黑丝", restored.Entries.Single(e => e.Id == ModA).Name);
        Assert.Equal("changli", restored.Entries.Single(e => e.Id == ModA).Character);
    }
}