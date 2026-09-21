namespace GIMI_ModManager.Core.ModMarket;

/// <summary>
/// Mod 市场「降级快照」路径在本地复刻 PostgREST 语义所需的最小行视图。
/// WinUI 侧的 <c>ModMarketMod</c> 直接隐式实现本接口，两条路径共用同一批对象、无需映射。
/// </summary>
public interface IMarketModRow
{
    Guid Id { get; }

    string Title { get; }

    string Character { get; }

    bool IsPublished { get; }

    bool Nsfw { get; }

    /// <summary>
    /// 注意语义：**空串不是 null**。线上过滤条件是 download_url=not.is.null，
    /// 空串同样命中（实测 74 空串 + 2 真链接 = 76 行命中）。用 IsNullOrEmpty 会变成 2。
    /// </summary>
    string? DownloadUrl { get; }

    int Views { get; }

    int LikesCount { get; }

    int DownloadsCount { get; }

    DateTime CreatedAt { get; }

    // 刻意不含 IsAvailable：快照没有这一列，反序列化后 bool 恒为 false，
    // 一旦照着线上的 is_available=eq.true 去过滤，结果就是整页空白。
}
