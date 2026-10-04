using System.Text.Json;
using GIMI_ModManager.WinUI.Models.ModEnvSetup;
using GIMI_ModManager.WinUI.Models.Options;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModEnv;

/// <summary>
/// Fetches a selectable-version catalogue (<c>xxmi-versions.json</c>, <c>launcher-versions.json</c>, …)
/// from the CDN. One service serves every catalogue: they share a schema and a degradation policy.
/// </summary>
/// <remarks>
/// Deliberately NOT cached. The file is tiny (~1 KB) and the picker is opened rarely, so re-fetching on
/// every wizard open is free — and it side-steps the trap that <see cref="ModEnvManifestService"/> has to
/// live with: its app-lifetime cache means a manifest edited on the CDN stays invisible until restart.
/// A stale catalogue would be worse here, because the user picks a version and expects THAT version to
/// be downloaded; silently falling back to an old entry would look like the picker is broken.
/// </remarks>
public class ModEnvVersionCatalogService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<ModEnvSetupOptions> _options;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ModEnvVersionCatalogService(IHttpClientFactory httpClientFactory,
        IOptions<ModEnvSetupOptions> options, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger.ForContext<ModEnvVersionCatalogService>();
    }

    /// <summary>
    /// Returns the selectable injector-framework versions (the <c>xxmi-versions.json</c> catalogue),
    /// newest first. See <see cref="GetVersionsAsync(string, CancellationToken)"/> for the contract.
    /// </summary>
    public Task<IReadOnlyList<ModEnvCatalogVersion>> GetVersionsAsync(CancellationToken ct = default)
        => GetVersionsAsync(_options.Value.VersionCatalogUrl, ct);

    /// <summary>
    /// Returns the versions listed by the catalogue at <paramref name="url"/>, newest first. Never throws
    /// and never returns null: being unreachable or malformed is an expected state, not an error — the
    /// wizard silently degrades to "latest only", which is exactly how JASM behaved before version
    /// selection existed.
    /// </summary>
    /// <remarks>
    /// Takes a URL rather than a package kind so one set of normalization/degradation rules serves every
    /// catalogue without this service having to know which URL belongs to which package — that mapping
    /// stays in <see cref="ModEnvSetupOptions"/> and the callers that read it.
    /// </remarks>
    public async Task<IReadOnlyList<ModEnvCatalogVersion>> GetVersionsAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _logger.Information("ModEnv version catalogue URL is not configured; version picker degrades to latest only");
            return Array.Empty<ModEnvCatalogVersion>();
        }

        try
        {
            // Reuses the "ModEnv" HttpClient (same CDN host, same UA/timeout policy as the manifest).
            var client = _httpClientFactory.CreateClient(ModEnvManifestService.HttpClientName);
            _logger.Information("Fetching ModEnv version catalogue from {Url}", url);
            var json = await client.GetStringAsync(url, ct).ConfigureAwait(false);
            var catalog = JsonSerializer.Deserialize<ModEnvVersionCatalog>(json, JsonOptions);

            var versions = Normalize(catalog, url);
            _logger.Information("Loaded ModEnv version catalogue with {Count} usable versions", versions.Count);
            return versions;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch ModEnv version catalogue from {Url}; degrading to latest only",
                url);
            return Array.Empty<ModEnvCatalogVersion>();
        }
    }

    /// <summary>Drops unusable/duplicate entries and sorts the rest newest-first.</summary>
    private List<ModEnvCatalogVersion> Normalize(ModEnvVersionCatalog? catalog, string url)
    {
        if (catalog?.Versions is null)
        {
            _logger.Warning("ModEnv version catalogue at {Url} has no Versions array", url);
            return new List<ModEnvCatalogVersion>();
        }

        var result = new List<ModEnvCatalogVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in catalog.Versions)
        {
            // A half-filled entry would fail mid-download (empty URL) or at SHA256 verification, which is a
            // far worse experience than just not offering it, so drop it here.
            if (string.IsNullOrWhiteSpace(entry.Version) ||
                string.IsNullOrWhiteSpace(entry.DownloadUrl) ||
                string.IsNullOrWhiteSpace(entry.Sha256))
            {
                _logger.Warning("Skipping incomplete ModEnv catalogue entry (Version={Version})", entry.Version);
                continue;
            }

            if (!seen.Add(entry.Version))
            {
                _logger.Warning("Skipping duplicate ModEnv catalogue entry for version {Version}", entry.Version);
                continue;
            }

            result.Add(entry);
        }

        result.Sort((left, right) => ModEnvVersion.CompareDescending(left.Version, right.Version));
        return result;
    }
}
