using GIMI_ModManager.Core.ModMarket;

namespace JASM.Tests;

/// <summary>
/// 锁住本地引擎与线上 PostgREST 查询的语义等价性。每一条对应 ModMarketService
/// 里的一个查询参数，注释里写明线上那一侧的写法。
/// </summary>
public class MarketQueryEngineTests
{
    private static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    private static MarketPage<TestMarketRow> Run(IEnumerable<TestMarketRow> rows, MarketQuery query) =>
        MarketQueryEngine.Execute(rows.ToList(), query);

    // ---- is_published -------------------------------------------------------

    /// <summary>线上恒带 <c>is_published=eq.true</c>；快照本就只含已发布行，这里是挡住坏数据。</summary>
    [Fact]
    public void Execute_DropsUnpublishedRows()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), Title = "published" },
            new TestMarketRow { Id = Id(2), Title = "draft", IsPublished = false }
        };

        var page = Run(rows, new MarketQuery());

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("published", Assert.Single(page.Items).Title);
    }

    // ---- character=eq.X / not.eq / Skins 分支 -------------------------------

    [Fact]
    public void Character_WithoutFilter_ReturnsEveryCharacter()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), Character = "千咲" },
            new TestMarketRow { Id = Id(2), Character = "UI" },
            new TestMarketRow { Id = Id(3), Character = "Other/Misc" }
        };

        Assert.Equal(3, Run(rows, new MarketQuery()).TotalCount);
        Assert.Equal(3, Run(rows, new MarketQuery { Character = MarketCategoryKeys.All }).TotalCount);
        Assert.Equal(3, Run(rows, new MarketQuery { Character = "   " }).TotalCount);
    }

    [Fact]
    public void Character_MatchesExactly()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), Character = "千咲" },
            new TestMarketRow { Id = Id(2), Character = "千咲皮肤" },
            new TestMarketRow { Id = Id(3), Character = "UI" }
        };

        var page = Run(rows, new MarketQuery { Character = "千咲" });

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(Id(1), Assert.Single(page.Items).Id);
    }

    /// <summary>PostgREST 的 eq 大小写敏感 —— 不能退化成忽略大小写。</summary>
    [Fact]
    public void Character_IsCaseSensitive()
    {
        var rows = new[] { new TestMarketRow { Id = Id(1), Character = "UI" } };

        Assert.Equal(1, Run(rows, new MarketQuery { Character = "UI" }).TotalCount);
        Assert.Equal(0, Run(rows, new MarketQuery { Character = "ui" }).TotalCount);
    }

    /// <summary>「角色皮肤」= <c>character=not.eq.UI and character=not.eq.Other/Misc</c>。</summary>
    [Fact]
    public void Skins_ExcludesUiAndOtherMisc()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), Character = "千咲" },
            new TestMarketRow { Id = Id(2), Character = "UI" },
            new TestMarketRow { Id = Id(3), Character = "Other/Misc" },
            new TestMarketRow { Id = Id(4), Character = "爱弥斯的机甲" }
        };

        var page = Run(rows, new MarketQuery { Character = MarketCategoryKeys.Skins });

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { Id(1), Id(4) }, page.Items.Select(r => r.Id).OrderBy(g => g).ToArray());
    }

    // ---- download_url=not.is.null ------------------------------------------

    /// <summary>
    /// 本文件最重要的一条。空串在 Postgres 里不是 null，同样命中 <c>not.is.null</c>：
    /// 线上实测 76 行（74 空串 + 2 真链），改用 IsNullOrEmpty 就只剩 2 行。
    /// </summary>
    [Fact]
    public void DirectDownloadOnly_KeepsEmptyString_ButDropsNull()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), DownloadUrl = null },
            new TestMarketRow { Id = Id(2), DownloadUrl = string.Empty },
            new TestMarketRow { Id = Id(3), DownloadUrl = "https://pan.baidu.com/s/1x" }
        };

        var page = Run(rows, new MarketQuery { DirectDownloadOnly = true });

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { Id(2), Id(3) }, page.Items.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void DirectDownloadOnly_Off_KeepsNullRows()
    {
        var rows = new[] { new TestMarketRow { Id = Id(1), DownloadUrl = null } };

        Assert.Equal(1, Run(rows, new MarketQuery()).TotalCount);
    }

    // ---- nsfw=eq.X ---------------------------------------------------------

    [Fact]
    public void Nsfw_FilterIsTriState()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), Nsfw = false },
            new TestMarketRow { Id = Id(2), Nsfw = true }
        };

        Assert.Equal(2, Run(rows, new MarketQuery()).TotalCount);
        Assert.Equal(2, Run(rows, new MarketQuery { Nsfw = null }).TotalCount);
        Assert.Equal(new[] { Id(1) }, Run(rows, new MarketQuery { Nsfw = false }).Items.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { Id(2) }, Run(rows, new MarketQuery { Nsfw = true }).Items.Select(r => r.Id).ToArray());
    }

    // ---- created_at=gte.{yyyy-MM-dd} ---------------------------------------

    /// <summary>
    /// 线上按**日期**比较，即「该日 UTC 零点起」，所以传进来的时间戳必须先截断到日 ——
    /// 直接用时刻会比线上更严格。
    /// </summary>
    [Fact]
    public void CreatedOnOrAfter_TruncatesToUtcDay()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), CreatedAt = new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc) },
            new TestMarketRow { Id = Id(2), CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) },
            new TestMarketRow { Id = Id(3), CreatedAt = new DateTime(2026, 4, 1, 23, 59, 59, DateTimeKind.Utc) }
        };

        var page = Run(rows, new MarketQuery { CreatedOnOrAfterUtc = new DateTime(2026, 4, 1, 18, 30, 0, DateTimeKind.Utc) });

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { Id(2), Id(3) }, page.Items.Select(r => r.Id).ToArray());
    }

    /// <summary>null 表示不带这个过滤条件。</summary>
    [Fact]
    public void CreatedOnOrAfter_Null_DoesNotFilter() =>
        Assert.Equal(1, Run([new TestMarketRow { Id = Id(1) }], new MarketQuery()).TotalCount);

    /// <summary>
    /// System.Text.Json 把 "…+00:00" 解析成 <c>Kind=Local</c> 的本地时间。若拿它直接和
    /// <c>Kind=Utc</c> 的截断点比 Ticks，就会凭空差一个时区偏移。
    /// 断言的是「同一刻的两种 Kind 表示必须给出同一结果」—— 这个断言与测试机时区无关。
    /// </summary>
    [Theory]
    [InlineData(0, true)] // 恰好在截断点上：不早于 ⇒ 保留
    [InlineData(-1, false)] // 早一秒 ⇒ 排除
    public void CreatedOnOrAfter_NormalisesLocalKindTimestamps(int offsetSeconds, bool expectedIncluded)
    {
        var instant = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(offsetSeconds);
        var cutoff = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        var asUtc = instant.UtcDateTime;
        var asLocal = instant.LocalDateTime; // 即 STJ 解析带偏移时间戳后的样子
        Assert.Equal(DateTimeKind.Local, asLocal.Kind);

        var fromUtc = Run([new TestMarketRow { Id = Id(1), CreatedAt = asUtc }], new MarketQuery { CreatedOnOrAfterUtc = cutoff });
        var fromLocal = Run([new TestMarketRow { Id = Id(2), CreatedAt = asLocal }], new MarketQuery { CreatedOnOrAfterUtc = cutoff });

        Assert.Equal(expectedIncluded, fromUtc.TotalCount == 1);
        Assert.Equal(fromUtc.TotalCount, fromLocal.TotalCount);
    }

    // ---- order=… -----------------------------------------------------------

    [Fact]
    public void CreatedAtDesc_OrdersNewestFirst()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            new TestMarketRow { Id = Id(2), CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
            new TestMarketRow { Id = Id(3), CreatedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc) }
        };

        var page = Run(rows, new MarketQuery { Sort = MarketSortKey.CreatedAtDesc });

        Assert.Equal(new[] { Id(2), Id(3), Id(1) }, page.Items.Select(r => r.Id).ToArray());
    }

    [Theory]
    [InlineData(MarketSortKey.DownloadsDesc)]
    [InlineData(MarketSortKey.LikesDesc)]
    [InlineData(MarketSortKey.ViewsDesc)]
    public void NumericSorts_OrderLargestFirst(MarketSortKey sort)
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), DownloadsCount = 5, LikesCount = 5, Views = 5 },
            new TestMarketRow { Id = Id(2), DownloadsCount = 90, LikesCount = 90, Views = 90 },
            new TestMarketRow { Id = Id(3), DownloadsCount = 40, LikesCount = 40, Views = 40 }
        };

        var page = Run(rows, new MarketQuery { Sort = sort });

        Assert.Equal(new[] { Id(2), Id(3), Id(1) }, page.Items.Select(r => r.Id).ToArray());
    }

    /// <summary>
    /// 标题排序用 en-US 文化比较（DB 侧是 ICU en-US）：主级别忽略大小写，
    /// "alpha" 在 "Beta" 之前。换成 Ordinal 的话顺序会反过来 —— 这条就是那个分水岭。
    /// </summary>
    [Fact]
    public void TitleAsc_IsCultureAware_NotOrdinal()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), Title = "gamma" },
            new TestMarketRow { Id = Id(2), Title = "Beta" },
            new TestMarketRow { Id = Id(3), Title = "alpha" }
        };

        var page = Run(rows, new MarketQuery { Sort = MarketSortKey.TitleAsc });

        Assert.Equal(new[] { "alpha", "Beta", "gamma" }, page.Items.Select(r => r.Title).ToArray());
    }

    /// <summary>
    /// 线上没有次级排序键，同分行的先后其实是未定义的（库里 created_at 有 124 组重复）。
    /// 本地用 ThenBy(Id) 钉死，纯粹是为了让分页稳定 —— 属于改进而非等价。
    /// </summary>
    [Theory]
    [InlineData(MarketSortKey.CreatedAtDesc)]
    [InlineData(MarketSortKey.DownloadsDesc)]
    [InlineData(MarketSortKey.LikesDesc)]
    [InlineData(MarketSortKey.ViewsDesc)]
    [InlineData(MarketSortKey.TitleAsc)]
    public void Ties_AreBrokenById_Ascending(MarketSortKey sort)
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(3), Title = "same" },
            new TestMarketRow { Id = Id(1), Title = "same" },
            new TestMarketRow { Id = Id(2), Title = "same" }
        };

        var page = Run(rows, new MarketQuery { Sort = sort });

        Assert.Equal(new[] { Id(1), Id(2), Id(3) }, page.Items.Select(r => r.Id).ToArray());
    }

    // ---- limit / offset ----------------------------------------------------

    [Fact]
    public void Paging_SlicesWithExactTotal()
    {
        var rows = Enumerable.Range(1, 5)
            .Select(n => new TestMarketRow { Id = Id(n), Title = $"mod {n}" })
            .ToArray();

        var first = Run(rows, new MarketQuery { Page = 1, PageSize = 2 });
        var second = Run(rows, new MarketQuery { Page = 2, PageSize = 2 });
        var last = Run(rows, new MarketQuery { Page = 3, PageSize = 2 });
        var beyond = Run(rows, new MarketQuery { Page = 9, PageSize = 2 });

        Assert.Equal(new[] { "mod 1", "mod 2" }, first.Items.Select(r => r.Title).ToArray());
        Assert.Equal(0, first.Offset);
        Assert.Equal(new[] { "mod 3", "mod 4" }, second.Items.Select(r => r.Title).ToArray());
        Assert.Equal(2, second.Offset);
        Assert.Equal(new[] { "mod 5" }, last.Items.Select(r => r.Title).ToArray());
        Assert.Equal(4, last.Offset);
        Assert.Empty(beyond.Items);
        Assert.Equal(16, beyond.Offset);

        // 本地模式的 total 永远是精确值，不发线上那个 int.MaxValue 哨兵。
        Assert.All(new[] { first, second, last, beyond }, page => Assert.Equal(5, page.TotalCount));
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyPage()
    {
        var page = Run([], new MarketQuery());

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(0, page.Offset);
    }

    /// <summary>脏参数不该抛异常（上层是 UI 线程，抛了就整页空白）。</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-3, -10)]
    public void DegeneratePaging_DoesNotThrow(int page, int pageSize)
    {
        var rows = new[] { new TestMarketRow { Id = Id(1) } };

        var result = Run(rows, new MarketQuery { Page = page, PageSize = pageSize });

        Assert.Equal(1, result.TotalCount);
        Assert.Empty(result.Items);
    }

    // ---- title=ilike.*q* ---------------------------------------------------

    [Fact]
    public void Search_UsesLikeSemantics_NotLiteralContains()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), Title = "心月狐 by kuzan" },
            new TestMarketRow { Id = Id(2), Title = "a_b underscore" },
            new TestMarketRow { Id = Id(3), Title = "axb underscore" }
        };

        Assert.Equal(new[] { "心月狐 by kuzan" }, Run(rows, new MarketQuery { Search = "KUZAN" }).Items.Select(r => r.Title).ToArray());
        // "a_b" 作为 LIKE 模式同时命中 "a_b" 与 "axb" —— 字面量匹配只会命中前者。
        Assert.Equal(2, Run(rows, new MarketQuery { Search = "a_b" }).TotalCount);
    }

    [Fact]
    public void EmptySearch_DoesNotFilter() =>
        Assert.Equal(1, Run([new TestMarketRow { Id = Id(1), Title = "任意" }], new MarketQuery { Search = "   " }).TotalCount);

    // ---- 组合 --------------------------------------------------------------

    [Fact]
    public void Filters_AreCombinedWithAnd()
    {
        var rows = new[]
        {
            new TestMarketRow { Id = Id(1), Character = "千咲", Title = "泳装", DownloadUrl = "https://a", CreatedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) },
            new TestMarketRow { Id = Id(2), Character = "千咲", Title = "泳装", DownloadUrl = null, CreatedAt = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc) },
            new TestMarketRow { Id = Id(3), Character = "UI", Title = "泳装", DownloadUrl = "https://c", CreatedAt = new DateTime(2026, 5, 3, 0, 0, 0, DateTimeKind.Utc) },
            new TestMarketRow { Id = Id(4), Character = "千咲", Title = "泳装", DownloadUrl = "https://d", CreatedAt = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc) }
        };

        var page = Run(rows, new MarketQuery
        {
            Character = "千咲",
            Search = "泳装",
            DirectDownloadOnly = true,
            CreatedOnOrAfterUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        });

        Assert.Equal(Id(1), Assert.Single(page.Items).Id);
    }
}
