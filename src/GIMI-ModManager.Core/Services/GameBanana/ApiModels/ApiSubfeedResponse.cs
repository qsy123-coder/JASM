using System.Text.Json.Serialization;

namespace GIMI_ModManager.Core.Services.GameBanana.ApiModels;

/// <summary>
/// <c>apiv11/Game/{id}/Subfeed</c>（板块内容流）与 <c>apiv11/Util/Search/Results</c>（搜索）
/// 共用的响应外壳 —— 两者的顶层结构与记录字段一致。
///
/// 分页参数是 <c>_nPage</c>（两端点一致），页大小**固定 15 条**
/// （<c>perPage</c> / <c>_nPerpage</c> / <c>_nPerPage</c> 实测全部被忽略）。
/// </summary>
public sealed class ApiSubfeedResponse
{
    [JsonPropertyName("_aMetadata")] public ApiSubfeedMetadata? Metadata { get; init; }

    [JsonPropertyName("_aRecords")] public ApiSubfeedRecord[]? Records { get; init; }
}

public sealed class ApiSubfeedMetadata
{
    /// <summary>
    /// 当前**视图**的记录总数 —— 注意它不是板块内 mod 总数：实测同一个板块
    /// <c>_sSort=default/new</c> 报 3062，而 <c>_sSort=updated</c> 只报 1333
    /// （「最近更新」视图只覆盖有更新记录的提交）。所以 UI 不能拿它说「板块共 N 个 mod」。
    /// </summary>
    [JsonPropertyName("_nRecordCount")] public int RecordCount { get; init; } = -1;

    /// <summary>服务端实际的页大小（实测恒为 15，改不动）。</summary>
    [JsonPropertyName("_nPerpage")] public int PerPage { get; init; } = -1;

    /// <summary>true = 已经取到最后一页。</summary>
    [JsonPropertyName("_bIsComplete")] public bool IsComplete { get; init; }

    /// <summary>只有搜索接口会给（按提交类型分组的命中数）。</summary>
    [JsonPropertyName("_aSectionMatchCounts")] public ApiSectionMatchCount[]? SectionMatchCounts { get; init; }
}

/// <summary>搜索接口的分类型命中数。例如搜 <c>skin</c>：总 705，其中 Mod 277、Question 266 …</summary>
public sealed class ApiSectionMatchCount
{
    [JsonPropertyName("_sModelName")] public string? ModelName { get; init; }

    [JsonPropertyName("_nMatchCount")] public int MatchCount { get; init; } = -1;
}

/// <summary>
/// 一条列表记录。
///
/// ⚠️ 搜索接口返回的是**混合类型**（实测同一页里混着 Mod / Question / Request / Poll …），
/// 且不同类型字段集不同（Request 会多出 <c>_nBounty</c>、<c>_sResolution</c> 之类）。
/// 所以这里只映射各类型共有的字段，并在 <see cref="ModelName"/> 上做过滤 ——
/// 不要假设每条记录都是 Mod。
/// </summary>
public sealed class ApiSubfeedRecord
{
    /// <summary><c>_sModelName</c> 里代表「可下载的 mod」的取值，也是 <c>_csvModelInclusions</c> 的筛选值。</summary>
    public const string ModModelName = "Mod";

    [JsonPropertyName("_idRow")] public int ModId { get; init; } = -1;

    /// <summary>提交类型：<c>Mod</c> / <c>Concept</c> / <c>Poll</c> / <c>Question</c> / <c>Request</c> …</summary>
    [JsonPropertyName("_sModelName")] public string? ModelName { get; init; }

    [JsonPropertyName("_sName")] public string? Name { get; init; }
    [JsonPropertyName("_sProfileUrl")] public string? ProfileUrl { get; init; }
    [JsonPropertyName("_sVersion")] public string? Version { get; init; }

    [JsonPropertyName("_tsDateAdded")] public long DateAdded { get; init; }
    [JsonPropertyName("_tsDateUpdated")] public long DateUpdated { get; init; }

    [JsonPropertyName("_bHasFiles")] public bool HasFiles { get; init; }
    [JsonPropertyName("_bIsObsolete")] public bool IsObsolete { get; init; }

    /// <summary>
    /// 列表里**唯一**可靠的成人内容标志。实测服务端过滤参数 <c>_bShowNsfw=false</c> 无效
    /// （记录数 6090 → 6090，纹丝不动），所以「默认隐藏」只能在客户端按这个字段过滤。
    ///
    /// 另注意：详情页正文里出现的 "adult/NSFW content" 是 <c>_aLicenseChecklist</c> 的许可条款
    /// 文案，不是标志位，别拿它判。
    /// </summary>
    [JsonPropertyName("_bHasContentRatings")] public bool HasContentRatings { get; init; }

    /// <summary>实测部分记录为空（不是 0，是没有值），所以可空。</summary>
    [JsonPropertyName("_nLikeCount")] public int? LikeCount { get; init; }

    [JsonPropertyName("_nViewCount")] public int? ViewCount { get; init; }

    [JsonPropertyName("_aRootCategory")] public ApiSubfeedCategory? RootCategory { get; init; }

    [JsonPropertyName("_aSubmitter")] public ApiAuthor? Author { get; init; }

    [JsonPropertyName("_aPreviewMedia")] public ApiImagesRoot? PreviewMedia { get; init; }

    /// <summary>
    /// 标签。**两个端点行为不同**：Subfeed 恒返回空数组（实测鸣潮板块 3/3 全空），
    /// Search 会返回真值（如 <c>"jinhsi: manuka"</c>）—— 所以它不能作为浏览视图的筛选维度。
    /// 形状上两端都是数组，不会是 null 也不是标量。
    /// </summary>
    [JsonPropertyName("_aTags")] public string[]? Tags { get; init; }
}

/// <summary>
/// 分类。
///
/// ⚠️ 实测**没有** <c>_idRow</c>，id 只能从 <c>_sProfileUrl</c>（<c>…/mods/cats/29496</c>）末段抠。
/// 鸣潮板块的分类**就是角色名**，所以「按角色筛选」落在 <c>_sName</c> 上。
/// </summary>
public sealed class ApiSubfeedCategory
{
    [JsonPropertyName("_sName")] public string? Name { get; init; }
    [JsonPropertyName("_sProfileUrl")] public string? ProfileUrl { get; init; }
    [JsonPropertyName("_sIconUrl")] public string? IconUrl { get; init; }
}