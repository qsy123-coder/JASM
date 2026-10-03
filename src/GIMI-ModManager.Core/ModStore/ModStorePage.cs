using GIMI_ModManager.Core.Services.GameBanana.ApiModels;

namespace GIMI_ModManager.Core.ModStore;

/// <summary>
/// 商店的一页结果。
///
/// 解析**绝不抛异常**：畸形或缺字段的响应一律退化成一页空结果，由调用方决定显示空态还是重试
/// （沿用 <c>AppUpdateReleaseResolver</c> 的既有风格 —— 远端给什么都不能让客户端崩）。
/// </summary>
public sealed class ModStorePage
{
    /// <summary>
    /// 服务端固定每页 15 条：<c>perPage</c> / <c>_nPerpage</c> / <c>_nPerPage</c> 实测**全部被忽略**
    /// （要 3 条照样给 15 条）。所以这个值只是「拿不到 <c>_nPerpage</c> 时的兜底」，
    /// 客户端**不能**指望翻页凑出别的页大小。
    /// </summary>
    public const int DefaultPageSize = 15;

    private ModStorePage(IReadOnlyList<ModStoreMod> items, int totalCount, bool isComplete, int pageSize,
        int rawRecordCount)
    {
        Items = items;
        TotalCount = totalCount;
        IsComplete = isComplete;
        PageSize = pageSize;
        RawRecordCount = rawRecordCount;
    }

    /// <summary>空页。请求失败或响应畸形时用它 —— 它同时意味着 <see cref="HasMore"/> 为 false。</summary>
    public static ModStorePage Empty { get; } = new([], 0, true, DefaultPageSize, 0);

    /// <summary>已过滤出 Mod 的条目。</summary>
    public IReadOnlyList<ModStoreMod> Items { get; }

    /// <summary>
    /// 总数。**口径是「当前视图的 Mod 数」，不是板块总数**：
    /// 搜索接口下取分类型命中数的 Mod 项（实测搜 <c>skin</c>：总 705、其中 Mod 277），
    /// 否则取 <c>_nRecordCount</c> —— 而后者随排序变（实测同一板块 default/new 报 3062、
    /// updated 只报 1333），所以 UI 别把它写成「板块共 N 个 mod」。
    /// </summary>
    public int TotalCount { get; }

    /// <summary>服务端说已经取到最后一页。</summary>
    public bool IsComplete { get; }

    public int PageSize { get; }

    /// <summary>过滤前的原始条数。调用方判断「服务端是不是真没货了」时看它，别看 <see cref="Items"/>。</summary>
    public int RawRecordCount { get; }

    /// <summary>
    /// 还有下一页。依据是服务端的 <c>_bIsComplete</c>，**不是** <see cref="Items"/> 的数量 ——
    /// 一页 15 条里可能混着非 Mod 记录（搜索接口就是混合类型），过滤完剩 0 条但后面还有货。
    /// </summary>
    public bool HasMore => !IsComplete;

    /// <summary>响应 → 一页结果。任何字段缺失都不抛。</summary>
    public static ModStorePage FromApi(ApiSubfeedResponse? response)
    {
        if (response is null)
            return Empty;

        var records = response.Records ?? [];
        if (records.Length == 0)
            return new ModStorePage([], ResolveTotal(response.Metadata, 0), response.Metadata?.IsComplete ?? true,
                ResolvePageSize(response.Metadata), 0);

        List<ModStoreMod> items = [];
        foreach (var record in records)
        {
            if (ModStoreMod.TryCreate(record) is { } mod)
                items.Add(mod);
        }

        return new ModStorePage(items, ResolveTotal(response.Metadata, items.Count),
            // 元数据缺失时按「已到底」处理：反过来会让翻页器在拿不到元数据时无限往下翻。
            response.Metadata?.IsComplete ?? true, ResolvePageSize(response.Metadata), records.Length);
    }

    private static int ResolveTotal(ApiSubfeedMetadata? metadata, int fallback)
    {
        if (metadata is null)
            return fallback;

        // 搜索接口的 _nRecordCount 是**全类型**总数，直接当 mod 数用会虚高（skin 的 705 vs 277）。
        // 优先取分类型命中里 Mod 那一项。
        var modMatch = metadata.SectionMatchCounts?
            .FirstOrDefault(c => string.Equals(c.ModelName, ApiSubfeedRecord.ModModelName,
                StringComparison.OrdinalIgnoreCase));

        if (modMatch is { MatchCount: >= 0 })
            return modMatch.MatchCount;

        return metadata.RecordCount >= 0 ? metadata.RecordCount : fallback;
    }

    private static int ResolvePageSize(ApiSubfeedMetadata? metadata) =>
        metadata is { PerPage: > 0 } ? metadata.PerPage : DefaultPageSize;
}