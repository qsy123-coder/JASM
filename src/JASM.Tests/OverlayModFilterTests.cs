using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="OverlayModFilter"/> — 浮窗 Mod 列表的搜索过滤与「已启用优先」排序。
///
/// 匹配字段与主窗口画廊是同一套（FolderName / Name / Author），排序也对齐画廊最后那步
/// 「已启用项前移」。两个界面对同一个搜索词必须命中同一批 Mod，否则用户会以为浮窗"丢了东西"。
/// </summary>
public class OverlayModFilterTests
{
    private sealed record FakeMod(string FolderName, string Name, string Author, bool IsEnabled) : IOverlayModEntry;

    private static FakeMod Mod(string folderName, bool enabled, string? name = null, string? author = null)
        => new(folderName, name ?? folderName, author ?? "anon", enabled);

    [Fact]
    public void ReturnsEverythingInTheOriginalOrderWhenThereIsNoSearchText()
    {
        var mods = new[] { Mod("b", false), Mod("a", false), Mod("c", false) };

        var result = OverlayModFilter.Apply(mods, null);

        // 空搜索不做任何重排：浮窗沿用后端给的顺序（按日期），用户在主窗口看到的顺序才对得上
        Assert.Equal(new[] { "b", "a", "c" }, result.Select(m => m.FolderName));
    }

    [Fact]
    public void TreatsAnEmptySearchTextAsNoSearch()
    {
        var mods = new[] { Mod("a", false), Mod("b", false) };

        Assert.Equal(2, OverlayModFilter.Apply(mods, string.Empty).Count);
    }

    [Fact]
    public void LiftsEnabledModsToTheFrontPreservingRelativeOrderWithinEachGroup()
    {
        var mods = new[]
        {
            Mod("off-1", false),
            Mod("on-1", true),
            Mod("off-2", false),
            Mod("on-2", true),
        };

        var result = OverlayModFilter.Apply(mods, null);

        // 两个分组内部各自保持原有顺序（稳定排序），而不是被打乱
        Assert.Equal(new[] { "on-1", "on-2", "off-1", "off-2" }, result.Select(m => m.FolderName));
    }

    [Theory]
    [InlineData("hair", "hair_mod")]        // 命中 FolderName
    [InlineData("hair", "Cool Hair Mod")]   // 命中 Name
    [InlineData("hair", "hair-by-someone")] // 命中 Author
    public void MatchesOnAnyOfTheThreeFieldsTheGalleryUses(string search, string fieldValue)
    {
        var mod = new FakeMod("folder", "name", "author", true);
        var candidate = search switch
        {
            "hair" when fieldValue == "hair_mod" => mod with { FolderName = fieldValue },
            "hair" when fieldValue == "Cool Hair Mod" => mod with { Name = fieldValue },
            _ => mod with { Author = fieldValue }
        };

        Assert.True(OverlayModFilter.Matches(candidate, search));
    }

    [Fact]
    public void IgnoresCaseLikeTheGalleryDoes()
    {
        var mod = new FakeMod("HairMod", "name", "author", true);

        Assert.True(OverlayModFilter.Matches(mod, "hairmod"));
    }

    [Fact]
    public void ReturnsNothingWhenNoModMatches()
    {
        var mods = new[] { Mod("hair", true), Mod("dress", false) };

        Assert.Empty(OverlayModFilter.Apply(mods, "sword"));
    }

    [Fact]
    public void StillLiftsEnabledModsWhenASearchIsActive()
    {
        var mods = new[]
        {
            Mod("hair-off", false),
            Mod("hair-on", true),
            Mod("dress", false),
        };

        var result = OverlayModFilter.Apply(mods, "hair");

        Assert.Equal(new[] { "hair-on", "hair-off" }, result.Select(m => m.FolderName));
    }

    [Fact]
    public void DoesNotMatchOnFieldsTheGalleryDoesNotSearch()
    {
        // 画廊只搜 FolderName / Name / Author 三个字段。浮窗若顺手多搜一个字段（比如路径），
        // 同一个词在两个界面就会给出不同结果 —— 这条钉住"不许多搜"
        var mod = new FakeMod("folder", "name", "author", true);

        Assert.False(OverlayModFilter.Matches(mod, "C:\\mods\\somewhere"));
    }
}