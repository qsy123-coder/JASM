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
}
