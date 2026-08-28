using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.WinUI.Models.ModEnvSetup;
using GIMI_ModManager.WinUI.Models.Options;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModEnv;

/// <summary>
/// Marker file stored inside the XXMI root folder, recording which package versions we installed.
/// Used for idempotency (update/repair/skip decisions) on subsequent runs.
/// </summary>
public class ModEnvMarker
{
    public int Version { get; set; } = 1;
    public Dictionary<string, string> InstalledVersions { get; set; } = new();
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Downloads, verifies and installs Mod environment packages (XXMI base + per-game packages).
/// Downloads go to a staging dir, are SHA256-verified and extracted there, then copied to the
/// target (with an elevated copy fallback when the target needs admin rights).
/// </summary>
public class ModEnvInstallerService
{
    public const string MarkerFileName = ".modenv.json";
    public const string StagingDirName = "modenv";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ModEnvSetupOptions _options;
    private readonly ArchiveService _archiveService;
    private readonly ElevatorService _elevatorService;
    private readonly ILogger _logger;

    public ModEnvInstallerService(IHttpClientFactory httpClientFactory, IOptions<ModEnvSetupOptions> options,
        ArchiveService archiveService, ElevatorService elevatorService, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _archiveService = archiveService;
        _elevatorService = elevatorService;
        _logger = logger.ForContext<ModEnvInstallerService>();
    }

    public static string StagingDir => Path.Combine(Path.GetTempPath(), "JASM", StagingDirName);

    // ---- Idempotency marker -------------------------------------------------

    public async Task<Dictionary<string, string>> ReadInstalledVersionsAsync(string rootFolder,
        CancellationToken ct = default)
    {
        var path = Path.Combine(rootFolder, MarkerFileName);
        if (!File.Exists(path)) return new();

        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            var marker = JsonSerializer.Deserialize<ModEnvMarker>(json);
            return marker?.InstalledVersions ?? new();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to read ModEnv marker at {Path}", path);
            return new();
        }
    }

    public async Task WriteMarkerAsync(string rootFolder, Dictionary<string, string> installedVersions,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(rootFolder);
        var marker = new ModEnvMarker { UpdatedAt = DateTime.UtcNow, InstalledVersions = installedVersions };
        await File.WriteAllTextAsync(Path.Combine(rootFolder, MarkerFileName),
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }), ct);
    }

    /// <summary>
    /// Signature files that mark a correctly-deployed XXMI-family game package. A WWMi/3DMigoto
    /// package ships the DXGI hook (d3d11.dll) plus its config (d3dx.ini) at the folder root and a
    /// Mods directory — there is no loader executable inside the package.
    /// </summary>
    private static readonly string[] PackageSignatureFiles = { "d3d11.dll", "d3dx.ini" };

    /// <summary>True when the MI sub-folder looks like a deployed XXMI-family game package.</summary>
    public static bool IsGamePackagePresent(string miSubFolder)
    {
        if (string.IsNullOrWhiteSpace(miSubFolder)) return false;
        return PackageSignatureFiles.All(f => File.Exists(Path.Combine(miSubFolder, f)))
               && Directory.Exists(Path.Combine(miSubFolder, "Mods"));
    }

    /// <summary>Checks whether the game package's key files are present and intact under the MI sub-folder.</summary>
    public (bool FilesOk, bool ModsOk) CheckGamePackageFiles(string miSubFolder)
    {
        var filesOk = PackageSignatureFiles.All(f => File.Exists(Path.Combine(miSubFolder, f)));
        var modsOk = Directory.Exists(Path.Combine(miSubFolder, "Mods"));
        return (filesOk, modsOk);
    }

    // ---- Install pipeline ---------------------------------------------------

    /// <summary>
    /// Downloads, verifies and installs one package.
    /// <paramref name="targetRoot"/> is the XXMI root; when <paramref name="subDir"/> is non-null the
    /// package installs into <c>targetRoot\subDir</c>.
    /// </summary>
    public async Task InstallPackageAsync(ModEnvPackage pkg, string targetRoot, string? subDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(StagingDir);
        var zipPath = await DownloadWithResumeAsync(pkg, progress, ct).ConfigureAwait(false);

        progress?.Report($"正在解压 {pkg.Version}...");
        var extractRoot = Path.Combine(StagingDir, $"extract_{pkg.GetHashCode():x}_{Guid.NewGuid():N}");
        var extractedFolder = _archiveService.ExtractArchive(zipPath, extractRoot).FullName;
        NormalizeSingleRootFolder(ref extractedFolder);

        var targetDir = subDir is null ? targetRoot : Path.Combine(targetRoot, subDir);
        await CopyToTargetAsync(extractedFolder, targetDir, progress, ct).ConfigureAwait(false);

        TryCleanup(extractRoot);
        TryCleanup(zipPath);
    }

    // ---- Download with resume + SHA256 -------------------------------------

    private async Task<string> DownloadWithResumeAsync(ModEnvPackage pkg, IProgress<string>? progress,
        CancellationToken ct)
    {
        var fileName = Uri.TryCreate(pkg.DownloadUrl, UriKind.Absolute, out var uri)
            ? Path.GetFileName(uri.LocalPath)
            : "package.zip";
        if (string.IsNullOrWhiteSpace(Path.GetExtension(fileName))) fileName += ".zip";

        var partPath = Path.Combine(StagingDir, $"{fileName}.part");
        var finalPath = Path.Combine(StagingDir, fileName);

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existing > 0)
            _logger.Information("Resuming download of {File} from {Bytes} bytes", fileName, existing);

        var client = _httpClientFactory.CreateClient(ModEnvManifestService.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, pkg.DownloadUrl);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        // 206 = resumed; 200 = server ignored Range, restart from scratch
        if (response.StatusCode == HttpStatusCode.OK)
        {
            existing = 0;
            File.Delete(partPath);
        }

        response.EnsureSuccessStatusCode();

        var total = existing + response.Content.Headers.ContentLength.GetValueOrDefault(0);
        await using (var inStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var outStream = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long written = existing;
            int read;
            while ((read = await inStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await outStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                progress?.Report($"下载中 {written:N0} / {total:N0} 字节");
            }
        }

        progress?.Report("校验文件完整性 (SHA256)...");
        var hash = await ComputeSha256Async(partPath, ct).ConfigureAwait(false);
        if (!string.Equals(hash, pkg.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Error("SHA256 mismatch for {Pkg}: expected {Expected}, got {Actual}", pkg.Version, pkg.Sha256, hash);
            File.Delete(partPath);
            throw new InvalidDataException($"SHA256 校验失败，请重试或检查网络 ({fileName})");
        }

        File.Move(partPath, finalPath, overwrite: true);
        return finalPath;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer.AsSpan(0, read));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // ---- Extraction / copy --------------------------------------------------

    private static void NormalizeSingleRootFolder(ref string extractDir)
    {
        var entries = Directory.GetFileSystemEntries(extractDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            extractDir = entries[0];
    }

    private async Task CopyToTargetAsync(string sourceDir, string targetDir, IProgress<string>? progress,
        CancellationToken ct)
    {
        try
        {
            DirectoryCopy(sourceDir, targetDir, overwrite: true);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.Warning("Target {Target} not writable, requesting elevated copy", targetDir);
            progress?.Report("目标目录需要管理员权限，正在请求提权...");
            var ok = await _elevatorService.CopyDirectoryAsync(sourceDir, targetDir, ct).ConfigureAwait(false);
            if (!ok)
                throw new UnauthorizedAccessException($"写入 {targetDir} 需要管理员权限。请以管理员身份运行 JASM 后重试。");
        }
    }

    private static void DirectoryCopy(string sourceDir, string destDir, bool overwrite)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            DirectoryCopy(subDir, Path.Combine(destDir, Path.GetFileName(subDir)), overwrite);
        }
    }

    private void TryCleanup(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Failed to clean up {Path}", path);
        }
    }
}
