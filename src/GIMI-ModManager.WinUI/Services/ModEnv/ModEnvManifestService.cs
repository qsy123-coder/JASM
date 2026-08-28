using System.Text.Json;
using GIMI_ModManager.WinUI.Models.ModEnvSetup;
using GIMI_ModManager.WinUI.Models.Options;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModEnv;

/// <summary>
/// Fetches and parses the remote Mod environment version manifest from the CDN.
/// </summary>
public class ModEnvManifestService
{
    public const string HttpClientName = "ModEnv";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<ModEnvSetupOptions> _options;
    private readonly ILogger _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private ModEnvManifest? _cached;

    public ModEnvManifestService(IHttpClientFactory httpClientFactory, IOptions<ModEnvSetupOptions> options,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger.ForContext<ModEnvManifestService>();
    }

    /// <summary>
    /// Returns the remote manifest, or null if it could not be fetched/parsed or is not configured.
    /// Cached for the lifetime of the app; call <see cref="ClearCache"/> to refresh.
    /// </summary>
    public async Task<ModEnvManifest?> GetManifestAsync(CancellationToken ct = default)
    {
        if (_cached is not null)
            return _cached;

        var manifestUrl = _options.Value.ManifestUrl;
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            _logger.Warning("ModEnv ManifestUrl is not configured in appsettings.json");
            return null;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            _logger.Information("Fetching ModEnv manifest from {Url}", manifestUrl);
            var json = await client.GetStringAsync(manifestUrl, ct);
            var manifest = JsonSerializer.Deserialize<ModEnvManifest>(json, JsonOptions);
            _cached = manifest;
            _logger.Information("Loaded ModEnv manifest with {Count} packages",
                manifest?.Packages.Count ?? 0);
            return manifest;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to fetch ModEnv manifest from {Url}", manifestUrl);
            return null;
        }
    }

    public void ClearCache() => _cached = null;
}
