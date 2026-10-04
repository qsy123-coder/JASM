namespace GIMI_ModManager.WinUI.Models.Options;

/// <summary>
/// Configuration for the one-click Mod environment setup feature.
/// Bound from the "ModEnv" section of appsettings.json.
/// </summary>
public class ModEnvSetupOptions
{
    public const string SectionName = "ModEnv";

    /// <summary>Base URL of the remote version manifest JSON on the CDN.</summary>
    public string ManifestUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL of the selectable-version catalogue JSON (e.g. <c>xxmi-versions.json</c>) on the CDN. The
    /// manifest above only describes one version per package, so this is what lets the user pick an older
    /// base package and roll back. When unset or unreachable the picker degrades to "latest only" and the
    /// wizard behaves exactly as it did before version selection existed.
    /// </summary>
    public string VersionCatalogUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL of the selectable-version catalogue for the XXMI Launcher (GUI) itself, e.g.
    /// <c>launcher-versions.json</c> — same schema as <see cref="VersionCatalogUrl"/>, different file.
    /// The launcher is a separate package from the injector framework with its own version numbering
    /// (2.2.1 / 2.3.8 / 2.4.1…), so it needs its own catalogue; sharing one file would tie two unrelated
    /// release cadences together. When unset or unreachable the launcher picker degrades to just the
    /// manifest's own launcher version — i.e. the pre-version-selection behaviour.
    /// </summary>
    public string LauncherVersionCatalogUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL of the selectable-version catalogue for the per-game package (<c>wwmi-versions.json</c> for
    /// Wuthering Waves) — same schema as the other two catalogues, different file. The game package
    /// versions with the game it targets, independently of the injector and the launcher, so it needs
    /// its own file too. Empty/unreachable degrades that picker to the manifest's own version, i.e. the
    /// pre-version-selection behaviour.
    /// </summary>
    public string WwmiVersionCatalogUrl { get; set; } = string.Empty;

    /// <summary>Id of the shared Mod injector base package inside the manifest.</summary>
    public string BasePackageId { get; set; } = "xxmi";

    /// <summary>
    /// How many per-version snapshots to keep under the backup folder before the oldest are pruned.
    /// Each snapshot is only a few MB (the base package's DLLs), so this is about tidiness, not disk.
    /// </summary>
    public int KeepBackupCount { get; set; } = 5;

    /// <summary>
    /// Optional id of the XXMI Launcher (GUI) package inside the manifest. When set (and present in the
    /// manifest), the setup pipeline also installs/updates the launcher into the XXMI root.
    /// </summary>
    public string? LauncherPackageId { get; set; }

    /// <summary>
    /// How many download attempts are made before giving up on weak networks. Each retry resumes the
    /// partially-downloaded .part via an HTTP Range request, so a dropped connection never restarts from 0.
    /// </summary>
    public int MaxDownloadRetries { get; set; } = 3;

    /// <summary>
    /// Activity timeout for the response body stream: if no bytes arrive within this window the download
    /// is considered stalled and is retried (resuming from the .part). <see cref="HttpClient.Timeout"/> does
    /// not cover draining the response body, so this is the only guard against a stuck connection.
    /// </summary>
    public int DownloadStallTimeoutSeconds { get; set; } = 30;

    /// <summary>Minimum interval between download progress reports, to avoid flooding the UI thread.</summary>
    public int ProgressReportIntervalMs { get; set; } = 400;
}
