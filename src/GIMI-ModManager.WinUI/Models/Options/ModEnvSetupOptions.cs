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

    /// <summary>Id of the shared Mod injector base package inside the manifest.</summary>
    public string BasePackageId { get; set; } = "xxmi";

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
