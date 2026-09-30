using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Models.Options;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.AppManagement.Updating;

/// <summary>
/// Downloads an application update package with resume, an activity timeout, throttled progress reporting
/// and optional SHA256 verification.
///
/// Deliberately the same shape as <c>ModEnvInstallerService.DownloadWithResumeAsync</c> /
/// <c>DownloadOnceAsync</c> (that pair is an already hand-verified, shipped download path — the task at
/// hand is "COS + a progress bar for app updates", not refactoring ModEnv). The duplication is the price
/// of not touching ModEnv; if a third caller ever appears, these two should be pulled into one shared
/// helper. The differences from ModEnv are intentional: progress is reported as a typed
/// <see cref="AppUpdateDownloadProgress"/> value (the UI binds a percentage and a byte count) instead of a
/// pre-formatted string, and both the expected size and the hash come from the update manifest and may be
/// absent.
/// </summary>
public sealed class AppUpdateDownloader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppUpdateOptions _options;
    private readonly ILogger _logger;

    public AppUpdateDownloader(IHttpClientFactory httpClientFactory, IOptions<AppUpdateOptions> options,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger.ForContext<AppUpdateDownloader>();
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="finalPath"/>, resuming a leftover
    /// <paramref name="partPath"/> .part file when one is present. Returns the final path.
    ///
    /// Throws <see cref="OperationCanceledException"/> when the caller cancels, and
    /// <see cref="InvalidDataException"/> with a user-facing message when the network stays broken past
    /// <see cref="AppUpdateOptions.MaxDownloadRetries"/> attempts or the hash does not match.
    /// </summary>
    /// <param name="expectedSizeBytes">Size from the manifest; 0 when unknown (progress then relies on Content-Length).</param>
    /// <param name="expectedSha256">Lowercase hex from the manifest; null/empty skips verification.</param>
    /// <param name="progress">Throttled progress sink. May be null.</param>
    public async Task<string> DownloadAsync(string url, string partPath, string finalPath, long expectedSizeBytes,
        string? expectedSha256, IProgress<AppUpdateDownloadProgress>? progress, CancellationToken ct)
    {
        var fileName = Path.GetFileName(finalPath);
        var maxRetries = Math.Max(1, _options.MaxDownloadRetries);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(1 << (attempt - 2), 8)); // 1s, 2s, 4s, 8s...
                _logger.Information("Retrying download of {File}, attempt {Attempt}/{Max}",
                    fileName, attempt, maxRetries);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            try
            {
                return await DownloadOnceAsync(url, partPath, finalPath, expectedSizeBytes, expectedSha256, progress, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // user cancelled — never auto-retry
            }
            catch (Exception ex) when (IsTransientDownloadError(ex))
            {
                _logger.Warning("Download of {File} failed transiently (attempt {Attempt}): {Msg}",
                    fileName, attempt, ex.Message);

                // The .part already holds everything received so far; the next attempt resumes from it.
                if (attempt >= maxRetries)
                    throw new InvalidDataException(
                        $"下载失败：网络不稳定，已自动重试 {maxRetries} 次仍未成功，请检查网络后重试 ({fileName})", ex);
            }
        }

        throw new InvalidDataException(
            $"下载失败：网络不稳定，已自动重试 {maxRetries} 次仍未成功，请检查网络后重试 ({fileName})");
    }

    /// <summary>
    /// One download attempt: issues an HTTP Range request resuming the <paramref name="partPath"/> .part,
    /// appends the body with an activity timeout and throttled progress, verifies SHA256, then renames the
    /// .part to the final path. Transient failures propagate so <see cref="DownloadAsync"/> retries.
    /// </summary>
    private async Task<string> DownloadOnceAsync(string url, string partPath, string finalPath,
        long expectedSizeBytes, string? expectedSha256, IProgress<AppUpdateDownloadProgress>? progress,
        CancellationToken ct)
    {
        var fileName = Path.GetFileName(finalPath);
        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        // A .part that already reached the manifest-declared size was never SHA-verified, so it can never be
        // completed by resuming — drop it and start over rather than sending a doomed Range request.
        if (expectedSizeBytes > 0 && existing >= expectedSizeBytes)
        {
            _logger.Information("Discarding stale .part for {File} ({Bytes} bytes >= {Size} bytes)",
                fileName, existing, expectedSizeBytes);
            File.Delete(partPath);
            existing = 0;
        }

        if (existing > 0)
            _logger.Information("Resuming download of {File} from {Bytes} bytes", fileName, existing);

        var client = _httpClientFactory.CreateClient(AppUpdateManifestService.HttpClientName);
        var stallTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.DownloadStallTimeoutSeconds));
        var reportInterval = TimeSpan.FromMilliseconds(Math.Max(50, _options.ProgressReportIntervalMs));

        for (var rangeAttempt = 0; ; rangeAttempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                // 200 = server ignored Range — restart from scratch.
                existing = 0;
                File.Delete(partPath);
            }
            else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Stale .part larger than the resource (no manifest size to pre-check against).
                if (rangeAttempt > 0)
                    throw new HttpRequestException($"服务器拒绝了范围请求 ({fileName})");

                _logger.Warning("Server returned 416 for {File}, discarding stale .part and restarting", fileName);
                File.Delete(partPath);
                existing = 0;
                continue;
            }

            response.EnsureSuccessStatusCode();

            // Fall back to the manifest size when the server omits Content-Length so progress stays correct.
            var total = existing + (response.Content.Headers.ContentLength ??
                                    (expectedSizeBytes > existing ? expectedSizeBytes - existing : 0));

            await using (var inStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var outStream = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long written = existing;
                var lastReportUtc = DateTime.UtcNow;
                long lastReportedBytes = existing;
                using var stalledCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                while (true)
                {
                    // Re-arm the activity timer before each read so a connection that stops delivering bytes
                    // is detected (CancelAfter fires mid-read) instead of hanging indefinitely.
                    stalledCts.CancelAfter(stallTimeout);

                    int read;
                    try
                    {
                        read = await inStream.ReadAsync(buffer, stalledCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new HttpRequestException($"下载长时间无数据，自动续传重试 ({fileName})");
                    }

                    if (read == 0)
                        break;

                    await outStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    written += read;

                    var now = DateTime.UtcNow;
                    if (now - lastReportUtc >= reportInterval)
                    {
                        var speed = (written - lastReportedBytes) / Math.Max((now - lastReportUtc).TotalSeconds, 0.01);
                        lastReportUtc = now;
                        lastReportedBytes = written;
                        progress?.Report(new AppUpdateDownloadProgress(written, total, speed));
                    }
                }
            }

            // Always surface the final state so the bar reaches 100% instead of stopping at the last tick.
            var finalBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            progress?.Report(new AppUpdateDownloadProgress(finalBytes, total, 0));

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var hash = await ComputeSha256Async(partPath, ct).ConfigureAwait(false);
                if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Error("SHA256 mismatch for {File}: expected {Expected}, got {Actual}",
                        fileName, expectedSha256, hash);
                    File.Delete(partPath);
                    throw new InvalidDataException($"更新包校验失败（SHA256 不匹配），请重试 ({fileName})");
                }
            }
            else
            {
                _logger.Debug("No SHA256 in the update manifest for {File}; skipping verification", fileName);
            }

            File.Move(partPath, finalPath, overwrite: true);
            return finalPath;
        }
    }

    /// <summary>True for exceptions that are worth retrying on a flaky link (network/socket/stall timeouts).</summary>
    private static bool IsTransientDownloadError(Exception ex) =>
        ex is HttpRequestException or IOException or TaskCanceledException;

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
}