namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// 商店一次取数的结果。
///
/// 与市场侧的 <c>ModMarketResult</c> 分开：那个带快照降级信息（Supabase 专用），
/// 商店没有快照这回事。
/// </summary>
public sealed class ModStoreResult
{
    private ModStoreResult(IReadOnlyList<ModStoreItem> items, int totalCount, bool hasMore, int nextPage,
        string? errorMessage)
    {
        Items = items;
        TotalCount = totalCount;
        HasMore = hasMore;
        NextPage = nextPage;
        ErrorMessage = errorMessage;
    }

    public IReadOnlyList<ModStoreItem> Items { get; }

    /// <summary>当前视图的总数（口径见 Core 的 <c>ModStorePage.TotalCount</c> —— 随排序视图变）。</summary>
    public int TotalCount { get; }

    public bool HasMore { get; }

    /// <summary>
    /// 下一次「加载更多」该请求的页码。
    ///
    /// 这一项不能由调用方自己 +1：服务层会因为「整页都被过滤掉（NSFW 隐藏 / 混合类型）」
    /// 而一次连取好几页，只有它知道游标停在哪。
    /// </summary>
    public int NextPage { get; }

    /// <summary>非 null = 这一页取失败了。UI 必须显式判它，否则失败会被当成「成功但 0 条」。</summary>
    public string? ErrorMessage { get; }

    public static ModStoreResult Success(IReadOnlyList<ModStoreItem> items, int totalCount, bool hasMore, int nextPage) =>
        new(items, totalCount, hasMore, nextPage, null);

    public static ModStoreResult Failure(string message) => new([], 0, false, 1, message);
}