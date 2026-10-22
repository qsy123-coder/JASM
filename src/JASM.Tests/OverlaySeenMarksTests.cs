using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="OverlaySeenMarks"/> — 浮窗里中键打的「已看过」名单。
///
/// 与 NSFW 名单共用 <see cref="OverlayMarkList"/> 的切换逻辑，但**设置键必须分开**：
/// 同一件 Mod 可以既被标过 NSFW、又被看过，两份记录不能互相覆盖。
/// </summary>
public class OverlaySeenMarksTests
{
    private static readonly Guid ModA = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void ToggleMarksAndUnmarks()
    {
        var marks = new OverlaySeenMarks();

        Assert.True(marks.Toggle(ModA, "feixue", "绯雪-零"));
        Assert.True(marks.IsMarked(ModA));

        Assert.False(marks.Toggle(ModA, "feixue", "绯雪-零"));
        Assert.False(marks.IsMarked(ModA));
    }

    [Fact]
    public void SeenAndNsfwListsAreSeparate()
    {
        var seen = new OverlaySeenMarks();
        var nsfw = new OverlayNsfwMarks();

        seen.Toggle(ModA, "feixue", "绯雪-零");

        Assert.True(seen.IsMarked(ModA));
        Assert.False(nsfw.IsMarked(ModA));
        Assert.NotEqual(OverlaySeenMarks.Key, OverlayNsfwMarks.Key);
    }

    [Fact]
    public async Task RoundTripsUnderItsOwnKey()
    {
        var marks = new OverlaySeenMarks();
        marks.Toggle(ModA, "feixue", "绯雪-零");

        var json = await Json.StringifyAsync(marks);

        // Key 是 const，不会进 JSON；读回来仍然是同一份名单
        Assert.DoesNotContain(nameof(OverlaySeenMarks.Key), json, StringComparison.Ordinal);

        var restored = await Json.ToObjectAsync<OverlaySeenMarks>(json);

        Assert.NotNull(restored);
        Assert.True(restored!.IsMarked(ModA));
        Assert.Equal("绯雪-零", restored.Entries.Single(entry => entry.Id == ModA).Name);
    }
}
