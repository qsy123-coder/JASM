using System.IO.Compression;
using System.Text.Json;
using GIMI_ModManager.Core.ModMarket;

namespace JASM.Tests;

/// <summary>
/// 用一份从真实快照裁出来的 fixture（29 行：18 行原样取自线上快照、只裁短了描述与图集，
/// 11 行按同样的列形状合成边界数据）锁住两件事：
///
/// <list type="number">
///   <item><b>列契约</b>：快照的列清单就是 <c>scripts/mods-snapshot.sql</c> 里那 24 列。
///         下游一旦增删列，这里立刻红 —— 而不是等到用户机器上某个字段静默变空。</item>
///   <item><b>语义</b>：本地引擎在真实数据上跑出来的筛选/排序/分页结果与线上口径一致。</item>
/// </list>
///
/// 刻意不提交完整的 509KB 快照：它按设计每天会变，进仓库只会变成反复失真的噪声源。
/// </summary>
public class ModMarketSnapshotFixtureTests
{
    private const int AllRows = 29;
    private const int PublishedRows = 28;

    /// <summary>fixture 里刻意留了一行空白角色 —— 生产库里没有，这一行专门用来暴露两侧口径差。</summary>
    private const int BlankCharacterRows = 1;

    private const int UiPublishedRows = 5;
    private const int OtherMiscPublishedRows = 2;

    /// <summary>侧边栏「角色皮肤」计数：已发布 28 − 空白角色 1 − UI 5 − Other/Misc 2。</summary>
    private const int TallySkins = PublishedRows - BlankCharacterRows - UiPublishedRows - OtherMiscPublishedRows;

    /// <summary>皮肤**列表**的行数：空白角色那一行会落进 <c>not.eq.UI and not.eq.Other/Misc</c>，故比计数多 1。</summary>
    private const int SkinsListRows = PublishedRows - UiPublishedRows - OtherMiscPublishedRows;

    private const int DirectDownloadPublishedRows = 15;
    private const int NsfwPublishedRows = 1;

    /// <summary>快照的完整列清单（顺序照 <c>mods-snapshot.sql</c> 的 select 列表）。</summary>
    private static readonly string[] SnapshotColumns =
    [
        "id", "title", "character", "version", "game_version", "game_key", "description",
        "images", "video_url", "download_url", "downloads_count", "drive_links", "nsfw",
        "mod_author_url", "views", "favorites_count", "likes_count", "comments_count",
        "rating_count", "rating_average", "is_published", "is_featured", "featured_order", "created_at"
    ];

    /// <summary>
    /// JsonDocument 必须活到进程结束：JsonElement 只是它内部缓冲区上的一层视图，
    /// 释放后任何取值都会 ObjectDisposedException。生产侧的 ModMarketSnapshot 同理。
    /// </summary>
    private static readonly JsonDocument Fixture = LoadFixture();

    private static JsonDocument LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mods-snapshot.sample.json.gz");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"缺少 fixture：{path}（JASM.Tests.csproj 里的 CopyToOutputDirectory 没生效？）", path);

        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gzip);
    }

    private static IReadOnlyList<JsonMarketRow> Rows() =>
        Fixture.RootElement.EnumerateArray().Select(e => new JsonMarketRow(e)).ToList();

    private static MarketPage<JsonMarketRow> Run(MarketQuery query) =>
        MarketQueryEngine.Execute(Rows(), query);

    // ---- 形状 --------------------------------------------------------------

    /// <summary>快照顶层是**裸数组**，不是包了一层对象 —— 反序列化时必须按 List 处理。</summary>
    [Fact]
    public void Fixture_IsABareJsonArray()
    {
        Assert.Equal(JsonValueKind.Array, Fixture.RootElement.ValueKind);
        Assert.Equal(AllRows, Fixture.RootElement.GetArrayLength());
    }

    /// <summary>列清单一字不差 —— 这是本文件最重要的一条。</summary>
    [Fact]
    public void Fixture_UsesExactlyTheSnapshotColumnSet()
    {
        var expected = SnapshotColumns.OrderBy(c => c, StringComparer.Ordinal).ToArray();

        foreach (var row in Rows())
        {
            var actual = row.Raw.EnumerateObject().Select(p => p.Name).OrderBy(c => c, StringComparer.Ordinal).ToArray();

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// 快照没有这两列，这是两处设计取舍的前提，必须显式钉住：
    /// 没有 <c>is_available</c> ⇒ 引擎与 IMarketModRow 都不得按它过滤（否则整页空白）；
    /// 没有 <c>updated_at</c> ⇒ 排序键里不可能有「最近更新」。
    /// </summary>
    [Theory]
    [InlineData("is_available")]
    [InlineData("updated_at")]
    [InlineData("xxmi_install_guide")]
    public void Fixture_LacksUnavailableColumns(string column)
    {
        foreach (var row in Rows())
        {
            Assert.False(row.Raw.TryGetProperty(column, out _), column);
        }
    }

    /// <summary>每一行都必须能被逐字段读出（缺键会抛 KeyNotFoundException，而不是静默给默认值）。</summary>
    [Fact]
    public void Fixture_EveryRowMapsOntoEveryEngineField()
    {
        foreach (var row in Rows())
        {
            Assert.NotEqual(Guid.Empty, row.Id);
            Assert.NotNull(row.Title);
            Assert.NotNull(row.Character);
            Assert.NotEqual(default, row.CreatedAt);
            _ = row.DownloadUrl;
            _ = row.IsPublished;
            _ = row.Nsfw;
            _ = row.Views;
            _ = row.LikesCount;
            _ = row.DownloadsCount;
        }
    }

    /// <summary>
    /// 真实快照的时间戳带偏移 + **6 位**小数秒（如 2026-08-10T13:12:58.215087+00:00）。
    /// STJ 对这类值返回 Kind=Local 的本地时间 —— 这正是 MarketQueryEngine 里要归一的原因。
    /// </summary>
    [Fact]
    public void Fixture_CreatedAtKeepsOffsetAndMicroseconds()
    {
        var row = Rows().Single(r => r.Id == Guid.Parse("6d99c0a7-f6d8-4124-901b-070beb7ddda9"));

        Assert.Equal(DateTimeKind.Local, row.CreatedAt.Kind);
        Assert.Equal(
            new DateTime(2026, 8, 10, 13, 12, 58, DateTimeKind.Utc).AddTicks(2_150_870),
            row.CreatedAt.ToUniversalTime());
    }

    [Fact]
    public void Fixture_ImagesAreStringArraysOrNull()
    {
        var withImages = 0;

        foreach (var row in Rows())
        {
            var images = row.Raw.GetProperty("images");
            if (images.ValueKind == JsonValueKind.Null) continue;

            Assert.Equal(JsonValueKind.Array, images.ValueKind);
            foreach (var image in images.EnumerateArray())
            {
                Assert.Equal(JsonValueKind.String, image.ValueKind);
            }

            withImages++;
        }

        Assert.True(withImages > 0, "fixture 至少要有一行带图，否则图集路径没被覆盖");
    }

    /// <summary><c>DriveLinkEntry</c> 把 <c>platform</c> 映射到 Name、<c>url</c> 映射到 Url。</summary>
    [Fact]
    public void Fixture_DriveLinksCarryPlatformAndUrlKeys()
    {
        var withLinks = 0;

        foreach (var row in Rows())
        {
            var links = row.Raw.GetProperty("drive_links");
            Assert.Equal(JsonValueKind.Array, links.ValueKind);

            foreach (var link in links.EnumerateArray())
            {
                Assert.Equal(JsonValueKind.String, link.GetProperty("platform").ValueKind);
                Assert.Equal(JsonValueKind.String, link.GetProperty("url").ValueKind);
                withLinks++;
            }
        }

        Assert.True(withLinks > 0, "fixture 至少要有一条网盘链接，否则详情面板的链接路径没被覆盖");
    }

    // ---- 与线上口径对齐 ----------------------------------------------------

    /// <summary>快照只含已发布行；fixture 里那 1 行未发布是刻意塞进去验证引擎的防御性过滤。</summary>
    [Fact]
    public void Fixture_PublishedRowCountMatchesTheLiveTable()
    {
        var rows = Rows();

        Assert.Equal(AllRows, rows.Count);
        Assert.Equal(PublishedRows, rows.Count(r => r.IsPublished));
        Assert.Equal(PublishedRows, Run(new MarketQuery()).TotalCount);
        Assert.Equal(NsfwPublishedRows, rows.Count(r => r.IsPublished && r.Nsfw));
    }

    /// <summary>
    /// 真实数据里空串比真链接还多（本 fixture：空串 13、真链 2、null 13）。
    /// 所以 <c>download_url=not.is.null</c> 必须按「非 null」而不是「非空」实现 —— 这条是回归锁。
    /// </summary>
    [Fact]
    public void Fixture_DirectDownloadFilterKeepsEmptyStrings()
    {
        var published = Rows().Where(r => r.IsPublished).ToList();

        Assert.Equal(DirectDownloadPublishedRows, Run(new MarketQuery { DirectDownloadOnly = true }).TotalCount);
        Assert.Equal(13, published.Count(r => r.DownloadUrl == string.Empty));
        Assert.Equal(13, published.Count(r => r.DownloadUrl is null));
        Assert.Equal(2, published.Count(r => r.DownloadUrl?.StartsWith("http") == true));

        // 反面：如果误用 IsNullOrEmpty，剩下的就只有那 2 条真链接。
        Assert.NotEqual(2, Run(new MarketQuery { DirectDownloadOnly = true }).TotalCount);
    }

    /// <summary>
    /// 分类计数与聚合视图同口径：空白角色名跳过（视图那边是 <c>btrim(character) &lt;&gt; ''</c>），
    /// 其余逐项累加。fixture 里那行空白角色正是为了锁住这个跳过。
    /// </summary>
    [Fact]
    public void Fixture_CategoryTallyMatchesTheAggregateView()
    {
        var tally = MarketCategoryTally.FromCharacters(Rows().Where(r => r.IsPublished).Select(r => r.Character));

        Assert.Equal(PublishedRows - BlankCharacterRows, tally.Total);
        Assert.Equal(UiPublishedRows, tally.Ui);
        Assert.Equal(OtherMiscPublishedRows, tally.OtherMisc);
        Assert.Equal(TallySkins, tally.Skins);
    }

    /// <summary>
    /// 侧边栏计数与列表长度在「空白角色」这一行上本来就会差 1，两条路径都如实复刻了这个口径：
    /// 计数跳过空白角色，而 <c>character=not.eq.UI and character=not.eq.Other/Misc</c> 会把它算进皮肤。
    /// 库里实测 62 个分组里没有空白角色，所以线上两边同为 5207 —— fixture 刻意塞这行就是为了不掩盖它。
    /// </summary>
    [Fact]
    public void Fixture_CharacterFiltersMatchTheLiveQueries()
    {
        Assert.Equal(UiPublishedRows, Run(new MarketQuery { Character = MarketCategoryKeys.Ui }).TotalCount);
        Assert.Equal(OtherMiscPublishedRows, Run(new MarketQuery { Character = MarketCategoryKeys.OtherMisc }).TotalCount);

        var skins = Run(new MarketQuery { Character = MarketCategoryKeys.Skins });
        Assert.Equal(SkinsListRows, skins.TotalCount);
        Assert.Contains(skins.Items, r => r.Character == string.Empty);
    }

    // ---- 查询 --------------------------------------------------------------

    /// <summary>默认列表 = 线上那条 <c>order=title.asc&amp;limit=24</c>。</summary>
    [Fact]
    public void Fixture_DefaultQueryPagesExactlyLikeTheLiveList()
    {
        var first = Run(new MarketQuery { Sort = MarketSortKey.TitleAsc, Page = 1, PageSize = 24 });
        var second = Run(new MarketQuery { Sort = MarketSortKey.TitleAsc, Page = 2, PageSize = 24 });

        Assert.Equal(24, first.Items.Count);
        Assert.Equal(4, second.Items.Count);
        Assert.Equal(PublishedRows, first.TotalCount);
        Assert.Equal(PublishedRows, second.TotalCount);
        Assert.Empty(first.Items.Select(r => r.Id).Intersect(second.Items.Select(r => r.Id)));
    }

    /// <summary>真实标题里带下划线的作者名：LIKE 模式下 <c>_</c> 是「任意单字符」，所以三行都能搜到。</summary>
    [Fact]
    public void Fixture_SearchFindsRealTitlesWithUnderscore()
    {
        var page = Run(new MarketQuery { Search = "_eldarC" });

        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, r => Assert.Contains("_eldarC", r.Title));
    }

    [Fact]
    public void Fixture_CreatedAtDescPutsNewestFirst()
    {
        var page = Run(new MarketQuery { Sort = MarketSortKey.CreatedAtDesc });

        var timestamps = page.Items.Select(r => r.CreatedAt.ToUniversalTime()).ToArray();
        Assert.Equal(timestamps.OrderByDescending(t => t).ToArray(), timestamps);
    }

    /// <summary>库里存在同一时刻的多行（真实快照里就有两对），分页必须靠 Id 次级键稳定下来。</summary>
    [Fact]
    public void Fixture_DuplicateTimestampsStillPageDeterministically()
    {
        var first = Run(new MarketQuery { Sort = MarketSortKey.CreatedAtDesc });
        var again = Run(new MarketQuery { Sort = MarketSortKey.CreatedAtDesc });

        Assert.Equal(first.Items.Select(r => r.Id), again.Items.Select(r => r.Id));

        var duplicated = first.Items
            .Where(r => r.CreatedAt.ToUniversalTime() == new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc))
            .Select(r => r.Id)
            .ToArray();

        Assert.Equal(3, duplicated.Length);
        Assert.Equal(duplicated.OrderBy(g => g).ToArray(), duplicated);
    }

    /// <summary>
    /// fixture 行 → IMarketModRow。刻意全部走 GetProperty：缺键会抛 KeyNotFoundException
    /// 而不是静默给默认值，所以它同时是列契约的断言。
    /// </summary>
    private sealed class JsonMarketRow : IMarketModRow
    {
        private readonly JsonElement _row;

        public JsonMarketRow(JsonElement row) => _row = row;

        public JsonElement Raw => _row;

        public Guid Id => _row.GetProperty("id").GetGuid();

        public string Title => _row.GetProperty("title").GetString() ?? string.Empty;

        public string Character => _row.GetProperty("character").GetString() ?? string.Empty;

        public bool IsPublished => _row.GetProperty("is_published").GetBoolean();

        public bool Nsfw => _row.GetProperty("nsfw").GetBoolean();

        /// <summary>空串算「有值」—— 与 PostgREST 的 <c>not.is.null</c> 一致。</summary>
        public string? DownloadUrl => _row.GetProperty("download_url").ValueKind == JsonValueKind.Null
            ? null
            : _row.GetProperty("download_url").GetString();

        public int Views => _row.GetProperty("views").GetInt32();

        public int LikesCount => _row.GetProperty("likes_count").GetInt32();

        public int DownloadsCount => _row.GetProperty("downloads_count").GetInt32();

        /// <summary>走 JsonElement.GetDateTime()，即 STJ 自己的 ISO 解析路径（含 Kind 陷阱）。</summary>
        public DateTime CreatedAt => _row.GetProperty("created_at").GetDateTime();
    }
}
