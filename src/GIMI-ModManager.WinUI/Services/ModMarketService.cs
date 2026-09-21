using System.Linq;
using System.Text.Json;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.ModMarket;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.Services.ModMarket;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services;

public class ModMarketService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly IGameService _gameService;
    private readonly ModMarketSnapshotService _snapshotService;

    // 网格/详情面板实际用到的字段。description 等大字段(单行好几 KB)在列表里不拉,
    // 点开详情时再按 id 单独拉一次(见 GetModByIdAsync),避免 24 个卡片每行都背 description 拖慢首屏。
    private static readonly string GridSelect =
        "id,title,character,images,download_url,nsfw,views,likes_count,comments_count,downloads_count,drive_links,created_at";

    // 分类计数按角色分组需 1000/页全扫(~4s),而计数变化很慢,带短 TTL 缓存避免每次进市场/切换重扫。
    private static readonly TimeSpan CategoryCacheTtl = TimeSpan.FromMinutes(10);
    private List<ModMarketCategory>? _categoryCache;
    private DateTime _categoryCacheAt;

    public ModMarketService(
        IHttpClientFactory httpClientFactory,
        IOptions<ModMarketOptions> options,
        ILogger logger,
        IGameService gameService,
        ModMarketSnapshotService snapshotService)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger.ForContext<ModMarketService>();
        _gameService = gameService;
        _snapshotService = snapshotService;
    }

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient("Supabase");
    }

    public async Task<int> GetModCountAsync(
        string? character = null,
        string? contentFilter = null,
        CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient();
            var filters = new List<string> { "is_published=eq.true", "is_available=eq.true" };

            if (!string.IsNullOrWhiteSpace(character) && character != "all")
            {
                if (character == "Skins")
                {
                    filters.Add("character=not.eq.UI");
                    filters.Add("character=not.eq.Other/Misc");
                }
                else
                {
                    filters.Add($"character=eq.{Uri.EscapeDataString(character)}");
                }
            }
            if (contentFilter == "SFW")
                filters.Add("nsfw=eq.false");
            else if (contentFilter == "NSFW")
                filters.Add("nsfw=eq.true");
            // "All" and "Blur" → no nsfw filter

            var request = new HttpRequestMessage(HttpMethod.Get,
                $"mods?{string.Join("&", filters)}&limit=0");
            request.Headers.Add("Prefer", "count=exact");

            var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            if (response.Headers.TryGetValues("Content-Range", out var rangeValues))
            {
                var rangeValue = rangeValues.FirstOrDefault();
                if (rangeValue != null && rangeValue.Contains('/'))
                {
                    var parts = rangeValue.Split('/');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var total))
                        return total;
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GetModCountAsync failed");
            return 0;
        }
    }

    public async Task<IReadOnlyList<ModMarketCategory>> GetCharacterCategoriesAsync(
        CancellationToken ct = default)
    {
        if (_categoryCache is not null && DateTime.UtcNow - _categoryCacheAt < CategoryCacheTtl)
        {
            _logger.Debug("Returning cached character categories ({Count})", _categoryCache.Count);
            return _categoryCache;
        }

        try
        {
            // 三层降级链,顺序有意如此 —— Supabase 通而 COS 被墙时 ② 能救命,反之 ③ 能救命:
            //   ① 聚合视图 mod_character_counts(62 行 / 2.2KB)
            //   ② 全表扫 1000/页(109KB) —— 现有实现原样保留,见 TryGetTallyFromFullScanAsync 的注释
            //   ③ 离线快照本地 group by
            var tally = await TryGetTallyFromViewAsync(ct)
                        ?? await TryGetTallyFromFullScanAsync(ct)
                        ?? await TryGetTallyFromSnapshotAsync(ct);

            if (tally is null)
            {
                _logger.Error("分类计数三条路径全部失败,侧边栏只显示「全部 0」");
                return [ModMarketCategory.CreateAll(0)];
            }

            var result = BuildCategories(tally);
            _categoryCache = result;
            _categoryCacheAt = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GetCharacterCategoriesAsync failed");
            return [ModMarketCategory.CreateAll(0)];
        }
    }

    /// <summary>
    /// ① 走数据库聚合视图,一次请求拿到每个角色的计数(替代 109KB 的全表扫,那是市场出口的大头)。
    ///
    /// 用 <c>IsSuccessStatusCode</c> 而不是 <c>EnsureSuccessStatusCode()</c>:后者抛出的异常会被
    /// 上面的 catch 吃掉,② 就永远没机会执行。
    ///
    /// 失败刻意**不做负缓存** —— 402/404 都是快速失败,加冷却反而会在网关恢复后继续把客户端钉在全表扫上。
    /// </summary>
    private async Task<MarketCategoryTally?> TryGetTallyFromViewAsync(CancellationToken ct)
    {
        try
        {
            var url = "mod_character_counts?select=character,mod_count&limit=1000";
            var response = await CreateClient().GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                // 网关锁定期(402)与「DDL 还没执行」(404)都会走到这里,两者都是可预期的,
                // 所以只记 Debug —— 真正值得报警的是下面全表扫也失败。
                _logger.Debug("聚合视图不可用({Status}),回退到全表扫", (int)response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var counts = new List<KeyValuePair<string, int>>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var name = element.TryGetProperty("character", out var c) ? c.GetString() : null;
                var count = element.TryGetProperty("mod_count", out var m) && m.TryGetInt32(out var n) ? n : 0;
                counts.Add(new KeyValuePair<string, int>(name ?? string.Empty, count));
            }

            // 撞到 limit 上限说明计数被截断,这时候的 Total 是错的(Skins 是算出来的,会跟着变小)。
            // 宁可回退到全表扫,也不要端出一份看着正常、实则少算的分类。
            if (counts.Count >= 1000)
            {
                _logger.Warning("聚合视图返回 {Count} 行,已达请求上限,计数可能被截断,回退到全表扫", counts.Count);
                return null;
            }

            var tally = MarketCategoryTally.FromCounts(counts);
            _logger.Information("分类计数取自聚合视图:{Groups} 组 / {Total} 条", tally.ByCharacter.Count, tally.Total);
            return tally;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "读取聚合视图失败,回退到全表扫");
            return null;
        }
    }

    /// <summary>
    /// ② 全表扫。**必须保留**:客户端是自动更新的,一定会先于手工执行的 DDL 到达用户机器,
    /// 那个窗口里聚合视图还是 404。代价约等于零 —— 只是把原来的循环收进一个返回可空值的方法。
    /// </summary>
    private async Task<MarketCategoryTally?> TryGetTallyFromFullScanAsync(CancellationToken ct)
    {
        try
        {
            var client = CreateClient();

            // Supabase-hosted PostgREST defaults db-max-rows to 1000 and silently clamps any
            // larger limit to 1000 — a single limit=10000 request only returns the first 1000
            // rows, truncating per-character counts at 1000. Instead paginate in chunks of 1000
            // (≤ the cap) ordered by the unique id so every row is counted exactly once, and keep
            // fetching until a short page signals the end. This yields exact per-character counts
            // regardless of how many published mods exist.
            const int pageSize = 1000;
            const int maxPages = 100; // safety guard (~100k mods) to avoid ever hanging the UI
            const string baseFilters =
                "mods?select=character&is_published=eq.true&is_available=eq.true&order=id.asc";
            var characters = new List<string?>();

            for (var offset = 0; ; offset += pageSize)
            {
                var url = $"{baseFilters}&limit={pageSize}&offset={offset}";
                _logger.Information("Supabase GET categories {Url}", url);
                var response = await client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var rowsThisPage = 0;

                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    rowsThisPage++;
                    if (element.TryGetProperty("character", out var prop))
                        characters.Add(prop.GetString());
                }

                // Short page => reached the end. Also bail on the page guard so a
                // server that ignores offset can never loop forever.
                if (rowsThisPage < pageSize || (offset / pageSize) + 1 >= maxPages)
                {
                    if ((offset / pageSize) + 1 >= maxPages)
                        _logger.Warning("Character category scan reached page guard at offset {Offset}", offset);
                    break;
                }
            }

            // 空白角色名的剔除统一在 MarketCategoryTally 里做(与聚合视图的 btrim 口径一致)。
            var tally = MarketCategoryTally.FromCharacters(characters);
            _logger.Information("分类计数取自全表扫:{Groups} 组 / {Total} 条", tally.ByCharacter.Count, tally.Total);
            return tally;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "全表扫分类失败,回退到离线快照");
            return null;
        }
    }

    /// <summary>
    /// ③ 离线快照本地 group by。顺带把快照灌进 <see cref="ModMarketSnapshotService"/> 的缓存,
    /// 紧接着的翻页/搜索直接复用,不产生第二次下载。
    /// </summary>
    private async Task<MarketCategoryTally?> TryGetTallyFromSnapshotAsync(CancellationToken ct)
    {
        var snapshot = await _snapshotService.GetAsync(ct);
        if (snapshot is null) return null;

        var tally = MarketCategoryTally.FromCharacters(
            snapshot.Mods.Where(m => m.IsPublished).Select(m => m.Character));
        _logger.Information("分类计数取自离线快照:{Groups} 组 / {Total} 条", tally.ByCharacter.Count, tally.Total);
        return tally;
    }

    /// <summary>
    /// 把计数组装成侧边栏分类列表。计数一律走 <see cref="MarketCategoryTally.CountFor"/> ——
    /// 「角色皮肤 = 总数 - UI - Other/Misc」这条算术只在 tally 里写了一遍,两条路径不会漂移。
    /// </summary>
    private List<ModMarketCategory> BuildCategories(MarketCategoryTally tally)
    {
        // Build multi-key image lookup from local characters
        Dictionary<string, Uri> imageLookup;
        try
        {
            var chars = _gameService.GetAllModdableObjectsAsCategory<ICharacter>(GetOnly.Both);
            imageLookup = new(StringComparer.OrdinalIgnoreCase);
            foreach (var c in chars)
            {
                if (c.ImageUri is null) continue;
                imageLookup[c.InternalName.Id] = c.ImageUri;
                imageLookup[c.DisplayName] = c.ImageUri;
                // Also add display name with special chars stripped
                var normalizedDisplay = c.DisplayName
                    .Replace(" ", "").Replace("・", "").Replace("·", "");
                if (normalizedDisplay != c.DisplayName)
                    imageLookup[normalizedDisplay] = c.ImageUri;
                if (c is ICharacter ch)
                    foreach (var key in ch.Keys)
                        imageLookup[key] = c.ImageUri;
            }

            _logger.Debug("ImageLookup has {Count} entries for {CharCount} characters",
                imageLookup.Count, chars.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to build image lookup from local characters");
            imageLookup = new();
        }

        // Icons for special Supabase-only categories
        var iconDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "Assets", "Games", "WuWa", "Images", "Characters");
        var specialIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MarketCategoryKeys.OtherMisc] = "other.png",
            [MarketCategoryKeys.Skins] = "skins.png",
            [MarketCategoryKeys.Ui] = "ui.png",
        };

        // Helper to resolve an image for a category key
        Uri? ResolveImage(string key)
        {
            Uri? img = null;
            imageLookup.TryGetValue(key, out img);

            // Fallback: remove special chars (spaces, middle dots) and try again
            if (img is null)
            {
                var normalized = key
                    .Replace(" ", "")
                    .Replace("·", "") // middle dot
                    .Replace("・", "") // katakana middle dot
                    .Replace("・", "")     // fullwidth middle dot
                    .Replace("·", "");     // latin middle dot

                if (!string.Equals(normalized, key, StringComparison.OrdinalIgnoreCase))
                {
                    imageLookup.TryGetValue(normalized, out img);
                    if (img is not null)
                        _logger.Debug("Resolved image for '{Key}' via normalized key '{Normalized}'", key, normalized);
                }
            }

            if (img is null && specialIcons.TryGetValue(key, out var iconFile))
            {
                var path = Path.Combine(iconDir, iconFile);
                if (File.Exists(path)) img = new Uri(path);
            }

            if (img is null && !specialIcons.ContainsKey(key))
            {
                _logger.Warning("No image found for category key '{Key}' (lookup has {Count} entries)",
                    key, imageLookup.Count);
                // Log first 5 lookup keys for diagnostics
                var sampleKeys = imageLookup.Keys.Take(5);
                _logger.Debug("Sample lookup keys: {Keys}", string.Join(", ", sampleKeys));
            }

            return img;
        }

        // Fixed-order special categories
        var specialKeys = new[]
        {
            MarketCategoryKeys.Skins, MarketCategoryKeys.OtherMisc, MarketCategoryKeys.Ui
        };
        var specialCategories = specialKeys
            .Select(key => new ModMarketCategory(key, key, tally.CountFor(key), ResolveImage(key)))
            .ToList();

        // Character categories: alphabetical by key
        var characterCategories = tally.ByCharacter
            .Where(kvp => !specialKeys.Contains(kvp.Key))
            .Select(kvp => new ModMarketCategory(kvp.Key, kvp.Key, kvp.Value, ResolveImage(kvp.Key)))
            .OrderBy(c => c.Key, StringComparer.Create(new System.Globalization.CultureInfo("zh-CN"), ignoreCase: true))
            .ToList();

        // Assemble final list
        var result = new List<ModMarketCategory> { ModMarketCategory.CreateAll(tally.Total) };
        result.AddRange(specialCategories);
        result.AddRange(characterCategories);

        _logger.Information("Loaded {Count} character categories, total mods: {Total}, skins: {Skins}",
            characterCategories.Count, tally.Total, tally.Skins);

        return result;
    }

    public async Task<ModMarketResult> GetModsAsync(
        string? character = null,
        string? search = null,
        string? contentFilter = null,
        string? sortBy = null,
        bool modsOnly = false,
        bool? nsfwOnly = null,
        bool directDownloadOnly = false,
        DateTime? updatedAfter = null,
        int page = 1,
        int pageSize = 24,
        CancellationToken ct = default)
    {
        try
        {
            var request = BuildQuery(character, search, contentFilter, sortBy, modsOnly, nsfwOnly,
                directDownloadOnly, updatedAfter, page, pageSize);

            var live = await TryQuerySupabaseAsync(request, ct);
            if (live is not null) return live;

            // 实时路径失败。当前最常见的原因是 Supabase 出口配额超限 ——
            // 网关会对 REST / Auth / Storage 一律回 402,而 COS 完全不受影响,
            // 这正是兜底快照存在的理由:锁定期里市场照样能看,只是数据停在快照那一刻。
            var snapshot = await _snapshotService.GetAsync(ct);
            if (snapshot is not null) return QuerySnapshot(snapshot, request);

            _logger.Error("Supabase 与离线快照都不可用,市场无法加载");
            return new ModMarketResult
            {
                ErrorMessage = "Supabase 与离线快照都不可用,请检查网络后重试。",
                RequestUrl = request.PostgrestQuery
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GetModsAsync failed. Type: {Type}, Message: {Msg}, Inner: {Inner}",
                ex.GetType().Name, ex.Message, ex.InnerException?.Message);
            return new ModMarketResult { ErrorMessage = $"加载失败:{ex.Message}" };
        }
    }

    /// <summary>
    /// 一次查询的两副面孔:给 Supabase 的 PostgREST 查询串,与喂给本地快照引擎的等价条件。
    /// 两者由同一个方法产出,所以筛选/排序不可能只在一条路径上生效。
    /// </summary>
    private sealed record MarketRequest(string Filters, string PostgrestQuery, MarketQuery Local);

    private MarketRequest BuildQuery(
        string? character,
        string? search,
        string? contentFilter,
        string? sortBy,
        bool modsOnly,
        bool? nsfwOnly,
        bool directDownloadOnly,
        DateTime? updatedAfter,
        int page,
        int pageSize)
    {
        // 先夹紧再同时喂给两侧:本地引擎内部也会夹紧,这里夹过之后两边必然拿到同一组值。
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Max(1, pageSize);

        var filters = new List<string> { "is_published=eq.true", "is_available=eq.true" };

        if (!string.IsNullOrWhiteSpace(character) && character != MarketCategoryKeys.All)
        {
            if (character == MarketCategoryKeys.Skins)
            {
                // Skins = everything except UI and Other/Misc
                filters.Add($"character=not.eq.{MarketCategoryKeys.Ui}");
                filters.Add($"character=not.eq.{MarketCategoryKeys.OtherMisc}");
            }
            else
            {
                filters.Add($"character=eq.{Uri.EscapeDataString(character)}");
            }
        }

        if (modsOnly)
            filters.Add("character=not.is.null");
        if (!string.IsNullOrWhiteSpace(search))
            filters.Add($"title=ilike.*{Uri.EscapeDataString(search)}*");

        // nsfw 结论只解析一次:分类级覆盖内容级这条优先级规则写两遍迟早会分叉。
        var nsfw = nsfwOnly ?? contentFilter switch
        {
            "SFW" => false,     // "隐藏 NSFW": exclude NSFW at API level
            "NSFW" => true,     // "仅 NSFW" (if set via content filter directly)
            _ => (bool?)null    // "All" / "Blur" → no nsfw filter; blur is applied client-side
        };
        if (nsfw.HasValue)
            filters.Add($"nsfw=eq.{(nsfw.Value ? "true" : "false")}");

        if (directDownloadOnly)
            filters.Add("download_url=not.is.null");
        if (updatedAfter.HasValue)
            filters.Add($"created_at=gte.{updatedAfter.Value.ToUniversalTime():yyyy-MM-dd}");

        // sortBy → 线上 order 串 + 本地排序键,同一处映射,不会出现「线上按 A 排、降级按 B 排」。
        string order;
        MarketSortKey sortKey;
        switch (sortBy)
        {
            case "Newest":
                order = "created_at.desc";
                sortKey = MarketSortKey.CreatedAtDesc;
                break;

            case "RecentlyUpdated":
                // UI 走不到这个分支(ModMarketViewModel 把「最近更新」与「最新」都映射到 Newest),
                // 且快照没有 updated_at 这一列。实时路径照旧按 updated_at 排,降级路径只能退化成
                // created_at.desc —— 特意在这里出声,免得日后有人以为两条路径等价。
                _logger.Warning("sortBy=RecentlyUpdated:快照无 updated_at 列,降级排序退化为 created_at.desc");
                order = "updated_at.desc";
                sortKey = MarketSortKey.CreatedAtDesc;
                break;

            case "Most Downloaded":
                order = "downloads_count.desc";
                sortKey = MarketSortKey.DownloadsDesc;
                break;

            case "Most Liked":
                order = "likes_count.desc";
                sortKey = MarketSortKey.LikesDesc;
                break;

            case "Most Viewed":
                order = "views.desc";
                sortKey = MarketSortKey.ViewsDesc;
                break;

            default:
                order = "title.asc";
                sortKey = MarketSortKey.TitleAsc;
                break;
        }

        var offset = (safePage - 1) * safePageSize;
        var filterString = string.Join("&", filters);
        var url = $"mods?select={GridSelect}&{filterString}&order={order}&limit={safePageSize}&offset={offset}";

        var local = new MarketQuery
        {
            Character = character,
            Search = search,
            Nsfw = nsfw,
            DirectDownloadOnly = directDownloadOnly,
            CreatedOnOrAfterUtc = updatedAfter,
            Sort = sortKey,
            Page = safePage,
            PageSize = safePageSize
        };

        return new MarketRequest(filterString, url, local);
    }

    /// <summary>
    /// 实时查询。失败一律返回 <c>null</c>(调用方据此转入快照),
    /// 与「成功但 0 行」严格区分 —— 后者是一次合法的空结果,不该触发降级。
    /// </summary>
    private async Task<ModMarketResult?> TryQuerySupabaseAsync(MarketRequest request, CancellationToken ct)
    {
        try
        {
            var client = CreateClient();
            var url = request.PostgrestQuery;
            _logger.Information("Supabase GET {Url}", url);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
            httpRequest.Headers.Add("Prefer", "count=exact");

            var response = await client.SendAsync(httpRequest, ct);
            _logger.Information("Supabase status: {Status}", response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                // 不能改用 EnsureSuccessStatusCode:抛出的异常会被本方法的 catch 吞掉,
                // 外面就无从判断该不该降级了。
                _logger.Warning("Supabase 请求失败({Status}),转入离线快照", (int)response.StatusCode);
                return null;
            }

            var rawJson = await response.Content.ReadAsStringAsync(ct);
            _logger.Information("Supabase body length: {Len}, preview: {Preview}",
                rawJson.Length,
                rawJson.Length > 200 ? rawJson[..200] : rawJson);

            var mods = new List<ModMarketMod>();
            var rawCount = 0;
            var droppedEntries = new List<string>();
            if (!string.IsNullOrWhiteSpace(rawJson) && rawJson != "[]")
            {
                using var doc = JsonDocument.Parse(rawJson);
                (mods, rawCount, droppedEntries) = ModMarketJson.ReadRows(doc.RootElement, _logger);
            }

            var totalCount = mods.Count;
            string? contentRange = null;
            var usedCountFallback = false;

            if (response.Headers.TryGetValues("Content-Range", out var rangeValues))
            {
                contentRange = rangeValues.FirstOrDefault();
                if (contentRange != null && contentRange.Contains('/'))
                {
                    var parts = contentRange.Split('/');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var total))
                        totalCount = total;
                }
            }

            // Fallback: if Content-Range header is missing, make a lightweight
            // count query with limit=0 to get the exact total.
            if (contentRange == null && rawCount > 0)
            {
                try
                {
                    var countUrl = $"mods?{request.Filters}&limit=0";
                    using var countRequest = new HttpRequestMessage(HttpMethod.Get, countUrl);
                    countRequest.Headers.Add("Prefer", "count=exact");

                    var countResponse = await client.SendAsync(countRequest, ct);
                    if (countResponse.Headers.TryGetValues("Content-Range", out var crValues))
                    {
                        var cr = crValues.FirstOrDefault();
                        if (cr != null && cr.Contains('/'))
                        {
                            var parts = cr.Split('/');
                            if (parts.Length == 2 && int.TryParse(parts[1], out var ctTotal))
                            {
                                totalCount = ctTotal;
                                contentRange = cr + " (fallback)";
                                usedCountFallback = true;
                                _logger.Information("Count fallback succeeded: {Range}", cr);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Count fallback query failed");
                }
            }

            // If still no total, guess from page fullness
            if (totalCount == mods.Count && rawCount >= request.Local.PageSize)
                totalCount = int.MaxValue; // sentinel: more pages exist

            _logger.Information("Returning {Count} mods (raw: {Raw}, total: {Total}, dropped: {Dropped})",
                mods.Count, rawCount, totalCount, droppedEntries.Count);

            return new ModMarketResult
            {
                Mods = mods,
                TotalCount = totalCount,
                RawResponseCount = rawCount,
                ContentRange = contentRange,
                RequestUrl = url,
                DroppedEntries = droppedEntries.ToArray(),
                UsedCountFallback = usedCountFallback
            };
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "实时查询失败,转入离线快照");
            return null;
        }
    }

    /// <summary>
    /// 对快照跑本地查询。纯内存、无 IO,走到这里一定拿得到结果(哪怕 0 行)。
    /// 与线上的一处刻意差异:<c>TotalCount</c> 永远是精确值,不会出现 int.MaxValue 哨兵 ——
    /// 哨兵是「拿不到 Content-Range 只能猜」的产物,本地根本不需要猜。
    /// </summary>
    private ModMarketResult QuerySnapshot(ModMarketSnapshot snapshot, MarketRequest request)
    {
        var page = MarketQueryEngine.Execute(snapshot.Mods, request.Local);

        if (snapshot.DroppedEntries > 0)
            _logger.Warning("本次快照有 {Dropped} 行解析失败(详见快照下载时的日志)", snapshot.DroppedEntries);

        _logger.Information("离线快照命中:{Count}/{Total} 条(快照共 {Raw} 行,生成于 {GeneratedAt})",
            page.Items.Count, page.TotalCount, snapshot.Mods.Count, snapshot.GeneratedAt);

        return new ModMarketResult
        {
            Mods = page.Items.ToList(),
            TotalCount = page.TotalCount,
            RawResponseCount = snapshot.Mods.Count,
            ContentRange = $"snapshot/{page.TotalCount}",
            RequestUrl = "cos://mods-snapshot",
            IsFromSnapshot = true,
            SnapshotGeneratedAt = snapshot.GeneratedAt
        };
    }

    /// <summary>
    /// 按 id 拉取单条 mod 的完整数据(select=*)。列表接口只拉了网格字段,
    /// 详情面板打开时用它补拉 description 等大字段。
    /// </summary>
    public async Task<ModMarketMod?> GetModByIdAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient();
            var url = $"mods?id=eq.{id}&select=*&is_published=eq.true&is_available=eq.true";
            _logger.Information("Supabase GET mod by id {Id}", id);
            var response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning("按 id 取详情失败({Status}),改从离线快照取", (int)response.StatusCode);
                return await GetModByIdFromSnapshotAsync(id, ct);
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var mod = doc.RootElement[0].Deserialize<ModMarketMod>(ModMarketJson.Options);
                return mod;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "GetModByIdAsync failed for {Id},改从离线快照取", id);
            return await GetModByIdFromSnapshotAsync(id, ct);
        }
    }

    /// <summary>
    /// 详情面板的降级路径。快照本来就是 select=* 的完整列集(比列表用的 GridSelect 还多),
    /// 所以这里没有信息损失 —— 面板不需要知道数据来自哪条路径。
    /// </summary>
    private async Task<ModMarketMod?> GetModByIdFromSnapshotAsync(Guid id, CancellationToken ct)
    {
        var snapshot = await _snapshotService.GetAsync(ct);
        if (snapshot is null) return null;

        var mod = snapshot.Mods.FirstOrDefault(m => m.Id == id);
        if (mod is null) _logger.Warning("离线快照里没有 id={Id}", id);
        return mod;
    }
}
