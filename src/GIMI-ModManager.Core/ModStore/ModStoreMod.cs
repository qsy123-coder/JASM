using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;
using GIMI_ModManager.Core.Services.GameBanana.Models;

namespace GIMI_ModManager.Core.ModStore;

/// <summary>
/// 商店里的一条 mod —— GameBanana 列表记录到域模型的映射（做法沿用 <c>ModPageInfo</c> 的
/// 「构造函数映射」风格，但改由 <see cref="TryCreate"/> 出场，因为它得能拒绝非 Mod 记录）。
///
/// 只映射**列表接口确实会给**的字段。三处容易踩空的地方：
/// <list type="bullet">
///   <item><c>_nDownloadCount</c> 只在详情页 ProfilePage 有 —— 卡片上不要显示下载量，
///         列表里根本没有这个值。</item>
///   <item><c>_aTags</c> 只在**搜索**接口有值，Subfeed（浏览）恒为空数组 —— 所以它不能作为
///         浏览视图的筛选维度，要筛选走 <see cref="Category"/>。</item>
///   <item><c>_aRootCategory</c> 没有 <c>_idRow</c>，见 <see cref="ModStoreCategory.Id"/>。</item>
/// </list>
/// </summary>
public sealed class ModStoreMod
{
    private ModStoreMod(ApiSubfeedRecord record)
    {
        Id = new GbModId(record.ModId);
        Name = record.Name?.Trim() ?? string.Empty;
        AuthorName = NullIfBlank(record.Author?.AuthorName);
        ModPageUrl = TryCreateModPageUrl(record.ProfileUrl);
        PreviewImages = GameBananaMediaUrls.GetPreviewImages(record.PreviewMedia);
        Category = ModStoreCategory.FromApi(record.RootCategory);
        Version = NullIfBlank(record.Version);
        LikeCount = record.LikeCount;
        ViewCount = record.ViewCount;
        HasFiles = record.HasFiles;
        IsObsolete = record.IsObsolete;
        IsAdult = record.HasContentRatings;
        Tags = record.Tags is { Length: > 0 } tags
            ? Array.AsReadOnly(tags.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray())
            : [];
        DateAdded = ToDateTimeOffset(record.DateAdded);
        DateUpdated = ToDateTimeOffset(record.DateUpdated);
    }

    /// <summary>
    /// 映射一条列表记录；**不是 Mod 就返回 null**。
    ///
    /// 搜索接口返回的是混合类型（实测同一页里混着 Mod / Request / Question / Poll …），
    /// 所以必须逐条判断，不能假设整页都是 Mod。缺少 <c>_idRow</c>（无法定位到具体 mod）的
    /// 残缺记录同样拒掉。
    /// </summary>
    public static ModStoreMod? TryCreate(ApiSubfeedRecord? record)
    {
        if (record is null)
            return null;

        if (!string.Equals(record.ModelName, ApiSubfeedRecord.ModModelName, StringComparison.OrdinalIgnoreCase))
            return null;

        if (record.ModId <= 0)
            return null;

        return new ModStoreMod(record);
    }

    public GbModId Id { get; }

    /// <summary>标题。接口缺 <c>_sName</c> 时是空串（不是 null），省得卡片每次都判空。</summary>
    public string Name { get; }

    public string? AuthorName { get; }

    /// <summary>mod 详情页地址；不是 https 的 gamebanana.com 时为 null。</summary>
    public Uri? ModPageUrl { get; }

    public IReadOnlyList<Uri> PreviewImages { get; }

    public ModStoreCategory? Category { get; }

    public string? Version { get; }

    public int? LikeCount { get; }

    public int? ViewCount { get; }

    public bool HasFiles { get; }

    public bool IsObsolete { get; }

    /// <summary>
    /// 成人内容。列表里**唯一**可靠的信号 —— 服务端过滤参数实测无效，默认隐藏只能在客户端按它过滤。
    /// </summary>
    public bool IsAdult { get; }

    /// <summary>
    /// 标签。**浏览（Subfeed）恒为空**，只有搜索接口会给值 —— 卡片上别给它留位置，
    /// 浏览视图里那个位置永远是空的。
    /// </summary>
    public IReadOnlyList<string> Tags { get; }

    public DateTimeOffset? DateAdded { get; }

    public DateTimeOffset? DateUpdated { get; }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Uri? TryCreateModPageUrl(string? profileUrl)
    {
        if (string.IsNullOrWhiteSpace(profileUrl) || !Uri.TryCreate(profileUrl, UriKind.Absolute, out var url))
            return null;

        if (url.Scheme != Uri.UriSchemeHttps ||
            !url.Host.Equals("gamebanana.com", StringComparison.OrdinalIgnoreCase))
            return null;

        return url;
    }

    /// <summary>
    /// Unix 秒 → <see cref="DateTimeOffset"/>。
    ///
    /// 字段缺失时 DTO 上是 <c>0</c>，而 <c>FromUnixTimeSeconds(0)</c> 会给出 1970-01-01 ——
    /// 那是个**看着合法的错日期**，比 null 危险得多，所以 &lt;= 0 一律按「没有」处理。
    /// 超出 <c>DateTimeOffset</c> 表示范围的脏数据同样按「没有」处理，不让它把整页拖崩。
    /// </summary>
    private static DateTimeOffset? ToDateTimeOffset(long unixSeconds)
    {
        if (unixSeconds <= 0 || unixSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}