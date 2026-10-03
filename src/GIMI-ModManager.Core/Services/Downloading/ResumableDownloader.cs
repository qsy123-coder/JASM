using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Serilog;

namespace GIMI_ModManager.Core.Services.Downloading;

/// <summary>
/// 可续传的单文件下载器（进度 + <c>Range</c> 续传 + 整文件哈希校验）。
///
/// 它是照着 <c>ModEnvInstallerService.DownloadWithResumeAsync</c> 那份**已实机验证过**的形状重写的 ——
/// 项目里那条链路（Mod 环境安装）和单文件自更新各有一份「够用但缺东西」的实现，商店是第三个调用方。
/// <b>本期只服务商店</b>，不迁移另两条：那两条动一下就是一次完整回归，风险和收益不成比例（PRD Phase 2）。
///
/// 关键行为（都不是随手加的，逐条对应一个实测过的坑）：
/// <list type="bullet">
///   <item><b>.part 续传</b>：数据落 <c>&lt;目标&gt;.part</c>，服务端认 <c>Range</c> 就接着写，
///         认不出（回 200）就丢掉重来；</item>
///   <item><b>活动超时</b>：连接不断但不再吐数据是最常见的「假死」，所以每次读之前重新计时，
///         超时按**可重试**失败处理（<c>.part</c> 留着，下一轮接着传）；</item>
///   <item><b>校验后才落盘</b>：哈希不匹配的文件**永远不会**变成目标文件 —— 半截包进安装流程
///         比下载失败难查得多；</item>
///   <item><b>取消不删 .part</b>：暂停/取消是用户的正常操作，删了就等于把「继续」变成「重下」。</item>
/// </list>
///
/// 一次 <see cref="DownloadAsync"/> 调用就是一次「下完这个文件」的完整意图（含重试）；
/// 队列、暂停/继续那层状态机在调用方（PRD Phase 1 第 6 项）。
/// </summary>
public sealed class ResumableDownloader
{
    /// <summary>续传用的临时后缀。公开出来是给调用方判断「有没有可续传的半截文件」（队列界面要显示）。</summary>
    public const string PartSuffix = ".part";

    private readonly ILogger _logger;
    private readonly ResumableDownloaderOptions _options;

    /// <param name="logger">不传就用 Serilog 的静态 logger（测试里不必为此搭一套日志管线）。</param>
    /// <param name="options">不传用默认值。</param>
    public ResumableDownloader(ILogger? logger = null, ResumableDownloaderOptions? options = null)
    {
        _logger = logger ?? Log.ForContext<ResumableDownloader>();
        _options = options ?? new ResumableDownloaderOptions();
    }

    /// <summary>续传文件的路径（<c>&lt;目标&gt;.part</c>）。</summary>
    public static string GetPartPath(string destinationPath) => destinationPath + PartSuffix;

    /// <summary>
    /// 把 <paramref name="url"/> 下到 <paramref name="destinationPath"/>。
    ///
    /// 可重试的失败（网络、连接中断、活动超时）会自动重试 <see cref="ResumableDownloaderOptions.MaxAttempts"/> 次，
    /// 每次从已有的 <c>.part</c> 续；重试耗尽抛 <see cref="DownloadFailedException"/>。
    /// 哈希不匹配与用户取消都**不**自动重试（前者重试还是同一份坏数据，后者是用户的意思）。
    /// </summary>
    /// <param name="httpClient">调用方给（商店复用既有的 GameBanana client，见 PRD）。</param>
    /// <param name="url">下载地址。</param>
    /// <param name="destinationPath">目标文件全路径；目录不存在会自动建。</param>
    /// <param name="hashCheck">期望的整文件哈希；不给就不校验（不推荐，但上传方没给哈希时只能这样）。</param>
    /// <param name="expectedSizeBytes">上传方声明的字节数，仅用于识破「比目标还大的 .part」这种陈旧残留，可空。</param>
    /// <param name="progress">进度回调；可能在任意线程上被调用。</param>
    /// <param name="cancellationToken">取消令牌。取消时 <c>.part</c> **保留**，下次还能续。</param>
    public async Task<DownloadResult> DownloadAsync(HttpClient httpClient, Uri url, string destinationPath,
        DownloadHashCheck? hashCheck = null, long? expectedSizeBytes = null,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrEmpty(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        var partPath = GetPartPath(destinationPath);
        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        var fileName = Path.GetFileName(destinationPath);

        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                // 1s, 2s, 4s, 8s… 上限 8s：退避是为了躲开「刚好那几秒连不上」，不是让用户干等。
                var factor = Math.Min(1 << (attempt - 2), 8);
                var delay = TimeSpan.FromTicks(_options.RetryBackoffBase.Ticks * factor);
                _logger.Information("Retrying download of {File}, attempt {Attempt}/{Max}, waiting {Delay}s",
                    fileName, attempt, maxAttempts, delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await DownloadOnceAsync(httpClient, url, partPath, destinationPath, fileName, hashCheck,
                    expectedSizeBytes, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // 用户取消：绝不自动重试，且 .part 保留（下次从这里续）
            }
            catch (DownloadFailedException ex) when (ex.Reason is DownloadFailureReason.Network
                                                          or DownloadFailureReason.Stall)
            {
                if (attempt >= maxAttempts)
                    throw;

                _logger.Warning("Download of {File} failed (attempt {Attempt}/{Max}): {Message}",
                    fileName, attempt, maxAttempts, ex.Message);
                // .part 里是已经收到的东西，下一轮从它继续。
            }
        }
    }

    /// <summary>
    /// 单次尝试：发（可能带 <c>Range</c> 的）请求 → 追加写 <c>.part</c> → 校验 → 移到目标路径。
    /// 只有「重试有意义」的失败才抛成可重试原因，其余（哈希不符、服务器拒绝）直接抛出去。
    /// </summary>
    private async Task<DownloadResult> DownloadOnceAsync(HttpClient httpClient, Uri url, string partPath,
        string destinationPath, string fileName, DownloadHashCheck? hashCheck, long? expectedSizeBytes,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        // 已经和数据源声明的体积一样大（甚至更大）的 .part 从来没被校验过，续传也补不出正确结果
        // —— 直接丢掉重下，比发一个注定失败的 Range 请求强。
        if (existing > 0 && expectedSizeBytes is > 0 && existing >= expectedSizeBytes.Value)
        {
            _logger.Information("Discarding stale .part for {File} ({Existing} bytes >= declared {Declared} bytes)",
                fileName, existing, expectedSizeBytes.Value);
            File.Delete(partPath);
            existing = 0;
        }

        if (existing > 0)
            _logger.Information("Resuming download of {File} from {Bytes} bytes", fileName, existing);

        for (var rangeAttempt = 0; ; rangeAttempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using var response = await SendAsync(httpClient, request, fileName, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                // 200 = 服务端没理会 Range（有些 CDN 就这样）→ 从头写，否则会拼出内容重复的文件。
                existing = 0;
                File.Delete(partPath);
            }
            else if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                if (rangeAttempt > 0 || existing == 0)
                    throw new DownloadFailedException($"服务器拒绝了范围请求 ({fileName})",
                        DownloadFailureReason.ServerRejected);

                _logger.Warning("Server returned 416 for {File}, discarding stale .part and restarting", fileName);
                File.Delete(partPath);
                existing = 0;
                continue;
            }
            else if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                // 206 但起点不是我们要的位置（极少见）= 接着写就会错位，按「没理会 Range」处理。
                var rangeStart = TryGetRangeStart(response.Content.Headers);
                if (rangeStart is { } start && start != existing)
                {
                    _logger.Warning("Server returned 206 starting at {Start} instead of {Expected} for {File}, restarting",
                        start, existing, fileName);
                    existing = 0;
                    File.Delete(partPath);
                }
            }

            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                throw new DownloadFailedException(
                    $"下载失败：服务器返回 {(int)response.StatusCode} ({fileName})",
                    IsTransientStatus(response.StatusCode)
                        ? DownloadFailureReason.Network
                        : DownloadFailureReason.ServerRejected);

            long bytesRead;
            try
            {
                bytesRead = await WriteBodyAsync(response, partPath, existing, expectedSizeBytes, fileName,
                    progress, cancellationToken).ConfigureAwait(false);
            }
            catch (DownloadFailedException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // 连接在后半程断掉（IOException / HttpRequestException）——**最值得重试**的一类失败：
                // .part 里已经躺着下载到一半的数据，下一轮从断点接着传，不用从头再来。
                throw new DownloadFailedException($"下载中断 ({fileName})", DownloadFailureReason.Network, ex);
            }

            var downloaded = existing + bytesRead;
            if (hashCheck is { } check && !string.IsNullOrWhiteSpace(check.ExpectedHash))
            {
                progress?.Report(new DownloadProgress(DownloadPhase.Verifying, downloaded, downloaded, 0));
                await VerifyAsync(partPath, fileName, check, cancellationToken).ConfigureAwait(false);
            }
            else if (hashCheck is not null)
            {
                // 上游没给哈希（GameBanana 的 _sMd5Checksum 可能是空串）≠ 校验失败：
                // 拿空值去比会把一份好文件删掉，那比不校验糟得多。
                _logger.Warning("No expected hash for {File}, skipping verification", fileName);
            }

            progress?.Report(new DownloadProgress(DownloadPhase.Completed, downloaded, downloaded, 0));
            File.Move(partPath, destinationPath, overwrite: true);
            _logger.Information("Downloaded {File} ({Bytes} bytes) from {Url}", fileName, downloaded, url);

            return new DownloadResult(destinationPath, downloaded, response.RequestMessage?.RequestUri ?? url);
        }
    }

    /// <summary>
    /// 把响应体追加写进 <c>.part</c>，返回**本次**写入的字节数。
    ///
    /// 活动超时的处理是这段的核心：每次读之前把计时器重新压上（<c>CancelAfter</c> 会重置），
    /// 于是「连上了但一直不吐数据」会被 CancelAfter 打断，而不是无限期挂着。
    /// </summary>
    private async Task<long> WriteBodyAsync(HttpResponseMessage response, string partPath, long existing,
        long? expectedSizeBytes, string fileName, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        // Content-Length 缺失时退回数据源声明的体积，否则进度会永远停在 0%。
        var total = existing + (response.Content.Headers.ContentLength ??
                                (expectedSizeBytes is { } size && size > existing ? size - existing : 0));

        await using var inStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var outStream = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None);

        var buffer = new byte[Math.Max(1, _options.BufferSize)];
        long written = existing;
        var lastReportUtc = DateTime.UtcNow;
        long lastReportedBytes = existing;
        using var stalledCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (true)
        {
            stalledCts.CancelAfter(_options.StallTimeout);

            int read;
            try
            {
                read = await inStream.ReadAsync(buffer, stalledCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 不可重试的失败里最像「网络问题」的一种，所以按可重试处理（.part 留着续）。
                throw new DownloadFailedException($"下载长时间无数据 ({fileName})", DownloadFailureReason.Stall);
            }

            if (read == 0)
                break;

            await outStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;

            var now = DateTime.UtcNow;
            if (now - lastReportUtc >= _options.ProgressReportInterval)
            {
                var speed = (written - lastReportedBytes) / Math.Max((now - lastReportUtc).TotalSeconds, 0.01);
                lastReportUtc = now;
                lastReportedBytes = written;
                progress?.Report(new DownloadProgress(DownloadPhase.Downloading, written,
                    total > 0 ? total : null, speed));
            }
        }

        await outStream.FlushAsync(ct).ConfigureAwait(false);
        return written - existing;
    }

    /// <summary>校验整文件哈希。不匹配就删掉 <c>.part</c> —— 坏数据留着只会在下次续出更坏的东西。</summary>
    private async Task VerifyAsync(string partPath, string fileName, DownloadHashCheck check, CancellationToken ct)
    {
        var actual = await ComputeHashAsync(partPath, check.Algorithm, ct).ConfigureAwait(false);
        if (string.Equals(actual, check.ExpectedHash?.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        File.Delete(partPath);
        _logger.Error("Hash mismatch for {File}: expected {Expected}, got {Actual}", fileName, check.ExpectedHash, actual);
        throw new DownloadFailedException($"文件校验失败，下载内容可能不完整 ({fileName})",
            DownloadFailureReason.HashMismatch);
    }

    private static async Task<string> ComputeHashAsync(string filePath, HashAlgorithmName algorithm,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            hash.AppendData(buffer.AsSpan(0, read));

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient httpClient, HttpRequestMessage request,
        string fileName, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            // 连不上 / 连到一半断了：归类成可重试，交给外层退避。
            throw new DownloadFailedException($"下载连接失败 ({fileName})", DownloadFailureReason.Network, ex);
        }
    }

    /// <summary>5xx 与 408 是服务端/链路抖动，值得重试；4xx 是「这个地址就是不行」，重试没意义。</summary>
    private static bool IsTransientStatus(HttpStatusCode status) =>
        (int)status >= 500 || status == HttpStatusCode.RequestTimeout;

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or IOException;

    /// <summary>
    /// 从 <c>Content-Range</c> 里取本次响应的起点（形如 <c>bytes 100-499/1000</c>）。
    /// 头缺失或格式不认识时返回 null —— 那时按「服务端没给」处理，不要凭猜去删文件。
    /// </summary>
    private static long? TryGetRangeStart(HttpContentHeaders headers)
    {
        var range = headers.ContentRange;
        if (range is null || !range.HasRange)
            return null;

        return range.From;
    }
}

/// <summary>下载器的可调参数。默认值就是线上该用的值，测试里会调小（比如活动超时）。</summary>
public sealed class ResumableDownloaderOptions
{
    /// <summary>含首次在内的总尝试次数。1 = 不重试。</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>退避基数：第 n 次重试等 <c>基数 × 2^(n-2)</c>（上限 8 倍）。</summary>
    public TimeSpan RetryBackoffBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>多久没读到任何字节就算「假死」。默认 30s —— 比一般 CDN 的空窗大得多，不会误杀慢链路。</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>进度回调的最小间隔。太密会把进度条刷成噪声，也会让回调本身成为开销。</summary>
    public TimeSpan ProgressReportInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public int BufferSize { get; init; } = 81920;
}

/// <summary>下载阶段。UI 靠它区分「在下」和「在校验」（后者没有字节在动，进度条会看起来卡住）。</summary>
public enum DownloadPhase
{
    Downloading,
    Verifying,
    Completed
}

/// <summary>
/// 一次进度回调。不可变结构体 —— 它会被跨线程读到（<see cref="IProgress{T}"/> 的回调线程由实现决定）。
/// </summary>
/// <param name="Phase">当前阶段。</param>
/// <param name="BytesReceived">已落盘字节数（含续传前已有的部分）。</param>
/// <param name="TotalBytes">总字节数；服务端与数据源都没给时为 null —— 那时<b>不要</b>显示百分比。</param>
/// <param name="BytesPerSecond">瞬时速度；未知时为 0。</param>
public readonly record struct DownloadProgress(DownloadPhase Phase, long BytesReceived, long? TotalBytes,
    double BytesPerSecond)
{
    /// <summary>百分比；总长未知时为 null。刻意不给「未知时返回 0」的默认值 —— 那会显示成「卡在 0%」。</summary>
    public int? Percent => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes.Value, 0, 100)
        : null;
}

/// <summary>下载完成的结果。</summary>
/// <param name="FullPath">最终文件全路径。</param>
/// <param name="BytesWritten">写入的字节数。</param>
/// <param name="ResolvedUri">跟完重定向后的真实地址（GameBanana 的 <c>/dl/</c> 会跳到 CDN），日志/去重用。</param>
public readonly record struct DownloadResult(string FullPath, long BytesWritten, Uri ResolvedUri);

/// <summary>期望的整文件哈希。商店用 MD5（GameBanana 的 <c>_sMd5Checksum</c>），保留算法参数是为了将来的调用方不必改这个类型。</summary>
/// <param name="Algorithm">哈希算法。</param>
/// <param name="ExpectedHash">期望值（十六进制，大小写不敏感）。</param>
public readonly record struct DownloadHashCheck(HashAlgorithmName Algorithm, string ExpectedHash)
{
    public static DownloadHashCheck Md5(string expectedHash) => new(HashAlgorithmName.MD5, expectedHash);
}

/// <summary>下载失败的分类，让调用方在 UI 上给出对得上的说法，而不是把英文异常原文丢给用户。</summary>
public enum DownloadFailureReason
{
    /// <summary>连不上 / 连接中断 / 5xx —— 重试有意义。</summary>
    Network,

    /// <summary>连上了但长时间没有数据 —— 重试有意义。</summary>
    Stall,

    /// <summary>整文件哈希不匹配。重试同一份数据没意义。</summary>
    HashMismatch,

    /// <summary>服务器明确拒绝（4xx，含 416 用尽重试）。</summary>
    ServerRejected
}

/// <summary>下载失败。<see cref="Exception.Message"/> 是给人看的中文说明，调用方可直接用。</summary>
public sealed class DownloadFailedException(string message, DownloadFailureReason reason, Exception? inner = null)
    : Exception(message, inner)
{
    public DownloadFailureReason Reason { get; } = reason;
}