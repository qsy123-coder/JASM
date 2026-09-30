using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Models.Options;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.AppManagement.Updating;

/// <summary>
/// Answers "which version should we update to, and where is its package" for the application's own
/// update chain, by delegating to <see cref="AppUpdateReleaseResolver"/> (COS manifest first, GitHub
/// Releases as fallback). All three consumers share this one service, so the badge the user sees and
/// the package that actually gets downloaded can never disagree:
///
/// <list type="bullet">
/// <item><see cref="UpdateChecker"/> — decides whether to light the "new version" badge.</item>
/// <item><see cref="SingleFileSelfUpdater"/> — picks the single-file package to download in-process.</item>
/// <item><c>JASM.AutoUpdater</c> — the standalone updater for the folder layout; it does not reference
/// the WinUI project, so it compiles the same Core resolver in directly (see its .csproj).</item>
/// </list>
///
/// Unlike <c>ModEnvManifestService</c> this deliberately caches nothing: the caller is a loop that
/// re-checks every two hours, and a process-lifetime cache would keep a freshly published release
/// invisible until the app restarts.
/// </summary>
public sealed class AppUpdateManifestService
{
    public const string HttpClientName = "AppUpdate";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppUpdateOptions _options;
    private readonly ILogger _logger;

    public AppUpdateManifestService(IHttpClientFactory httpClientFactory, IOptions<AppUpdateOptions> options,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger.ForContext<AppUpdateManifestService>();
    }

    /// <summary>
    /// Resolves the current release. Never throws for remote/network trouble — a failed lookup comes back
    /// as <see cref="AppUpdateSource.None"/>, which callers treat exactly like "no update right now".
    /// </summary>
    public async Task<AppUpdateReleaseResolver.Result> ResolveLatestAsync(CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var result = await AppUpdateReleaseResolver
            .ResolveAsync(client, _options.ManifestUrl, _options.ReleasesApiUrl, cancellationToken)
            .ConfigureAwait(false);

        switch (result.Source)
        {
            case AppUpdateSource.Cos:
                _logger.Information("App update release {Version} resolved from the COS manifest",
                    result.Release?.Version);
                break;

            case AppUpdateSource.GitHub:
                // Warning, not Information: the fallback working is still a signal that the primary
                // channel is unconfigured or broken, and that is what an operator needs to notice.
                _logger.Warning("Falling back to the GitHub update channel. Reason: {Reason}",
                    result.Diagnostic ?? "COS manifest not configured");
                break;

            default:
                _logger.Warning("No app update release could be resolved. Reason: {Reason}", result.Diagnostic);
                break;
        }

        return result;
    }

    /// <summary>
    /// Human-facing "what's new" page for a release: the release's own <c>notesUrl</c> (pointing at the
    /// GitHub release page, where the changelog is written), else the configured manual-download page.
    /// Returns null when neither is usable, in which case the caller should hide the link entirely.
    /// </summary>
    public string? GetNotesUrl(AppUpdateRelease? release)
    {
        if (!string.IsNullOrWhiteSpace(release?.NotesUrl))
            return release.NotesUrl;

        return string.IsNullOrWhiteSpace(_options.ManualDownloadUrl) ? null : _options.ManualDownloadUrl;
    }
}