namespace GIMI_ModManager.Core.ModMarket;

/// <summary>角色键里的特殊取值（与 Supabase 侧的既有约定一致）。</summary>
public static class MarketCategoryKeys
{
    /// <summary>「全部」——不是角色，仅用于让上层表达"不按角色过滤"。</summary>
    public const string All = "all";

    /// <summary>「角色皮肤」兜底分类：除 UI 与 Other/Misc 之外的一切。</summary>
    public const string Skins = "Skins";

    public const string OtherMisc = "Other/Misc";

    public const string Ui = "UI";
}

/// <summary>
/// 本地查询的排序键。刻意**不含 UpdatedAtDesc**：快照没有 updated_at 这一列，
/// 且 UI 也走不到那个分支（"最近更新"与"最新"都映射到 created_at.desc）。
/// </summary>
public enum MarketSortKey
{
    TitleAsc,

    CreatedAtDesc,

    DownloadsDesc,

    LikesDesc,

    ViewsDesc
}

/// <summary>与 ModMarketService 的 PostgREST 查询参数一一对应的本地查询条件。</summary>
public sealed record MarketQuery
{
    /// <summary>角色名。"all"/空白 = 不过滤；"Skins" = 排除 UI 与 Other/Misc；其余按 Ordinal 精确匹配。</summary>
    public string? Character { get; init; }

    /// <summary>搜索词。按 LIKE 模式匹配（见 <see cref="MarketLikePattern"/>），不是字面量子串。</summary>
    public string? Search { get; init; }

    public bool? Nsfw { get; init; }

    /// <summary>对应 PostgREST 的 download_url=not.is.null（空串也算命中）。</summary>
    public bool DirectDownloadOnly { get; init; }

    /// <summary>
    /// 对应 PostgREST 的 created_at=gte.{yyyy-MM-dd}。传时间戳即可，
    /// 引擎会自行截断到 UTC 日的零点（线上就是这么比的）。
    /// </summary>
    public DateTime? CreatedOnOrAfterUtc { get; init; }

    public MarketSortKey Sort { get; init; } = MarketSortKey.TitleAsc;

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 24;
}

/// <summary>一页结果。本地模式下 <see cref="TotalCount"/> 永远是精确值，不使用线上那个 int.MaxValue 哨兵。</summary>
public sealed record MarketPage<T>(IReadOnlyList<T> Items, int TotalCount, int Offset);
