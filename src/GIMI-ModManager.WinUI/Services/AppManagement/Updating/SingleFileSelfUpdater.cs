using System.Diagnostics;
using System.IO.Compression;
using Newtonsoft.Json;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.AppManagement.Updating;

public sealed record SingleFileSelfUpdateResult(bool Success, string? Error)
{
    public static SingleFileSelfUpdateResult Ok() => new(true, null);
    public static SingleFileSelfUpdateResult Fail(string error) => new(false, error);
}

/// <summary>
/// 单文件（SingleFile）版 JASM 的进程内自更新。
///
/// 单文件产物里没有独立的 “JASM - Auto Updater.exe”，外部更新器路径（<see cref="AutoUpdaterService"/>）不可用，
/// 因此由本进程直接完成：下载 SingleFile 更新包 → 用 <see cref="System.IO.Compression.ZipFile"/> 解出新的
/// “JASM - Just Another Skin Manager.exe” → 写一个临时 PowerShell 脚本等待本进程退出 → 脚本用新版 exe 覆盖旧 exe
/// → 再启动新版。全程保持纯单文件，无需外部更新器。选用 .zip（而非 .7z）正是为了用 BCL 解压、不依赖运行时 7z.exe。
/// </summary>
public class SingleFileSelfUpdater
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/qsy123-coder/JASM/releases?per_page=2";

    private const string SingleFileAssetPrefix = "SingleFile_JASM_";
    private const string ExeName = "JASM - Just Another Skin Manager.exe";

    /// <summary>临时 PowerShell 脚本：等本进程退出 → 覆盖 exe → 重启。路径全部经参数传入，避免插值转义陷阱。</summary>
    private const string SelfUpdateScript = @"
param(
    [string]$New,
    [string]$Target,
    [string]$LogFile,
    [int]$PidToWatch
)

$ErrorActionPreference = 'Continue'

function Log([string]$msg) {
    try {
        Add-Content -LiteralPath $LogFile -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' ' + $msg) -ErrorAction SilentlyContinue
    } catch {}
}

Log 'Self-update started. Waiting for JASM to exit...'

$deadline = (Get-Date).AddMinutes(5)
while ((Get-Process -Id $PidToWatch -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
    Start-Sleep -Milliseconds 300
}
Log 'Process exit wait finished.'

$copied = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        Copy-Item -Force -LiteralPath $New -Destination $Target
        $copied = $true
        break
    } catch {
        Start-Sleep -Milliseconds 500
    }
}

Remove-Item -LiteralPath $New -Force -ErrorAction SilentlyContinue

if (-not $copied) {
    Log 'Failed to copy new exe over target. Keeping old version.'
    Start-Process -FilePath $Target
    exit 1
}

Log 'New exe copied. Restarting JASM.'
Start-Process -FilePath $Target
exit 0
";

    private readonly ILogger _logger;

    public SingleFileSelfUpdater(ILogger logger)
    {
        _logger = logger.ForContext<SingleFileSelfUpdater>();
    }

    /// <summary>
    /// 检查是否有更新的单文件包；有则下载并交接给替换脚本。
    /// 返回 <see cref="SingleFileSelfUpdateResult.Success"/> 时调用方应退出应用，由替换脚本接管重启。
    /// </summary>
    public async Task<SingleFileSelfUpdateResult> TryUpdateAsync(Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (assetUrl, version) = await GetSingleFileAssetAsync(cancellationToken);

            if (assetUrl is null)
                return SingleFileSelfUpdateResult.Fail(
                    "未在 GitHub Releases 找到可用的单文件更新包（SingleFile_JASM_*.zip）。请到 https://github.com/qsy123-coder/JASM/releases 手动下载。");

            if (version <= currentVersion)
                return SingleFileSelfUpdateResult.Fail($"当前已是最新版本（v{currentVersion}）。");

            // 1. 下载到临时目录
            var workDir = Path.Combine(Path.GetTempPath(), "JASM_SingleFile_Update");
            Directory.CreateDirectory(workDir);
            var zipPath = Path.Combine(workDir, $"SingleFile_JASM_{version}.zip");
            await DownloadAsync(assetUrl, zipPath, cancellationToken);

            // 2. 解出单个 exe
            var stagedExe = Path.Combine(workDir, ExeName);
            ExtractExe(zipPath, stagedExe);
            TryDelete(zipPath);

            // 3. 交接给替换脚本
            var targetExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(targetExe))
                return SingleFileSelfUpdateResult.Fail("无法确定当前 JASM.exe 的路径，请从原始位置直接运行后再试。");

            return LaunchReplaceScript(workDir, stagedExe, targetExe)
                ? SingleFileSelfUpdateResult.Ok()
                : SingleFileSelfUpdateResult.Fail("启动替换脚本失败，请重试。");
        }
        catch (OperationCanceledException)
        {
            return SingleFileSelfUpdateResult.Fail("更新已取消。");
        }
        catch (Exception e)
        {
            _logger.Error(e, "Single-file self update failed.");
            return SingleFileSelfUpdateResult.Fail($"更新失败：{e.Message}");
        }
    }

    private async Task<(string? Url, Version Version)> GetSingleFileAssetAsync(CancellationToken ct)
    {
        using var httpClient = CreateHttpClient();
        var result = await httpClient.GetAsync(ReleasesApiUrl, ct);
        if (!result.IsSuccessStatusCode)
        {
            _logger.Error("Failed to fetch releases. StatusCode: {StatusCode}", result.StatusCode);
            return (null, new Version(0, 0, 0));
        }

        var text = await result.Content.ReadAsStringAsync(ct);
        var releases = JsonConvert.DeserializeObject<GitHubRelease[]>(text) ?? Array.Empty<GitHubRelease>();

        var latest = releases
            .Where(r => !r.prerelease)
            .Where(r => r.assets is { Length: > 0 })
            .OrderByDescending(r => new Version(r.tag_name?.Trim('v') ?? "0.0.0"))
            .FirstOrDefault();

        if (latest is null)
            return (null, new Version(0, 0, 0));

        var version = new Version(latest.tag_name?.Trim('v') ?? "0.0.0");
        var asset = latest.assets?.FirstOrDefault(a =>
            a.name?.StartsWith(SingleFileAssetPrefix, StringComparison.CurrentCultureIgnoreCase) ?? false);

        return (asset?.browser_download_url, version);
    }

    private async Task DownloadAsync(string url, string zipPath, CancellationToken ct)
    {
        _logger.Information("Downloading single-file update from {Url}", url);
        using var httpClient = CreateHttpClient();
        httpClient.DefaultRequestHeaders.Add("Accept", "application/octet-stream");

        var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(zipPath);
        await source.CopyToAsync(target, ct);
    }

    private static void ExtractExe(string zipPath, string stagedExe)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(ExeName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new InvalidOperationException(
                $"更新包中未找到 {ExeName}（条目：{string.Join(", ", archive.Entries.Select(x => x.FullName))}）。");

        entry.ExtractToFile(stagedExe, overwrite: true);
    }

    private bool LaunchReplaceScript(string workDir, string stagedExe, string targetExe)
    {
        var scriptPath = Path.Combine(workDir, "JASM_SelfUpdate.ps1");
        var logFile = Path.Combine(workDir, "JASM_SelfUpdate_Log.txt");
        var pid = Environment.ProcessId;

        File.WriteAllText(scriptPath, SelfUpdateScript);

        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32",
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershellPath))
            powershellPath = "powershell.exe";

        var psi = new ProcessStartInfo
        {
            FileName = powershellPath,
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
                $"-New \"{stagedExe}\" -Target \"{targetExe}\" -LogFile \"{logFile}\" -PidToWatch {pid}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workDir
        };

        try
        {
            Process.Start(psi);
            _logger.Information("Launched replacement script. WorkDir={WorkDir}, Target={Target}", workDir, targetExe);
            return true;
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to launch replacement script.");
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "Failed to delete {Path}", path);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3
        });
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "JASM-Just_Another_Skin_Manager-Update");
        return httpClient;
    }

    private class GitHubRelease
    {
        public string? tag_name { get; set; }
        public bool prerelease { get; set; }
        public Asset[]? assets { get; set; }
    }

    private class Asset
    {
        public string? name { get; set; }
        public string? browser_download_url { get; set; }
    }
}