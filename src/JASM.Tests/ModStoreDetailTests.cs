using System.Text.Json;
using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;
using GIMI_ModManager.Core.Services.GameBanana.Models;

namespace JASM.Tests;

/// <summary>
/// 用四份线上真实响应锁住商店**详情**这条链路（详情端点 + 文件端点各两份，一份隐藏 mod、
/// 一份普通 mod）。
///
/// 两份 mod 是刻意挑的对照：
/// <list type="bullet">
///   <item><b>709792</b>（Qingxiao清宵，<c>_sInitialVisibility=hide</c>）：文件**只在**
///         <c>_aArchivedFiles</c> 里、<c>_aFiles</c> 键不存在；有内容分级；角色分类带父分类；
///         作者头像是真图；正文是带链接的 HTML。</item>
///   <item><b>658343</b>（Chibi Portraits，UI 类）：两个字段的文件都有；无内容分级；
///         <c>_aCategory</c> 就是根分类「UI」且 <c>_aSuperCategory</c> 键不存在；
///         作者用默认头像；<c>_sDescription</c> 是**空串**。</item>
/// </list>
/// 详情这条链路的坑几乎全是「不报错但显示错」——「UI 类 mod 的角色显示成 UI」、
/// 「隐藏 mod 看着没有文件」、HTML 正文直接糊在界面上。只有这些断言能拦住。
/// </summary>
public class ModStoreDetailTests
{
    private static readonly ApiModProfile HiddenModProfile =
        Load<ApiModProfile>("mod-store-mod-profilepage-709792.sample.json");

    private static readonly ApiModFilesInfo HiddenModFiles =
        Load<ApiModFilesInfo>("mod-store-mod-downloadpage-709792.sample.json");

    private static readonly ApiModProfile UiModProfile =
        Load<ApiModProfile>("mod-store-mod-profilepage-658343.sample.json");

    private static readonly ApiModFilesInfo UiModFiles =
        Load<ApiModFilesInfo>("mod-store-mod-downloadpage-658343.sample.json");

    private static T Load<T>(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"缺少 fixture：{path}（csproj 的 CopyToOutputDirectory 没生效？）", path);

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path))
               ?? throw new InvalidOperationException($"fixture 反序列化成了 null：{path}");
    }

    private static string RawFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

    /// <summary>隐藏 mod（文件在归档字段里）。</summary>
    private static ModStoreDetail HiddenMod() => ModStoreDetail.TryCreate(HiddenModProfile, HiddenModFiles)!;

    /// <summary>UI 类 mod（没有角色、有活跃文件）。</summary>
    private static ModStoreDetail UiMod() => ModStoreDetail.TryCreate(UiModProfile, UiModFiles)!;

    // ---- 标量映射 ----------------------------------------------------------

    [Fact]
    public void Detail_MapsEveryScalarForAHiddenMod()
    {
        var detail = HiddenMod();

        Assert.Equal("709792", detail.Id.ModId);
        Assert.Equal("Qingxiao清宵", detail.Name);
        Assert.Equal("_SlugCat", detail.AuthorName);
        Assert.Equal("1.2", detail.Version);

        // 下载量**只有**这个端点给（列表接口没有这一项），所以它必须映射出来。
        Assert.Equal(1185, detail.DownloadCount);
        Assert.Equal(596, detail.LikeCount);
        Assert.Equal(18929, detail.ViewCount);
        Assert.Equal(6, detail.CommentCount);
        Assert.Equal(3, detail.ThanksCount);

        Assert.Equal(1787705440, detail.DateAdded!.Value.ToUnixTimeSeconds());
        Assert.Equal(1790740376, detail.DateUpdated!.Value.ToUnixTimeSeconds());

        Assert.False(detail.IsObsolete);
        Assert.True(detail.HasUpdates);
        Assert.Equal(2, detail.UpdatesCount);
    }

    /// <summary>
    /// 详情与列表的统计**会不一样**（同一时刻两边各算一次）。实测 709792：
    /// 列表说 593 赞 / 18427 浏览，详情说 596 / 18929。
    ///
    /// 这条不是「谁对谁错」，而是把「两边都可能给值」这个事实钉住 —— 界面上的合并策略
    /// （<c>ModStoreDetailItem.ApplyDetail</c>：详情优先、缺值时保留列表的）就是为它写的。
    /// </summary>
    [Fact]
    public void Detail_CountersDifferFromTheListEndpoint()
    {
        var detail = HiddenMod();

        Assert.NotEqual(593, detail.LikeCount);   // 列表 fixture（mod-store-subfeed）里的值
        Assert.NotEqual(18427, detail.ViewCount);
    }

    /// <summary>空串的 <c>_sDescription</c>（658343 就是这样）算「没有简介」，不能变成空串。</summary>
    [Fact]
    public void Detail_TreatsAnEmptyDescriptionAsMissing()
    {
        Assert.Null(UiMod().Description);
        Assert.Equal("Qingxiao清宵", HiddenMod().Description);
    }

    /// <summary>
    /// 正文是 HTML：必须洗成纯文本，而且 <c>&lt;br&gt;</c> 要变成真换行
    /// （否则作者的「一行一句」会粘成一段）。
    /// </summary>
    [Fact]
    public void Body_IsHtmlConvertedToPlainText()
    {
        var body = HiddenMod().Body;

        Assert.NotNull(body);
        Assert.DoesNotContain('<', body);
        Assert.DoesNotContain("&amp;", body);
        Assert.Contains("patreon", body);                    // 链接只留可见文字
        Assert.DoesNotContain("href", body);
        Assert.Equal(5, body.Split('\n').Length);            // 原 HTML 里正好 4 个 <br>
    }

    /// <summary>
    /// 658343 的正文里全是 <c>&lt;h1&gt;</c> / <c>&lt;ul&gt;&lt;li&gt;</c>：块级标签也要换行，
    /// 不然「Features:」会和第一条 <c>li</c> 粘成「Features:Edit pictures…」。
    /// </summary>
    [Fact]
    public void Body_BreaksOnBlockTagsToo()
    {
        var body = UiMod().Body;

        Assert.NotNull(body);
        Assert.DoesNotContain('<', body);
        Assert.Contains("Features:\nEdit pictures via ini customisation", body);
        Assert.Contains("Installation:\nEverything you need is contained", body);
    }

    /// <summary>
    /// 分类层级：<c>_aCategory</c> 是**最具体**那一级。
    /// <list type="bullet">
    ///   <item>709792：<c>Qingxiao</c>（父分类 Skins）→ 是角色；</item>
    ///   <item>658343：<c>UI</c>（**没有父分类**）→ 它是根分类，不是角色。</item>
    /// </list>
    /// 少了「有没有父分类」这一判，UI 类 mod 会在抽屉里挂上一个假角色「UI」。
    /// </summary>
    [Fact]
    public void Character_OnlyCountsAsACharacterWhenTheCategoryHasAParent()
    {
        Assert.Equal("Qingxiao", HiddenMod().Character);
        Assert.Equal(46596, HiddenMod().CategoryId);

        Assert.Null(UiMod().Character);
        Assert.Equal(29496, UiMod().CategoryId);   // 分类 id 照样有用（能按分类浏览）

        // 依据本身也要钉住：UI 那条**整个键都不存在**，不是给了个空对象。
        Assert.NotNull(HiddenModProfile.SuperCategory);
        Assert.Null(UiModProfile.SuperCategory);
    }

    /// <summary>
    /// 成人内容：详情端点**没有**列表侧那个 <c>_bHasContentRatings</c>，
    /// 只能靠 <c>_aContentRatings</c> 非空判定（对象形状，值是给人类看的标签）。
    /// </summary>
    [Fact]
    public void AdultContent_ComesFromTheRatingsObjectOnly()
    {
        var hidden = HiddenMod();
        Assert.Equal(["Partial Nudity", "Full Nudity"], hidden.ContentRatings);
        Assert.True(hidden.IsAdult);

        Assert.Empty(UiMod().ContentRatings);
        Assert.False(UiMod().IsAdult);

        // 「详情端点没有 _bHasContentRatings」是这条判定成立的前提，所以直接对原始 JSON 断言。
        Assert.DoesNotContain("_bHasContentRatings",
            RawFixture("mod-store-mod-profilepage-709792.sample.json"));
    }

    /// <summary>作者头像必须是本图床的 https —— 658343 用的是 GB 的默认头像 gif，照常显示。</summary>
    [Fact]
    public void AuthorAvatar_ComesFromTheImageCdn()
    {
        Assert.Equal("https://images.gamebanana.com/img/av/690cacbf2a00d.jpg",
            HiddenMod().AuthorAvatarUrl!.ToString());

        Assert.Equal("https://images.gamebanana.com/static/img/defaults/avatar.gif",
            UiMod().AuthorAvatarUrl!.ToString());
    }

    [Theory]
    [InlineData("https://images.gamebanana.com/img/av/a.jpg", true)]
    [InlineData("http://images.gamebanana.com/img/av/a.jpg", false)]      // 非 https
    [InlineData("https://images.gamebanana.com.evil.com/a.jpg", false)]   // 域名后缀混淆
    [InlineData("https://evil.example.com/a.jpg", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void AuthorAvatar_OnlyAcceptsTheImageCdn(string? avatarUrl, bool expected)
    {
        var profile = new ApiModProfile { ModId = 1, Author = new ApiAuthor { AvatarImageUrl = avatarUrl } };

        Assert.Equal(expected, ModStoreDetail.TryCreate(profile, null)!.AuthorAvatarUrl is not null);
    }

    /// <summary>时间戳缺失（DTO 上是 0）必须变成 null，不能变成 1970-01-01。</summary>
    [Fact]
    public void Detail_TreatsMissingTimestampsAsNull()
    {
        var detail = ModStoreDetail.TryCreate(new ApiModProfile { ModId = 1 }, null)!;

        Assert.Null(detail.DateAdded);
        Assert.Null(detail.DateUpdated);
    }

    /// <summary>-1 是「接口没给」的哨兵值，要变 null；0 是真实结果（真的没人下载过），要留着。</summary>
    [Fact]
    public void Counters_DistinguishTheMinusOneSentinelFromARealZero()
    {
        var missing = ModStoreDetail.TryCreate(new ApiModProfile { ModId = 1 }, null)!;

        Assert.Null(missing.DownloadCount);
        Assert.Null(missing.LikeCount);
        Assert.Null(missing.ViewCount);
        Assert.Null(missing.CommentCount);
        Assert.Null(missing.ThanksCount);
        Assert.Null(missing.UpdatesCount);

        var realZero = ModStoreDetail.TryCreate(new ApiModProfile { ModId = 1, DownloadCount = 0 }, null)!;

        Assert.Equal(0, realZero.DownloadCount);
    }

    // ---- 文件清单 ----------------------------------------------------------

    /// <summary>
    /// 普通 mod：活跃文件来自 <c>DownloadPage</c>，顺序**照抄**（作者自己排的）。
    ///
    /// 文件级的 <c>_sVersion</c> 这四个文件一个都没有，所以版本要退回 mod 级（2.0）——
    /// 否则抽屉里那行小字大半是空的，看着像没做。
    /// </summary>
    [Fact]
    public void Files_KeepTheAuthorOrderAndFallBackToTheModVersion()
    {
        var files = UiMod().Files;

        Assert.Equal(
            ["37hashupdater.rar", "chibiportraitsv2_272f9.rar", "portrait_driver_56427.rar",
                "portrait_builder_74a19.rar"],
            files.Select(f => f.FileName).ToArray());

        var update = files[0];
        Assert.Equal("1831976", update.FileId.ModFileId);
        Assert.Equal(6022, update.FileSize);
        Assert.Equal("0621fa1d9aee29ae44b4aca7bf58fef4", update.Md5Checksum);
        Assert.Equal("https://gamebanana.com/dl/1831976", update.DownloadUrl!.ToString());
        Assert.Equal("Use this to update Chisa/Mornye effect hashes", update.Description);
        Assert.Equal("2.0", update.Version);          // 文件级没有 => 退回 mod 级
        Assert.False(update.IsArchived);
        Assert.Equal(16, update.DownloadCount);
        Assert.Equal(1790768299, update.DateAdded!.Value.ToUnixTimeSeconds());
    }

    /// <summary>
    /// 隐藏 mod：<c>_aFiles</c> 键都不存在，文件**全在** <c>_aArchivedFiles</c> 里 ——
    /// 这是「只读 <c>_aFiles</c> 就会显示成没有文件」那个坑的回归锁。
    /// 这些文件仍然可下，但必须带归档标记（界面据此打「已归档」）。
    /// </summary>
    [Fact]
    public void Files_AreFoundEvenWhenOnlyTheArchivedFieldExists()
    {
        var files = HiddenMod().Files;

        Assert.Equal(["qingxiaosfw_3ef7a.zip", "qingxiao.zip"], files.Select(f => f.FileName).ToArray());

        Assert.All(files, file => Assert.True(file.IsArchived));

        // 归档文件的 _sVersion 是**有**的，直接用它（不退回 mod 级那一个 1.2）。
        Assert.Equal(["1.1", "1.2"], files.Select(f => f.Version).OfType<string>().ToArray());
        Assert.Equal("NSFW", files[1].Description);
        Assert.Null(files[0].Description);            // 这条没有 _sDescription 键
    }

    /// <summary>
    /// 两个字段可能同时有货（普通 mod 的旧版本也在归档字段里）：合并后活跃在前、归档在后，
    /// 用户挑文件时当前版本排在前面。
    /// </summary>
    [Fact]
    public void Files_MergeBothFieldsWithActiveOnesFirst()
    {
        var profile = new ApiModProfile
        {
            ModId = 1,
            Files = [ApiFile(1, "new.zip", archived: false)],
            ArchivedFiles = [ApiFile(2, "old.zip", archived: true)]
        };

        var files = ModStoreDetail.TryCreate(profile, null)!.Files;

        Assert.Equal(["new.zip", "old.zip"], files.Select(f => f.FileName).ToArray());
        Assert.Equal([false, true], files.Select(f => f.IsArchived).ToArray());
    }

    /// <summary>
    /// <c>DownloadPage</c> 取不到（请求失败 / 一条文件都没给）时回落到 <c>ProfilePage</c> 自带的那份。
    /// 实测 658343 的详情页也带着 4 条活跃文件，两条路径拿到的是同一批 id。
    /// </summary>
    [Fact]
    public void Files_FallBackToTheProfilePageWhenTheDownloadPageGivesNothing()
    {
        var withoutFilesEndpoint = ModStoreDetail.TryCreate(UiModProfile, null)!.Files;

        Assert.Equal(
            UiMod().Files.Select(f => f.FileId.ModFileId).ToArray(),
            withoutFilesEndpoint.Select(f => f.FileId.ModFileId).ToArray());

        // 隐藏 mod 也一样：它的详情页带着 _aArchivedFiles，兜底后仍然看得到文件。
        Assert.Equal(2, ModStoreDetail.TryCreate(HiddenModProfile, null)!.Files.Count);

        // 空响应（键缺失退化成空集合）同样走兜底，而不是「这个 mod 没有文件」。
        Assert.Equal(4, ModStoreDetail.TryCreate(UiModProfile, new ApiModFilesInfo())!.Files.Count);
    }

    /// <summary>没有 id 或没有文件名的记录建不出来 —— 前者下不了，后者显示了也没用。</summary>
    [Fact]
    public void File_RejectsRecordsWithoutAnIdOrAName()
    {
        Assert.Null(ModStoreFile.TryCreate(null, null, false));
        Assert.Null(ModStoreFile.TryCreate(ApiFile(0, "a.zip"), null, false));        // FileId 默认 -1 也走这条
        Assert.Null(ModStoreFile.TryCreate(ApiFile(-1, "a.zip"), null, false));
        Assert.Null(ModStoreFile.TryCreate(ApiFile(1, null), null, false));
        Assert.Null(ModStoreFile.TryCreate(new ApiModFileInfo { FileId = 1, FileName = "   " }, null, false));

        Assert.NotNull(ModStoreFile.TryCreate(ApiFile(1, "a.zip"), null, false));
    }

    /// <summary>文件级版本缺失、mod 级也没有时是 null（界面少显示一格），不是空串。</summary>
    [Fact]
    public void File_VersionIsNullWhenNeitherLevelHasOne()
    {
        Assert.Null(ModStoreFile.TryCreate(ApiFile(1, "a.zip"), null, false)!.Version);
        Assert.Equal("9.9", ModStoreFile.TryCreate(ApiFile(1, "a.zip"), " 9.9 ", false)!.Version);
        Assert.Equal("1.0", ModStoreFile.TryCreate(ApiFile(1, "a.zip", version: "1.0"), "9.9", false)!.Version);
    }

    /// <summary>文件大小缺失时是 null（界面那格直接不写），不是 -1。</summary>
    [Fact]
    public void File_SizeIsNullWhenNotProvided()
    {
        Assert.Null(ModStoreFile.TryCreate(ApiFile(1, "a.zip"), null, false)!.FileSize);   // 默认 -1
        Assert.Equal(0, ModStoreFile.TryCreate(ApiFile(1, "a.zip", size: 0), null, false)!.FileSize);
    }

    /// <summary>
    /// 下载地址来自接口，但真发请求前仍然只认 https + gamebanana.com
    /// （拼接陷阱见 <see cref="GameBananaMediaUrls"/> 的同名测试）。
    /// </summary>
    [Theory]
    [InlineData("https://gamebanana.com/dl/1", true)]
    [InlineData("https://files.gamebanana.com/dl/1", true)]
    [InlineData("http://gamebanana.com/dl/1", false)]
    [InlineData("https://gamebanana.com.evil.com/dl/1", false)]
    [InlineData("https://evil.example.com/dl/1", false)]
    [InlineData("", false)]
    public void File_DownloadUrlMustBeHttpsOnGameBanana(string downloadUrl, bool expected)
    {
        var file = ModStoreFile.TryCreate(ApiFile(1, "a.zip", downloadUrl: downloadUrl), null, false);

        Assert.Equal(expected, file!.DownloadUrl is not null);
    }

    /// <summary>预览图与列表侧同一口径：只认 https 的图床（实测 709792 有 4 张）。</summary>
    [Fact]
    public void PreviewImages_AreAllOnTheCdn()
    {
        var images = HiddenMod().PreviewImages;

        Assert.Equal(4, images.Count);
        Assert.All(images, url =>
        {
            Assert.Equal(Uri.UriSchemeHttps, url.Scheme);
            Assert.Equal("images.gamebanana.com", url.Host);
        });
    }

    // ---- 不应抛出 / 不应假装有详情 ------------------------------------------

    /// <summary>
    /// 拿不到详情页就没有详情 —— 只有文件清单是没有意义的（没有标题、没有正文，
    /// 抽屉会显示成一条空壳）。所以 <c>TryCreate</c> 必须给 null，由界面显示失败 + 重试。
    /// </summary>
    [Fact]
    public void TryCreate_NeedsAProfileWithAnId()
    {
        Assert.Null(ModStoreDetail.TryCreate(null, null));
        Assert.Null(ModStoreDetail.TryCreate(null, UiModFiles));
        Assert.Null(ModStoreDetail.TryCreate(new ApiModProfile(), UiModFiles));      // ModId 默认 -1
        Assert.Null(ModStoreDetail.TryCreate(new ApiModProfile { ModId = 0 }, UiModFiles));

        Assert.NotNull(ModStoreDetail.TryCreate(new ApiModProfile { ModId = 1 }, null));
    }

    /// <summary>畸形的分类 / 分级 / 图片一个都不能让它炸掉。</summary>
    [Fact]
    public void Detail_DegradesWithoutThrowing()
    {
        var detail = ModStoreDetail.TryCreate(new ApiModProfile
        {
            ModId = 1,
            Category = new ApiSubfeedCategory { Name = "   " },
            SuperCategory = new ApiSubfeedCategory { Name = "Skins" },
            ContentRatings = new Dictionary<string, string> { ["pn"] = "  ", ["nu"] = "Full Nudity" }
        }, null)!;

        Assert.Null(detail.Character);                        // 分类名是空白
        Assert.Equal(["Full Nudity"], detail.ContentRatings);  // 空白标签丢掉，其余保留
        Assert.True(detail.IsAdult);
        Assert.Null(detail.CategoryId);                        // 缺 _idRow => 默认 -1
        Assert.Empty(detail.PreviewImages);
        Assert.Empty(detail.Files);
    }

    // ---- 辅助 --------------------------------------------------------------

    private static ApiModFileInfo ApiFile(int id, string? name, bool archived = false, string? version = null,
        long size = -1, string? downloadUrl = "https://gamebanana.com/dl/1") => new()
        {
            FileId = id,
            FileName = name!,
            IsArchived = archived,
            Version = version,
            FileSize = size,
            DownloadUrl = downloadUrl!
        };
}