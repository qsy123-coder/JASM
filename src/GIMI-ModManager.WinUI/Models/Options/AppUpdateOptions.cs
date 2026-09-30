namespace GIMI_ModManager.WinUI.Models.Options;

/// <summary>
/// Configuration for the application's own update chain (check for a new version, download the package,
/// replace the running exe). Bound from the "AppUpdate" section of appsettings.json.
///
/// Mirrors <see cref="ModEnvSetupOptions"/> on purpose: both download large packages from the same COS
/// bucket, so they share the same retry / stall-timeout / progress-interval knobs and their defaults
/// should be read the same way.
/// </summary>
public class AppUpdateOptions
{
    public const string SectionName = "AppUpdate";

    /// <summary>
    /// URL of the update manifest JSON on COS (<c>app/update.json</c>) — the primary channel. Object
    /// storage serves files but no metadata, so this manifest carries what the GitHub Releases API used
    /// to supply: which version is newest, and where its packages live.
    ///
    /// Leaving this empty is the operational rollback switch: the client then goes straight to
    /// <see cref="ReleasesApiUrl"/> (see <c>AppUpdateReleaseResolver</c>), so if COS ever has to be
    /// taken out of the picture, blanking this value makes every shipped client fall back without
    /// anyone having to release a new build.
    /// </summary>
    public string ManifestUrl { get; set; } = string.Empty;

    /// <summary>
    /// GitHub Releases API used as the fallback when the COS manifest is unset or unusable. Needed as a
    /// fallback at least until the pre-COS clients (<c>&lt;= 2.30.0</c>) and the old standalone
    /// <c>JASM - Auto Updater.exe</c> are out of circulation — those only ever read GitHub.
    /// </summary>
    public string ReleasesApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// Human-facing page to open when the user wants to download manually or read the changelog, and the
    /// resolver did not supply a per-release <c>notesUrl</c>. Points at this fork's releases page, not
    /// upstream JASM's.
    /// </summary>
    public string ManualDownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// How many download attempts are made before giving up on weak networks. Each retry resumes the
    /// partially-downloaded .part via an HTTP Range request, so a dropped connection never restarts from 0.
    /// </summary>
    public int MaxDownloadRetries { get; set; } = 3;

    /// <summary>
    /// Activity timeout for the response body stream: if no bytes arrive within this window the download
    /// is considered stalled and is retried (resuming from the .part). <see cref="HttpClient.Timeout"/> does
    /// not cover draining the response body, so this is the only guard against a stuck connection.
    ///
    /// Higher than ModEnv's 30s because update packages are ~100 MB and COS's default domain (no CDN
    /// behind it) can go quiet for a while on a slow line — timing out early costs the user a resumed
    /// retry, which is worse than waiting.
    /// </summary>
    public int DownloadStallTimeoutSeconds { get; set; } = 60;

    /// <summary>Minimum interval between download progress reports, to avoid flooding the UI thread.</summary>
    public int ProgressReportIntervalMs { get; set; } = 400;
}