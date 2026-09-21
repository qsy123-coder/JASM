using System.Globalization;

namespace GIMI_ModManager.Core.ModMarket;

/// <summary>
/// 降级快照路径的筛选 / 排序 / 分页引擎 —— 逐条复刻 ModMarketService 里 PostgREST 的查询语义。
/// 纯函数、无 IO，故放在 Core 里以便测试覆盖（JASM.Tests 只引用 Core）。
/// </summary>
public static class MarketQueryEngine
{
    /// <summary>
    /// 标题排序比较器。DB 是 ICU en-US（datlocprovider='i'），.NET 也是 ICU，但版本与裁剪可能不同，
    /// 个别标题的先后或与线上不一致 —— 已知且可接受（只影响某个 mod 落在第几页）。
    /// 刻意不用 CurrentCulture：那会跟着用户的机器语言变。
    /// </summary>
    private static readonly StringComparer TitleComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), ignoreCase: false);

    public static MarketPage<T> Execute<T>(IReadOnlyList<T> rows, MarketQuery query) where T : IMarketModRow
    {
        var filtered = Filter(rows, query);
        var sorted = Sort(filtered, query.Sort);

        var pageSize = Math.Max(query.PageSize, 0);
        var page = Math.Max(query.Page, 1);
        var offset = (page - 1) * pageSize;

        return new MarketPage<T>(sorted.Skip(offset).Take(pageSize).ToList(), sorted.Count, offset);
    }

    private static List<T> Filter<T>(IReadOnlyList<T> rows, MarketQuery query) where T : IMarketModRow
    {
        var pattern = string.IsNullOrWhiteSpace(query.Search) ? null : MarketLikePattern.Create(query.Search);
        var cutoff = ToUtcDayStart(query.CreatedOnOrAfterUtc);
        var result = new List<T>(rows.Count);

        foreach (var row in rows)
        {
            // 快照本就只含已发布行，这里是防御性过滤。
            // ⚠️ 刻意不检查 IsAvailable：快照没有这一列，bool 恒为 false，过滤即整页空白。
            if (!row.IsPublished) continue;

            if (!MatchesCharacter(row, query.Character)) continue;

            if (query.Nsfw.HasValue && row.Nsfw != query.Nsfw.Value) continue;

            // 对应 download_url=not.is.null。空串在 Postgres 里不是 null，同样命中 ——
            // 用 IsNullOrEmpty 会把 76 行缩到 2 行。
            if (query.DirectDownloadOnly && row.DownloadUrl is null) continue;

            if (cutoff.HasValue && AsUtc(row.CreatedAt) < cutoff.Value) continue;

            if (pattern is not null && !pattern.IsMatch(row.Title)) continue;

            result.Add(row);
        }

        return result;
    }

    private static bool MatchesCharacter<T>(T row, string? character) where T : IMarketModRow
    {
        if (string.IsNullOrWhiteSpace(character) || character == MarketCategoryKeys.All) return true;

        if (character == MarketCategoryKeys.Skins)
        {
            return !string.Equals(row.Character, MarketCategoryKeys.Ui, StringComparison.Ordinal)
                   && !string.Equals(row.Character, MarketCategoryKeys.OtherMisc, StringComparison.Ordinal);
        }

        // PostgREST 的 eq 是大小写敏感的精确匹配。
        return string.Equals(row.Character, character, StringComparison.Ordinal);
    }

    private static List<T> Sort<T>(List<T> rows, MarketSortKey sortKey) where T : IMarketModRow
    {
        // ThenBy(Id) 让同分行的顺序确定。线上没有次级排序键（实测 created_at 有 124 组重复值），
        // 同分行的顺序其实是未定义的 —— 这里比线上更确定，属于改进而非等价（等价物是未定义行为，无法复刻）。
        return sortKey switch
        {
            MarketSortKey.CreatedAtDesc => rows.OrderByDescending(r => AsUtc(r.CreatedAt)).ThenBy(r => r.Id).ToList(),
            MarketSortKey.DownloadsDesc => rows.OrderByDescending(r => r.DownloadsCount).ThenBy(r => r.Id).ToList(),
            MarketSortKey.LikesDesc => rows.OrderByDescending(r => r.LikesCount).ThenBy(r => r.Id).ToList(),
            MarketSortKey.ViewsDesc => rows.OrderByDescending(r => r.Views).ThenBy(r => r.Id).ToList(),
            _ => rows.OrderBy(r => r.Title, TitleComparer).ThenBy(r => r.Id).ToList()
        };
    }

    /// <summary>
    /// 线上是 <c>created_at=gte.{yyyy-MM-dd}</c>，Postgres 按日期比较，即"该日 UTC 零点起"
    /// （库会话时区实测 = UTC）。所以这里也必须截断到日 —— 直接用时间戳会比线上更严格。
    /// </summary>
    private static DateTime? ToUtcDayStart(DateTime? value)
    {
        if (value is not { } raw) return null;
        return DateTime.SpecifyKind(AsUtc(raw).Date, DateTimeKind.Utc);
    }

    /// <summary>
    /// 带偏移的时间戳（快照与 PostgREST 都是 "…+00:00"）会被 System.Text.Json 解析成 Kind=Local，
    /// 直接与 Kind=Utc 的值比 Ticks 会恒错一个时区偏移。比较前必须归一。
    /// 无 Kind 的值按本地时间处理 —— 我们的数据恒带偏移，走不到这个分支。
    /// </summary>
    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
}
