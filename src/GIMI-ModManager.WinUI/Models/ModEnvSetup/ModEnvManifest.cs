namespace GIMI_ModManager.WinUI.Models.ModEnvSetup;

/// <summary>
/// Remote version manifest describing the Mod environment packages (e.g. XXMI base + per-game package).
/// Hosted on a China-accessible CDN and maintained by the JASM maintainer.
/// </summary>
public class ModEnvManifest
{
    public int ManifestVersion { get; set; } = 1;

    /// <summary>Packages keyed by id (e.g. "xxmi", "wwmi").</summary>
    public Dictionary<string, ModEnvPackage> Packages { get; set; } = new();
}

public class ModEnvPackage
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }

    /// <summary>Game version this package targets (game packages only, e.g. "2.4.0").</summary>
    public string? GameVersion { get; set; }

    /// <summary>Additional game versions the package is compatible with.</summary>
    public List<string> CompatibleGameVersions { get; set; } = new();
}
