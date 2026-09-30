using System.Text.Json;
using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;
using GIMI_ModManager.Core.Services.GameBanana.Models;

namespace JASM.Tests;

/// <summary>
/// 用三份从线上真实响应裁出来的 fixture 锁住商店列表这条链路。
///
/// 它们是**实测证据的固化**：<c>docs/mod-store-prd.md</c> 里「已验证的 GameBanana API 事实」
/// 那张表中每一条能写成断言的，都在这里可执行 —— 那些坑（参数名被静默忽略、混合类型、
/// 总数口径随视图变）在真机上全部表现为「不报错但数据是错的」，只有测试能拦住。
///
/// fixture 是原样抓取后只裁短了记录条数（subfeed 3 条 / search 6 条 / profilepage 只留
/// 板块自身的几个标量 + 完整的根分类清单），字段名与嵌套结构与线上一致。
/// </summary>
public class ModStoreBrowseTests
{
    /// <summary>Subfeed：<c>_nPage=1&amp;_csvModelInclusions=Mod&amp;_sSort=updated</c> —— 3 条全 Mod、tags 全空。</summary>
    private static readonly ApiSubfeedResponse Subfeed = Load<ApiSubfeedResponse>("mod-store-subfeed.sample.json");

    /// <summary>Search：<c>_sSearchString=skin&amp;_idGameRow=20357</c> —— 6 条里只有 3 条是 Mod（其余是 Request/Question）。</summary>
    private static readonly ApiSubfeedResponse Search = Load<ApiSubfeedResponse>("mod-store-search.sample.json");

    /// <summary>ProfilePage：<c>Game/20357/ProfilePage</c> —— 侧栏「分类」与那些条目数都出自这里。</summary>
    private static readonly ApiGameProfilePageResponse ProfilePage =
        Load<ApiGameProfilePageResponse>("mod-store-game-profilepage.sample.json");

    private static T Load<T>(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"缺少 fixture：{path}（csproj 的 CopyToOutputDirectory 没生效？）", path);

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path))
               ?? throw new InvalidOperationException($"fixture 反序列化成了 null：{path}");
    }

    private static ModStorePage SubfeedPage() => ModStorePage.FromApi(Subfeed);

    private static ModStorePage SearchPage() => ModStorePage.FromApi(Search);

    // ---- 列表元数据 --------------------------------------------------------

    /// <summary>
    /// 页大小是服务端定死的 15，不是我们请求的 —— 要 3 条也会给 15 条。所以客户端不能
    /// 指望靠参数控制页大小，必须如实读出 <c>_nPerpage</c>。
    /// </summary>
    [Fact]
    public void Subfeed_TakesPageSizeFromTheServerNotFromUs()
    {
        var page = SubfeedPage();

        Assert.Equal(3, page.Items.Count);      // fixture 裁剩 3 条
        Assert.Equal(15, page.PageSize);        // 但服务端的页大小是 15
        Assert.Equal(3, page.RawRecordCount);
        Assert.True(page.HasMore);              // _bIsComplete=false
    }

    /// <summary>
    /// 浏览视图的总数直接取自 <c>_nRecordCount</c>（这里是 <c>_sSort=updated</c> 的 1333）。
    ///
    /// ⚠️ 这个数字**随排序变**：同一板块 default/new 报 3062、updated 只报 1333。
    /// 它只是「当前视图的记录数」，UI 上不能写成「板块共 N 个 mod」。
    /// </summary>
    [Fact]
    public void Subfeed_TotalIsTheViewSpecificRecordCount()
    {
        var page = SubfeedPage();

        Assert.Equal(1333, page.TotalCount);
        Assert.Equal(1333, Subfeed.Metadata!.RecordCount);

        // 反面对照：它跟 fixture 里的条目数毫无关系（拿 Items.Count 当总数就会显示 3）。
        Assert.NotEqual(page.Items.Count, page.TotalCount);
    }

    /// <summary>
    /// 搜索接口的 <c>_nRecordCount</c> 是**全类型**总数（705，含 Question/Request/Poll…），
    /// 直接当 mod 数用会虚高两倍多。正确口径是分类型命中里的 Mod 项：277。
    /// 这条是本文件最重要的回归锁。
    /// </summary>
    [Fact]
    public void Search_TotalPrefersTheModSectionMatchCount()
    {
        var page = SearchPage();

        Assert.Equal(277, page.TotalCount);
        Assert.Equal(705, Search.Metadata!.RecordCount);
        Assert.Contains(Search.Metadata.SectionMatchCounts!,
            c => c.ModelName == "Mod" && c.MatchCount == 277);

        Assert.NotEqual(705, page.TotalCount);
    }

    /// <summary>元数据缺失时必须退化成「已到底」 —— 反过来会让「加载更多」在拿不到元数据时无限翻页。</summary>
    [Fact]
    public void MissingMetadata_DegradesToACompleteSinglePage()
    {
        var record = new ApiSubfeedRecord { ModelName = ApiSubfeedRecord.ModModelName, ModId = 5, Name = "x" };

        var page = ModStorePage.FromApi(new ApiSubfeedResponse { Records = [record] });

        Assert.Single(page.Items);
        Assert.True(page.IsComplete);
        Assert.False(page.HasMore);
        Assert.Equal(ModStorePage.DefaultPageSize, page.PageSize);
        Assert.Equal(1, page.TotalCount); // 没元数据就只能拿条目数兜底
    }

    // ---- 混合类型过滤 ------------------------------------------------------

    /// <summary>
    /// 搜索接口返回**混合类型提交**，一页 15 条里可能大半不是 mod。非 Mod 必须逐条滤掉，
    /// 但它们仍然计入 <see cref="ModStorePage.RawRecordCount"/> —— 「还有没有下一页」
    /// 看的是服务端的 <c>_bIsComplete</c>，不是过滤后的条目数。
    /// </summary>
    [Fact]
    public void Search_FiltersOutNonModSubmissions()
    {
        var page = SearchPage();

        Assert.Equal(6, Search.Records!.Length);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(6, page.RawRecordCount);

        Assert.Equal(["529580", "537550", "568261"],
            page.Items.Select(m => m.Id.ModId).OrderBy(id => id, StringComparer.Ordinal));

        // 被滤掉的那三条确实存在于原始响应里（否则这条测试是自欺欺人）。
        Assert.Equal(["Question", "Question", "Request"],
            Search.Records.Where(r => r.ModelName != "Mod")
                .Select(r => r.ModelName!).OrderBy(n => n, StringComparer.Ordinal));

        // 过滤后不到 15 条，但后面还有货。
        Assert.True(page.HasMore);
    }

    /// <summary>非 Mod 记录进不了域模型；mod 记录进得来。三种类型逐条验证，不假设整页同质。</summary>
    [Fact]
    public void TryCreate_AcceptsOnlyModRecords()
    {
        var byId = Search.Records!.ToDictionary(r => r.ModId);

        Assert.Null(ModStoreMod.TryCreate(byId[65064]));            // Request
        Assert.Null(ModStoreMod.TryCreate(byId[68729]));            // Question
        Assert.NotNull(ModStoreMod.TryCreate(byId[537550]));        // Mod

        Assert.Null(ModStoreMod.TryCreate(null));
    }

    /// <summary>缺 <c>_idRow</c> 的残缺记录也要拒 —— 没有 Id 就定位不到要下载哪个 mod。</summary>
    [Fact]
    public void TryCreate_RejectsModRecordsWithoutAnId()
    {
        Assert.Null(ModStoreMod.TryCreate(new ApiSubfeedRecord { ModelName = "Mod" }));      // ModId 默认 -1
        Assert.Null(ModStoreMod.TryCreate(new ApiSubfeedRecord { ModelName = "Mod", ModId = 0 }));
        Assert.Null(ModStoreMod.TryCreate(new ApiSubfeedRecord { Name = "x", ModId = 5 })); // 没 _sModelName
    }

    // ---- 字段映射 ----------------------------------------------------------

    /// <summary>逐字段对照真实记录（含中文名与 <c>_SlugCat</c> 这种下划线开头的作者名）。</summary>
    [Fact]
    public void Mod_MapsEveryListFieldForARealRecord()
    {
        var mod = SubfeedPage().Items.Single(m => m.Id.ModId == "709792");

        Assert.Equal("Qingxiao清宵", mod.Name);
        Assert.Equal("_SlugCat", mod.AuthorName);
        Assert.Equal("1.2", mod.Version);
        Assert.Equal(593, mod.LikeCount);
        Assert.Equal(18427, mod.ViewCount);
        Assert.Equal(6, mod.CommentCount);
        Assert.True(mod.HasFiles);
        Assert.False(mod.IsObsolete);
        Assert.Equal("https://gamebanana.com/mods/709792", mod.ModPageUrl!.ToString());

        // 时间戳是 Unix 秒。缺字段时 DTO 上是 0，而 FromUnixTimeSeconds(0) 会给 1970 —— 那是
        // 一个看着合法的错日期，所以映射层必须把 <= 0 当「没有」。
        Assert.Equal(1787705440, mod.DateAdded!.Value.ToUnixTimeSeconds());
        Assert.Equal(1790740376, mod.DateUpdated!.Value.ToUnixTimeSeconds());
    }

    /// <summary>时间戳缺失时要变成 null，不能变成 1970-01-01。</summary>
    [Fact]
    public void Mod_TreatsMissingTimestampsAsNull()
    {
        var mod = ModStoreMod.TryCreate(new ApiSubfeedRecord
        {
            ModelName = "Mod", ModId = 1, Name = "x", DateAdded = 0, DateUpdated = 0
        });

        Assert.Null(mod!.DateAdded);
        Assert.Null(mod.DateUpdated);
    }

    // ---- 评论数 ------------------------------------------------------------

    /// <summary>
    /// 卡片上第三项统计取 <c>_nPostCount</c>。它必须**三个列表端点都给**才敢往卡片上放 ——
    /// 这里把两份 fixture（浏览 / 搜索）逐条对一遍；一旦哪个端点悄悄不给了，这条会红。
    ///
    /// 对照：<c>_nDownloadCount</c> 只有详情页有，所以卡片上**没有**下载量那一项。
    /// </summary>
    [Fact]
    public void Mod_MapsCommentCountFromPostCount()
    {
        var subfeed = SubfeedPage().Items.ToDictionary(m => m.Id.ModId, m => m.CommentCount);
        Assert.Equal(85, subfeed["658343"]);
        Assert.Equal(364, subfeed["575376"]);
        Assert.Equal(6, subfeed["709792"]);

        var search = SearchPage().Items.ToDictionary(m => m.Id.ModId, m => m.CommentCount);
        Assert.Equal(11, search["537550"]);
        Assert.Equal(6, search["529580"]);
        Assert.Equal(84, search["568261"]);
    }

    /// <summary>接口这一条没给 <c>_nPostCount</c> 时是 null（卡片上不显示数字），不能变成 0 条评论。</summary>
    [Fact]
    public void Mod_CommentCountIsNullWhenTheFieldIsMissing()
    {
        var mod = ModStoreMod.TryCreate(new ApiSubfeedRecord { ModelName = "Mod", ModId = 7 });

        Assert.Null(mod!.CommentCount);
    }

    /// <c>_sProfileUrl</c> 末段抠。这里锁的 Name 是**根分类**（Skins / UI），
    /// 不是角色 —— 角色在 <c>_aSubCategory</c>，见 <see cref="Character_ComesFromSubCategory"/>。
    /// </summary>
    [Fact]
    public void Category_ParsesTheIdOutOfTheProfileUrl()
    {
        var ui = SubfeedPage().Items.Single(m => m.Id.ModId == "658343").Category!;
        Assert.Equal("UI", ui.Name);
        Assert.Equal(29496, ui.Id);

        var skins = SubfeedPage().Items.Single(m => m.Id.ModId == "709792").Category!;
        Assert.Equal("Skins", skins.Name);
        Assert.Equal(29524, skins.Id);
    }

    /// <summary>
    /// 角色来自 <c>_aSubCategory</c> 而**不是**根分类 —— 这是上线前差点搞错的一处：
    /// 根分类实测只有 Skins / UI / Other-Misc 三个（不是角色），真正按角色筛的是子分类。
    ///
    /// 同时锁住「UI 类记录没有角色」：这类记录里 <c>_aSubCategory</c> 这个键**整个不存在**，
    /// 所以必须是 null 而不是空串（卡片上不占位）。
    /// </summary>
    [Fact]
    public void Character_ComesFromSubCategory()
    {
        var skins = SubfeedPage().Items.Single(m => m.Id.ModId == "709792");
        Assert.Equal("Qingxiao", skins.Character);
        Assert.Equal("Skins", skins.Category!.Name); // 根分类与角色是两回事

        var ui = SubfeedPage().Items.Single(m => m.Id.ModId == "658343");
        Assert.Null(ui.Character);
        Assert.Equal("UI", ui.Category!.Name);
    }

    /// <summary>子分类名称为空白时按「没有角色」处理，不留空串给 UI 判。</summary>
    [Fact]
    public void Character_IsNullWhenSubCategoryIsBlank()
    {
        var mod = ModStoreMod.TryCreate(new ApiSubfeedRecord
        {
            ModelName = "Mod", ModId = 1, SubCategory = new ApiSubfeedCategory { Name = "   " }
        });

        Assert.Null(mod!.Character);
    }

    /// <summary>分类缺失 / 名称为空 / 地址抠不出 id 时，都不能让映射炸掉。</summary>
    [Fact]
    public void Category_DegradesWithoutThrowing()
    {
        Assert.Null(ModStoreCategory.FromApi(null));
        Assert.Null(ModStoreCategory.FromApi(new ApiSubfeedCategory { Name = "  " }));

        var noId = ModStoreCategory.FromApi(new ApiSubfeedCategory { Name = "Skins", ProfileUrl = "https://gamebanana.com/mods/cats/" });
        Assert.NotNull(noId);
        Assert.Null(noId.Id);
        Assert.Equal("Skins", noId.Name);
    }

    /// <summary>缺 <c>_sName</c> 时是空串而不是 null，省得每张卡片都判空。</summary>
    [Fact]
    public void Mod_NameIsNeverNull()
    {
        var mod = ModStoreMod.TryCreate(new ApiSubfeedRecord { ModelName = "Mod", ModId = 7 });

        Assert.Equal(string.Empty, mod!.Name);
        Assert.Null(mod.AuthorName);
        Assert.Null(mod.ModPageUrl);
        Assert.Empty(mod.PreviewImages);
        Assert.Null(mod.Category);
        Assert.Null(mod.Character);
        Assert.Empty(mod.Tags);
    }

    /// <summary>非 https 或非 gamebanana.com 的详情页地址一概不认 —— 它会被拿去 OpenInBrowser。</summary>
    [Theory]
    [InlineData("http://gamebanana.com/mods/1", false)]
    [InlineData("https://evil.example.com/mods/1", false)]
    [InlineData("https://gamebanana.com.evil.com/mods/1", false)]
    [InlineData("https://gamebanana.com/mods/1", true)]
    public void ModPageUrl_MustBeHttpsOnGameBanana(string profileUrl, bool expected)
    {
        var mod = ModStoreMod.TryCreate(new ApiSubfeedRecord
        {
            ModelName = "Mod", ModId = 1, ProfileUrl = profileUrl
        });

        Assert.Equal(expected, mod!.ModPageUrl is not null);
    }

    // ---- 成人内容 ----------------------------------------------------------

    /// <summary>
    /// 「默认隐藏 NSFW」只能靠客户端的 <c>_bHasContentRatings</c> —— 服务端过滤参数实测无效。
    /// 两份 fixture 里各有一条成人内容（Subfeed 的 709792 / Search 的 568261），钉住这个唯一信号。
    /// </summary>
    [Fact]
    public void AdultFlag_IsTheOnlyClientSideSignal()
    {
        var subfeedAdult = SubfeedPage().Items.Where(m => m.IsAdult).Select(m => m.Id.ModId).ToArray();
        Assert.Equal(["709792"], subfeedAdult);

        var searchAdult = SearchPage().Items.Where(m => m.IsAdult).Select(m => m.Id.ModId).ToArray();
        Assert.Equal(["568261"], searchAdult);

        Assert.Contains("NSFW", SearchPage().Items.Single(m => m.IsAdult).Name);
    }

    // ---- 标签：两个端点行为不同 --------------------------------------------

    /// <summary>
    /// <c>_aTags</c> 在浏览（Subfeed）里恒为空数组、在搜索里有真值 —— 所以它不能作为浏览视图的
    /// 筛选维度。两份 fixture 把这个差异钉住（形状上两端都是数组，不会是 null）。
    /// </summary>
    [Fact]
    public void Tags_AreEmptyOnSubfeedButPopulatedOnSearch()
    {
        Assert.All(Subfeed.Records!, r => Assert.NotNull(r.Tags));
        Assert.All(SubfeedPage().Items, m => Assert.Empty(m.Tags));

        var tagged = SearchPage().Items.Single(m => m.Id.ModId == "537550");
        Assert.Equal(["jinhsi: manuka"], tagged.Tags);
    }

    // ---- 预览图地址 --------------------------------------------------------

    /// <summary>
    /// 真实响应里存在「<c>_aPreviewMedia</c> 在，但**根本没有 <c>_aImages</c> 键**」的提交
    /// （Question 93923 里只有 <c>_aMetadata</c>）—— 注意这不是空数组，是键整个缺失。
    /// DTO 上那个 <c>= []</c> 默认值就是为它准备的：缺键退化成「零张图」而不是 null，
    /// 否则下游每处调用都得判空。
    ///
    /// 这条以前写错过：拿它当「<c>_sBaseUrl</c> 是空串」的例子，结果 <c>Images[0]</c> 直接越界 ——
    /// PowerShell 的 <c>@($null)</c> 会把缺失的键显示成 1 个元素，肉眼看着像有图。
    /// </summary>
    [Fact]
    public void MediaUrls_HandleARealRecordWithoutAnyImages()
    {
        var withoutImages = Search.Records!.Single(r => r.ModId == 93923);

        Assert.NotNull(withoutImages.PreviewMedia);
        Assert.Empty(withoutImages.PreviewMedia.Images);
        Assert.Empty(GameBananaMediaUrls.GetPreviewImages(withoutImages.PreviewMedia));
    }

    /// <summary>
    /// 地址来自用户提交的内容，所以只认 https 的 images.gamebanana.com。
    /// 后两种是拼接陷阱：<c>images.gamebanana.com.evil.com</c> 与 <c>evil.example.com</c> ——
    /// 只看「包不包含域名」会放它们进来。
    /// </summary>
    [Theory]
    [InlineData("https://images.gamebanana.com/img/ss/mods", "6a871d1a3de14.jpg", true)]
    [InlineData("https://images.gamebanana.com/img/ss/mods", "", false)]
    [InlineData("", "6a871d1a3de14.jpg", false)]
    [InlineData("http://images.gamebanana.com/img/ss/mods", "6a871d1a3de14.jpg", false)]   // 非 https
    [InlineData("https://images.gamebanana.com.evil.com/img", "6a871d1a3de14.jpg", false)] // 域名后缀混淆
    [InlineData("https://evil.example.com/img", "6a871d1a3de14.jpg", false)]
    [InlineData("not a url", "6a871d1a3de14.jpg", false)]
    public void MediaUrls_OnlyAcceptTheGameBananaCdn(string baseUrl, string imageId, bool expected)
    {
        var url = GameBananaMediaUrls.TryCreateImageUrl(new ApiImageUrl { BaseUrl = baseUrl, ImageId = imageId });

        Assert.Equal(expected, url is not null);
    }

    /// <summary>映射出来的每张图都必须是 https 的图床地址（真实数据上跑一遍）。</summary>
    [Fact]
    public void PreviewImages_AreAllOnTheCdn()
    {
        var all = SubfeedPage().Items.Concat(SearchPage().Items).SelectMany(m => m.PreviewImages).ToArray();

        Assert.NotEmpty(all);
        Assert.All(all, url =>
        {
            Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
            Assert.Equal("images.gamebanana.com", url.Host);
        });
    }

    // ---- 永不抛 ------------------------------------------------------------

    /// <summary>畸形响应一律退化成空页 —— 远端给什么都不能让商店页崩掉。</summary>
    [Fact]
    public void FromApi_NeverThrowsOnMalformedResponses()
    {
        Assert.Same(ModStorePage.Empty, ModStorePage.FromApi(null));

        var noRecords = ModStorePage.FromApi(new ApiSubfeedResponse());
        Assert.Empty(noRecords.Items);
        Assert.Equal(0, noRecords.RawRecordCount);
        Assert.False(noRecords.HasMore);

        var emptyRecords = ModStorePage.FromApi(new ApiSubfeedResponse { Records = [] });
        Assert.Empty(emptyRecords.Items);

        // 整页都是非 Mod：条目 0 条，但原始条数仍在，且「还有下一页」照旧成立。
        var allFilteredOut = ModStorePage.FromApi(new ApiSubfeedResponse
        {
            Records = [new ApiSubfeedRecord { ModelName = "Poll", ModId = 1 }],
            Metadata = new ApiSubfeedMetadata { RecordCount = 10, IsComplete = false, PerPage = 15 }
        });

        Assert.Empty(allFilteredOut.Items);
        Assert.Equal(1, allFilteredOut.RawRecordCount);
        Assert.Equal(10, allFilteredOut.TotalCount);
        Assert.True(allFilteredOut.HasMore);
    }

    [Fact]
    public void EmptyPage_IsATerminalEmptyResult()
    {
        Assert.Empty(ModStorePage.Empty.Items);
        Assert.Equal(0, ModStorePage.Empty.TotalCount);
        Assert.False(ModStorePage.Empty.HasMore);
        Assert.True(ModStorePage.Empty.IsComplete);
    }

    // ---- 排序取值 ----------------------------------------------------------

    /// <summary>
    /// 枚举 → <c>_sSort</c> 的映射必须逐个覆盖：少一个就会静默退化成默认排序（错误无声无息）。
    /// </summary>
    [Fact]
    public void Sort_ValuesCoverEveryEnumMember()
    {
        Assert.Equal("default", GbSubfeedSort.Default.ToApiValue());
        Assert.Equal("new", GbSubfeedSort.New.ToApiValue());
        Assert.Equal("updated", GbSubfeedSort.Updated.ToApiValue());

        foreach (var sort in Enum.GetValues<GbSubfeedSort>())
        {
            Assert.False(string.IsNullOrWhiteSpace(sort.ToApiValue()));
        }
    }

    // ---- 板块 Id -----------------------------------------------------------

    /// <summary>板块 Id 直接拼进 URL，所以解析要严：必须 <c>games</c> 段 + 末段正整数。</summary>
    [Theory]
    [InlineData("https://gamebanana.com/games/20357", "20357")]
    [InlineData("https://gamebanana.com/games/20357/", "20357")]   // 尾斜杠
    [InlineData("https://gamebanana.com/mods/551373", null)]       // mod 地址不是游戏地址
    [InlineData("https://gamebanana.com/games/20357/mods", null)]  // 末段不是 id（宽松实现会给出 "mods"）
    [InlineData("https://gamebanana.com/games/abc", null)]
    [InlineData("https://gamebanana.com/games/0", null)]
    [InlineData("http://gamebanana.com/games/20357", null)]        // 非 https
    [InlineData("https://evil.example.com/games/20357", null)]
    public void GameId_IsParsedStrictly(string url, string? expected)
    {
        var parsed = GameBananaUrlHelper.TryGetGameIdFromUrl(new Uri(url), out var gameId);

        Assert.Equal(expected is not null, parsed);
        Assert.Equal(expected, gameId?.GameId);
    }

    /// <summary>record 的相等性按字符串比较 —— 两种构造方式得到同一个 Id。</summary>
    [Fact]
    public void GameId_IsAValueObject()
    {
        Assert.Equal(new GbGameId(20357), new GbGameId("20357"));
        Assert.Equal("20357", new GbGameId(20357).ToString());

        // 隐式转成 string 后能直接拼进 URL（客户端就是这么用的）。
        Assert.Equal("https://gamebanana.com/apiv11/Game/20357/Subfeed",
            "https://gamebanana.com/apiv11/Game/" + new GbGameId(20357) + "/Subfeed");
    }

    // ---- 侧栏「分类」用的根分类清单 ----------------------------------------

    /// <summary>
    /// 侧栏那三行（Skins 2815 / Other·Misc 155 / UI 84）就来自这里，数字是服务端给的
    /// <c>_nItemCount</c>，不是我们数出来的 —— 数出来得翻几十页。
    ///
    /// 同时也钉住「根分类只有这三个」：角色**不是**根分类（它们是子分类，见
    /// <see cref="Character_ComesFromSubCategory"/>），侧栏的角色表来自本地游戏数据。
    /// </summary>
    [Fact]
    public void RootCategories_ComeFromTheProfilePageWithTheirCounts()
    {
        var raw = ProfilePage.ModRootCategories!;
        Assert.Equal(3, raw.Length);

        // 响应里每条还带着 _nCategoryCount / _sUrl / _sIconUrl 等用不上的字段 ——
        // DTO 只认自己要的三个，多出来的字段不能让反序列化出错。
        var mapped = raw.Select(ModStoreRootCategory.FromApi).OfType<ModStoreRootCategory>().ToArray();

        Assert.Equal(3, mapped.Length);
        Assert.Equal([29524, 29493, 29496], mapped.Select(c => c.Id).ToArray());
        Assert.Equal(["Skins", "Other/Misc", "UI"], mapped.Select(c => c.Name).ToArray());
        Assert.Equal([2815, 155, 84], mapped.Select(c => c.ItemCount).ToArray());

        // 三个分类之和（3054）**不等于**「全部」那个数字（板块内容流报 3056）——
        // 口径不同（分类归属 vs 板块记录数），所以这里刻意不断言两者相等。
        Assert.Equal(3054, mapped.Sum(c => c.ItemCount));
    }

    /// <summary>
    /// 名称为空白、或 id 无效的根分类直接丢掉 —— 建不出能点选的筛选项。
    /// 缺 <c>_nItemCount</c> 时保持 -1（= 未知），界面据此不显示数字；**不能是 0**，
    /// 那会在侧栏上写成「这个分类一条 mod 都没有」。
    /// </summary>
    [Fact]
    public void RootCategory_DegradesWithoutThrowing()
    {
        Assert.Null(ModStoreRootCategory.FromApi(null));
        Assert.Null(ModStoreRootCategory.FromApi(new ApiRootCategory { Id = 29524, Name = "   " }));
        Assert.Null(ModStoreRootCategory.FromApi(new ApiRootCategory { Name = "Skins" })); // 缺 _idRow => 默认 -1
        Assert.Null(ModStoreRootCategory.FromApi(new ApiRootCategory { Id = 0, Name = "Skins" }));

        var noCount = ModStoreRootCategory.FromApi(new ApiRootCategory { Id = 29524, Name = " Skins " });

        Assert.Equal(29524, noCount!.Id);
        Assert.Equal("Skins", noCount.Name);   // 首尾空白去掉
        Assert.Equal(-1, noCount.ItemCount);   // 未知，不是 0
    }

    /// <summary>响应里没有根分类清单（板块主页结构变了 / 被裁）时是 null，不是抛异常。</summary>
    [Fact]
    public void ProfilePage_WithoutRootCategoriesDegradesToNull()
    {
        var parsed = JsonSerializer.Deserialize<ApiGameProfilePageResponse>("{}");

        Assert.NotNull(parsed);
        Assert.Null(parsed.ModRootCategories);
    }
}