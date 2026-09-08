using System.Linq;
using System.Net.Http.Json;
using System.Text.Json;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.WinUI.Models;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services;

public class ModMarketService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly IGameService _gameService;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
        IGameService gameService)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger.ForContext<ModMarketService>();
        _gameService = gameService;
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
            var categories = new Dictionary<string, int>();

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
                    {
                        var name = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            categories.TryGetValue(name, out var count);
                            categories[name] = count + 1;
                        }
                    }
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
                ["Other/Misc"] = "other.png",
                ["Skins"] = "skins.png",
                ["UI"] = "ui.png",
            };

            var total = categories.Values.Sum();

            // "Skins" = everything except UI and Other/Misc (catch-all category)
            var uiCount = categories.GetValueOrDefault("UI", 0);
            var otherMiscCount = categories.GetValueOrDefault("Other/Misc", 0);
            var skinsCount = total - uiCount - otherMiscCount;

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
            var specialKeys = new[] { "Skins", "Other/Misc", "UI" };
            var specialCategories = new List<ModMarketCategory>();
            foreach (var key in specialKeys)
            {
                var count = key switch
                {
                    "Skins" => skinsCount,
                    "Other/Misc" => otherMiscCount,
                    "UI" => uiCount,
                    _ => categories.GetValueOrDefault(key, 0)
                };
                specialCategories.Add(new ModMarketCategory(key, key, count, ResolveImage(key)));
            }

            // Character categories: alphabetical by key
            var characterCategories = categories
                .Where(kvp => !specialKeys.Contains(kvp.Key))
                .Select(kvp => new ModMarketCategory(kvp.Key, kvp.Key, kvp.Value, ResolveImage(kvp.Key)))
                .OrderBy(c => c.Key, StringComparer.Create(new System.Globalization.CultureInfo("zh-CN"), ignoreCase: true))
                .ToList();

            // Assemble final list
            var result = new List<ModMarketCategory> { ModMarketCategory.CreateAll(total) };
            result.AddRange(specialCategories);
            result.AddRange(characterCategories);

            _logger.Information("Loaded {Count} character categories, total mods: {Total}, skins: {Skins}",
                characterCategories.Count, total, skinsCount);

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
            var client = CreateClient();
            var filters = new List<string> { "is_published=eq.true", "is_available=eq.true" };

            if (!string.IsNullOrWhiteSpace(character) && character != "all")
            {
                if (character == "Skins")
                {
                    // Skins = everything except UI and Other/Misc
                    filters.Add("character=not.eq.UI");
                    filters.Add("character=not.eq.Other/Misc");
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

            // Category-level NSFW filter takes precedence over content-level
            if (nsfwOnly.HasValue)
            {
                filters.Add(nsfwOnly.Value ? "nsfw=eq.true" : "nsfw=eq.false");
            }
            else if (contentFilter == "SFW")
            {
                // "隐藏 NSFW": exclude NSFW at API level
                filters.Add("nsfw=eq.false");
            }
            else if (contentFilter == "NSFW")
            {
                // "仅 NSFW" (if set via content filter directly)
                filters.Add("nsfw=eq.true");
            }
            // "All" and "Blur" → no nsfw filter; blur is applied client-side

            if (directDownloadOnly)
                filters.Add("download_url=not.is.null");
            if (updatedAfter.HasValue)
            {
                var dateStr = updatedAfter.Value.ToUniversalTime().ToString("yyyy-MM-dd");
                filters.Add($"created_at=gte.{dateStr}");
            }

            var order = sortBy switch
            {
                "Newest"           => "created_at.desc",
                "RecentlyUpdated"  => "updated_at.desc",
                "Most Downloaded"  => "downloads_count.desc",
                "Most Liked"       => "likes_count.desc",
                "Most Viewed"      => "views.desc",
                _                  => "title.asc"
            };

            var offset = (page - 1) * pageSize;
            var url = $"mods?select={GridSelect}&{string.Join("&", filters)}&order={order}&limit={pageSize}&offset={offset}";

            _logger.Information("Supabase GET {Url}", url);

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Prefer", "count=exact");

            var response = await client.SendAsync(request, ct);
            _logger.Information("Supabase status: {Status}", response.StatusCode);
            response.EnsureSuccessStatusCode();

            var rawJson = await response.Content.ReadAsStringAsync(ct);
            _logger.Information("Supabase body length: {Len}, preview: {Preview}",
                rawJson.Length,
                rawJson.Length > 200 ? rawJson[..200] : rawJson);

            // Count raw JSON array elements before deserialization.
            // Also capture the first few dropped entries for the debug overlay.
            int rawCount = 0;
            var droppedEntries = new List<string>();
            var mods = new List<ModMarketMod>();
            if (!string.IsNullOrWhiteSpace(rawJson) && rawJson != "[]")
            {
                using var doc = JsonDocument.Parse(rawJson);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    rawCount++;
                    try
                    {
                        var m = el.Deserialize<ModMarketMod>(JsonOptions);
                        if (m != null) mods.Add(m);
                    }
                    catch (JsonException jex)
                    {
                        var raw = el.ToString();
                        // Capture the specific error path and a longer snippet for the overlay
                        var msg = $"[{jex.Path ?? "(root)"}] {jex.Message}";
                        var snippet = raw.Length > 600 ? raw[..600] : raw;
                        droppedEntries.Add($"{msg}\n{snippet}");
                        _logger.Warning(jex, "Failed to deserialize mod entry (#{Index}) at {Path}: {Raw}",
                            rawCount, jex.Path, snippet);
                    }
                }
            }

            var totalCount = mods.Count;
            string? contentRange = null;
            bool usedCountFallback = false;

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
                    var countFilters = new List<string>(filters);
                    var countUrl = $"mods?{string.Join("&", countFilters)}&limit=0";
                    var countRequest = new HttpRequestMessage(HttpMethod.Get, countUrl);
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
            if (totalCount == mods.Count && rawCount >= pageSize)
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
            _logger.Error(ex, "GetModsAsync failed. Type: {Type}, Message: {Msg}, Inner: {Inner}",
                ex.GetType().Name, ex.Message, ex.InnerException?.Message);
            return new ModMarketResult();
        }
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
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
            {
                var mod = doc.RootElement[0].Deserialize<ModMarketMod>(JsonOptions);
                return mod;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GetModByIdAsync failed for {Id}", id);
            return null;
        }
    }
}
