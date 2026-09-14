namespace GIMI_ModManager.WinUI.Models.ModEnvSetup;

/// <summary>
/// Remote catalogue of the <em>selectable</em> XXMI base-package versions, hosted next to the main manifest.
/// <see cref="ModEnvManifest"/> only ever describes ONE version per package, so it cannot express
/// "let the user roll back" — that is what this catalogue is for. The two coexist deliberately:
/// the manifest stays the source of truth for the default (latest) flow, the catalogue only adds choices.
/// </summary>
public class ModEnvVersionCatalog
{
    public int CatalogVersion { get; set; } = 1;

    /// <summary>Selectable versions. Sorted newest-first by the service, not by the JSON.</summary>
    public List<ModEnvCatalogVersion> Versions { get; set; } = new();
}

/// <summary>
/// One selectable version. Derives from <see cref="ModEnvPackage"/> so it can be handed straight to
/// <c>ModEnvInstallerService.InstallPackageAsync</c> — the download/verify/extract/copy pipeline needs
/// no knowledge that the package came from a version picker rather than the main manifest.
/// </summary>
public class ModEnvCatalogVersion : ModEnvPackage
{
    /// <summary>Release date, e.g. "2026-09-13". Display only, may be empty.</summary>
    public string? ReleasedAt { get; set; }

    /// <summary>Optional one-line note shown next to the version in the picker.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// Label for the version dropdown: "v1.1.7（2026-09-13）". The release date is what actually lets a
    /// user tell two "latest" builds apart when they are unsure which one their mods were working with,
    /// so it is shown whenever the catalogue provides it.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(ReleasedAt)
        ? $"v{Version}"
        : $"v{Version}（{ReleasedAt}）";
}
