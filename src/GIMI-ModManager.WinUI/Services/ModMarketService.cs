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
                filters.Add($"character=eq.{Uri.EscapeDataString(character)}");
            if (!string.IsNullOrWhiteSpace(contentFilter) && contentFilter != "All")
                filters.Add(contentFilter == "NSFW" ? "nsfw=eq.true" : "nsfw=eq.false");

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
        try
        {
            var client = CreateClient();
            var response = await client.GetAsync(
                "mods?select=character&is_published=eq.true&is_available=eq.true", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var categories = new Dictionary<string, int>();

            foreach (var element in doc.RootElement.EnumerateArray())
            {
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

            // Build multi-key image lookup from local characters
            Dictionary<string, Uri> imageLookup;
            try
            {
                var chars = _gameService.GetAllModdableObjectsAsCategory<ICharacter>(GetOnly.Both);
                imageLookup = new(StringComparer.OrdinalIgnoreCase);
                foreach (var c in chars)
                {
                    if (c.ImageUri is null) continue;
                    imageLookup.TryAdd(c.InternalName.Id, c.ImageUri);
                    imageLookup.TryAdd(c.DisplayName, c.ImageUri);
                    if (c is ICharacter ch)
                        foreach (var key in ch.Keys)
                            imageLookup.TryAdd(key, c.ImageUri);
                }
            }
            catch { imageLookup = new(); }

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

            var result = categories
                .Select(kvp =>
                {
                    Uri? img = null;
                    imageLookup.TryGetValue(kvp.Key, out img);
                    if (img is null && specialIcons.TryGetValue(kvp.Key, out var iconFile))
                    {
                        var path = Path.Combine(iconDir, iconFile);
                        if (File.Exists(path)) img = new Uri(path);
                    }
                    return new ModMarketCategory(kvp.Key, kvp.Key, kvp.Value, img);
                })
                .OrderByDescending(c => c.ModCount)
                .ToList();

            var total = result.Sum(c => c.ModCount);
            result.Insert(0, ModMarketCategory.CreateAll(total));

            _logger.Information("Loaded {Count} character categories, total mods: {Total}", result.Count - 1, total);
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GetCharacterCategoriesAsync failed");
            return [ModMarketCategory.CreateAll(0)];
        }
    }

    public async Task<(IReadOnlyList<ModMarketMod> Mods, int TotalCount)> GetModsAsync(
        string? character = null,
        string? search = null,
        string? contentFilter = null,
        string? sortBy = null,
        bool modsOnly = false,
        int page = 1,
        int pageSize = 24,
        CancellationToken ct = default)
    {
        try
        {
            var client = CreateClient();
            var filters = new List<string> { "is_published=eq.true", "is_available=eq.true" };

            if (!string.IsNullOrWhiteSpace(character) && character != "all")
                filters.Add($"character=eq.{Uri.EscapeDataString(character)}");
            if (modsOnly)
                filters.Add("character=not.is.null");
            if (!string.IsNullOrWhiteSpace(search))
                filters.Add($"title=ilike.*{Uri.EscapeDataString(search)}*");
            if (!string.IsNullOrWhiteSpace(contentFilter) && contentFilter != "All")
                filters.Add(contentFilter == "NSFW" ? "nsfw=eq.true" : "nsfw=eq.false");

            var order = sortBy switch
            {
                "RecentlyUpdated" => "updated_at.desc",
                "Most Downloaded" => "downloads_count.desc",
                "Most Liked" => "likes_count.desc",
                _ => "created_at.desc"
            };

            var offset = (page - 1) * pageSize;
            var url = $"mods?{string.Join("&", filters)}&order={order}&limit={pageSize}&offset={offset}";

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

            var mods = new List<ModMarketMod>();
            if (!string.IsNullOrWhiteSpace(rawJson) && rawJson != "[]")
            {
                using var doc = JsonDocument.Parse(rawJson);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    try { var m = el.Deserialize<ModMarketMod>(JsonOptions); if (m != null) mods.Add(m); }
                    catch (JsonException) { /* skip bad entry */ }
                }
            }

            var totalCount = mods.Count;
            if (response.Headers.TryGetValues("Content-Range", out var rangeValues))
            {
                var rangeValue = rangeValues.FirstOrDefault();
                if (rangeValue != null && rangeValue.Contains('/'))
                {
                    var parts = rangeValue.Split('/');
                    if (parts.Length == 2 && int.TryParse(parts[1], out var total))
                        totalCount = total;
                }
            }

            _logger.Information("Returning {Count} mods (total: {Total})", mods.Count, totalCount);
            return (mods, totalCount);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GetModsAsync failed. Type: {Type}, Message: {Msg}, Inner: {Inner}",
                ex.GetType().Name, ex.Message, ex.InnerException?.Message);
            return ([], 0);
        }
    }
}
