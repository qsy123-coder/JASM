using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;
using GIMI_ModManager.Core.Services.GameBanana.Models;

namespace GIMI_ModManager.Core.ModStore;

/// <summary>
/// 商店里一条 mod 的**详情**（抽屉的内容）—— 由两个端点合起来映射：
/// <c>Mod/{id}/ProfilePage</c>（简介、正文、统计、版本、分级）+
/// <c>Mod/{id}/DownloadPage</c>（文件清单）。
///
/// 为什么两个端点都要（ProfilePage 其实也带文件）：文件清单以
/// <c>DownloadPage</c> 为准，因为**下载**走的是它（<c>GameBananaCoreService</c> 查的就是
/// <c>_aFiles</c>），两边对不上就会出现「详情里列出的文件下不了」；而 ProfilePage 那份
/// 只当兜底 —— 真有 mod 在那边一个文件都不给（实测 709792，<c>_aFiles</c> 键缺失）。
/// 反过来 <c>DownloadPage</c> 又不给下载量/正文/分级，所以缺哪个都不行。
///
/// 三处刻意保留「不确定」的地方（都不要在 UI 上假装确定）：
/// <list type="bullet">
///   <item>统计数字可空 —— 接口没给时是 null，不是 0（0 是「真的没有人看过」）；</item>
///   <item><see cref="Body"/> 是 HTML 洗出来的纯文本，**不保留**加粗/链接；</item>
///   <item>成人内容是 <c>_aContentRatings</c> 非空判定的（详情端点**没有**列表侧那个
///         <c>_bHasContentRatings</c> 字段，实测键都不存在）。</item>
/// </list>
/// </summary>
public sealed class ModStoreDetail
{
    private ModStoreDetail(ApiModProfile profile, ApiModFilesInfo? filesInfo)
    {
        Id = new GbModId(profile.ModId);
        Name = profile.ModName?.Trim() ?? string.Empty;

        AuthorName = NullIfBlank(profile.Author?.AuthorName);
        AuthorAvatarUrl = TryCreateImageUrl(profile.Author?.AvatarImageUrl);
        AuthorProfileUrl = TryCreateProfileUrl(profile.Author?.ProfileUrl);

        Description = GameBananaHtml.ToPlainText(profile.Description);
        Body = GameBananaHtml.ToPlainText(profile.Text);

        // _aCategory 是**最具体**的那一级，只有它带 _idRow（比列表侧的 _aSubCategory 可靠，
        // 那条可能整个键都没有）。但它也可能是根分类本身 —— 实测 UI 类 mod 的 _aCategory
        // 就是「UI」，那时 _aSuperCategory 整个键都不存在。**没有父分类 = 它不是角色**，
        // 少了这一判，抽屉会在 UI 类 mod 上显示一个假的角色「UI」。
        Character = profile.SuperCategory is null ? null : NullIfBlank(profile.Category?.Name);
        CategoryId = profile.Category is { Id: > 0 } category ? category.Id : null;

        Version = NullIfBlank(profile.Version);

        DownloadCount = NullIfNegative(profile.DownloadCount);
        LikeCount = NullIfNegative(profile.LikeCount);
        ViewCount = NullIfNegative(profile.ViewCount);
        CommentCount = NullIfNegative(profile.PostCount);
        ThanksCount = NullIfNegative(profile.ThanksCount);

        DateAdded = ToDateTimeOffset(profile.DateAdded);
        DateUpdated = ToDateTimeOffset(profile.DateUpdated);

        IsObsolete = profile.IsObsolete;
        HasUpdates = profile.HasUpdates;
        UpdatesCount = NullIfNegative(profile.UpdatesCount);

        ContentRatings = profile.ContentRatings is { Count: > 0 } ratings
            ? ratings.Values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray()
            : [];

        PreviewImages = GameBananaMediaUrls.GetPreviewImages(profile.PreviewMedia);

        Files = BuildFiles(filesInfo, Version) is { Count: > 0 } fromDownloadPage
            ? fromDownloadPage
            : BuildFiles(profile, Version);
    }

    /// <summary>
    /// 合并两个端点；缺 <c>ProfilePage</c>（拿不到 id / 整个响应是 null）就当详情拿不到 ——
    /// 只有文件清单是没有意义的。
    /// </summary>
    public static ModStoreDetail? TryCreate(ApiModProfile? profile, ApiModFilesInfo? filesInfo)
    {
        if (profile is null || profile.ModId <= 0)
            return null;

        return new ModStoreDetail(profile, filesInfo);
    }

    public GbModId Id { get; }

    /// <summary>标题；接口缺 <c>_sName</c> 时是空串（与列表侧同口径）。</summary>
    public string Name { get; }

    public string? AuthorName { get; }

    /// <summary>作者头像；非 https / 非本图床时为 null（头像地址来自用户资料，同样不可信）。</summary>
    public Uri? AuthorAvatarUrl { get; }

    public Uri? AuthorProfileUrl { get; }

    /// <summary>一句话简介，已洗成纯文本；没有这段内容时为 null。</summary>
    public string? Description { get; }

    /// <summary>正文，已洗成纯文本（原文是 HTML）。</summary>
    public string? Body { get; }

    /// <summary>
    /// 角色名：<c>_aCategory._sName</c>，**但只在这条分类真有父分类时**才认。
    /// UI 类 mod（自己就挂在根分类下，<c>_aSuperCategory</c> 键不存在）为 null ——
    /// 否则卡片上那个角色标签会写成「UI」。
    /// </summary>
    public string? Character { get; }

    /// <summary>分类 id，可用于按分类浏览（<c>Mod/Index?_aFilters[Generic_Category]</c>）。</summary>
    public int? CategoryId { get; }

    public string? Version { get; }

    public int? DownloadCount { get; }
    public int? LikeCount { get; }
    public int? ViewCount { get; }
    public int? CommentCount { get; }
    public int? ThanksCount { get; }

    public DateTimeOffset? DateAdded { get; }
    public DateTimeOffset? DateUpdated { get; }

    /// <summary>作者标记为过时（游戏新版本已经不支持）。</summary>
    public bool IsObsolete { get; }

    public bool HasUpdates { get; }

    /// <summary>更新次数；接口没给时为 null。**注意它不是「有几个文件」**。</summary>
    public int? UpdatesCount { get; }

    /// <summary>内容分级的**标签**（如 <c>Partial Nudity</c>），键是缩写所以不要。空 = 无分级。</summary>
    public IReadOnlyList<string> ContentRatings { get; }

    /// <summary>
    /// 成人内容。判定依据是 <see cref="ContentRatings"/> 非空 —— 这是**详情**唯一的信号，
    /// 列表侧那个 <c>_bHasContentRatings</c> 在这个端点不存在。
    /// </summary>
    public bool IsAdult => ContentRatings.Count > 0;

    public IReadOnlyList<Uri> PreviewImages { get; }

    /// <summary>
    /// 文件清单：活跃文件在前、归档文件在后，各自**保持接口给的顺序**
    /// （实测活跃那份是作者自己排的序、归档那份按时间升序，都不重排 —— 重排过的顺序
    /// 用户对不上作者写在正文里的「先装 A 再装 B」）。
    /// 来源以 <c>DownloadPage</c> 为准，那份一条都给不出时才回落到 <c>ProfilePage</c> 自带的。
    /// </summary>
    public IReadOnlyList<ModStoreFile> Files { get; }

    /// <summary>文件清单主要来源：<c>Mod/{id}/DownloadPage</c>。</summary>
    private static IReadOnlyList<ModStoreFile> BuildFiles(ApiModFilesInfo? filesInfo, string? modVersion) =>
        BuildFiles(filesInfo?.Files, filesInfo?.ArchivedFiles, modVersion);

    /// <summary>兜底来源：<c>ProfilePage</c> 自带的那份（只在 DownloadPage 一条都没给出时用）。</summary>
    private static IReadOnlyList<ModStoreFile> BuildFiles(ApiModProfile profile, string? modVersion) =>
        BuildFiles(profile.Files, profile.ArchivedFiles, modVersion);

    /// <summary>
    /// 两个数组合并成一个清单：活跃在前、归档在后（归档的仍然可下，界面会打标记）。
    ///
    /// ⚠️ 只读 <c>_aFiles</c> 会把「被隐藏的 mod」看成没有文件 —— 实测 mod 709792
    /// （<c>_sInitialVisibility=hide</c>）的文件**全在** <c>_aArchivedFiles</c> 里，
    /// <c>_aFiles</c> 那个键根本不存在。
    /// </summary>
    private static IReadOnlyList<ModStoreFile> BuildFiles(
        ICollection<ApiModFileInfo>? activeFiles, ICollection<ApiModFileInfo>? archivedFiles, string? modVersion)
    {
        List<ModStoreFile> files = [];

        // `?? []` 不只是防御：DTO 上声明为非空集合，但 JSON 里显式为 null 时
        // System.Text.Json 会把属性设成 null（声明管不住运行时）。
        foreach (var file in activeFiles ?? [])
            Add(file, fromArchivedList: false);

        foreach (var file in archivedFiles ?? [])
            Add(file, fromArchivedList: true);

        return files.AsReadOnly();

        void Add(ApiModFileInfo apiFile, bool fromArchivedList)
        {
            if (ModStoreFile.TryCreate(apiFile, modVersion, fromArchivedList) is { } file)
                files.Add(file);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>接口用 -1 表示「没有这个值」，转成 null —— 0 是真实结果，要留着。</summary>
    private static int? NullIfNegative(int value) => value < 0 ? null : value;

    private static Uri? TryCreateProfileUrl(string? profileUrl)
    {
        if (string.IsNullOrWhiteSpace(profileUrl) || !Uri.TryCreate(profileUrl, UriKind.Absolute, out var url))
            return null;

        if (url.Scheme != Uri.UriSchemeHttps || !IsGameBananaHost(url.Host))
            return null;

        return url;
    }

    /// <summary>头像走的是 images.gamebanana.com，校验口径与预览图一致。</summary>
    private static Uri? TryCreateImageUrl(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || !Uri.TryCreate(imageUrl, UriKind.Absolute, out var url))
            return null;

        if (url.Scheme != Uri.UriSchemeHttps ||
            !url.Host.Equals("images.gamebanana.com", StringComparison.OrdinalIgnoreCase))
            return null;

        return url;
    }

    private static bool IsGameBananaHost(string host) =>
        host.Equals("gamebanana.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".gamebanana.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Unix 秒 → <see cref="DateTimeOffset"/>；&lt;= 0 与超范围都按「没有」处理
    /// （<c>FromUnixTimeSeconds(0)</c> 会给出 1970-01-01 这种**看着合法的错日期**）。
    /// </summary>
    private static DateTimeOffset? ToDateTimeOffset(long unixSeconds)
    {
        if (unixSeconds <= 0 || unixSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}