using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GIMI_ModManager.Core.Services.Downloading;

namespace JASM.Tests;

/// <summary>
/// 公共下载件（<see cref="ResumableDownloader"/>）的行为锁。
///
/// 这些用例刻意都拿 <see cref="HttpMessageHandler"/> 假造响应，而不是打真网络 ——
/// 断点续传/416/超时这些分支**只有在被精心摆出来的响应下才可复现**，
/// 靠真实 CDN 是赌运气（而且 CI 上还不稳定）。
///
/// 覆盖的四类事：① 续传与「服务端不配合」时的退路；② 校验把关（坏数据绝不落成目标文件）；
/// ③ 失败分类与重试边界（哪些该重试、哪些不该）；④ 取消/暂停要留下 <c>.part</c>。
/// </summary>
public class ResumableDownloaderTests : IDisposable
{
    private const string DefaultName = "mod.zip";

    private readonly string _directory;

    public ResumableDownloaderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "jasm-downloader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响任何断言，别让清理失败把测试染红。
        }
    }

    // ── 基本形状 ────────────────────────────────────────────

    [Fact]
    public void GetPartPath_AppendsThePartSuffix()
    {
        Assert.Equal(@"C:\tmp\mod.zip.part", ResumableDownloader.GetPartPath(@"C:\tmp\mod.zip"));
    }

    [Fact]
    public void Percent_IsNullWhenTheTotalIsUnknown()
    {
        // 「未知」和「0%」在界面上是两件事：前者不该画进度条。
        var unknown = new DownloadProgress(DownloadPhase.Downloading, 500, null, 0);
        Assert.Null(unknown.Percent);

        var known = new DownloadProgress(DownloadPhase.Downloading, 500, 1000, 0);
        Assert.Equal(50, known.Percent);
    }

    [Fact]
    public async Task Download_WritesTheWholeFileAndRemovesThePartFile()
    {
        var payload = Payload(1000);
        var handler = new FakeHandler(_ => Ok(payload));
        var destination = Destination();

        var result = await NewDownloader().DownloadAsync(NewClient(handler), Url, destination);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(1000, result.BytesWritten);
        Assert.False(File.Exists(ResumableDownloader.GetPartPath(destination)));
        Assert.Null(handler.RangeHeaders[0]); // 没有 .part 就不该发 Range
    }

    [Fact]
    public async Task Download_CreatesTheDestinationDirectory()
    {
        var payload = Payload(10);
        var handler = new FakeHandler(_ => Ok(payload));
        var destination = Path.Combine(_directory, "nested", "deeper", DefaultName);

        await NewDownloader().DownloadAsync(NewClient(handler), Url, destination);

        Assert.True(File.Exists(destination));
    }

    // ── 续传 ────────────────────────────────────────────────

    [Fact]
    public async Task Download_ResumesFromThePartFileWithARangeRequest()
    {
        var payload = Payload(1000);
        var destination = Destination();
        await File.WriteAllBytesAsync(ResumableDownloader.GetPartPath(destination), payload[..400]);
        var handler = new FakeHandler(request => request.Headers.Range is not null
            ? Partial(payload[400..], from: 400, total: 1000)
            : Ok(payload));

        var result = await NewDownloader().DownloadAsync(NewClient(handler), Url, destination);

        Assert.Equal("bytes=400-", handler.RangeHeaders[0]);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(1000, result.BytesWritten); // 含续传前那 400 字节
        Assert.False(File.Exists(ResumableDownloader.GetPartPath(destination)));
    }

    [Fact]
    public async Task Download_RestartsWhenTheServerIgnoresTheRangeHeader()
    {
        var payload = Payload(1000);
        var destination = Destination();
        await File.WriteAllBytesAsync(ResumableDownloader.GetPartPath(destination), payload[..400]);
        // 有些 CDN 就是不理 Range，回 200 + 全量 —— 若当作「接着写」就会得到 1400 字节的坏文件。
        var handler = new FakeHandler(_ => Ok(payload));

        await NewDownloader().DownloadAsync(NewClient(handler), Url, destination);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Download_RestartsWhenTheServerAnswers416()
    {
        var payload = Payload(1000);
        var destination = Destination();
        await File.WriteAllBytesAsync(ResumableDownloader.GetPartPath(destination), payload[..400]);
        var handler = new FakeHandler(request => request.Headers.Range is not null
            ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)
            : Ok(payload));

        await NewDownloader().DownloadAsync(NewClient(handler), Url, destination);

        // 416 = 「你要的起点不存在」，先丢 .part 再来一次，两次请求。
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Download_RestartsWhenPartialContentStartsAtTheWrongOffset()
    {
        var payload = Payload(1000);
        var destination = Destination();
        await File.WriteAllBytesAsync(ResumableDownloader.GetPartPath(destination), payload[..400]);
        // 206 但从 0 开始：接着写会错位，必须丢掉重来。
        var handler = new FakeHandler(_ => Partial(payload, from: 0, total: 1000));

        await NewDownloader().DownloadAsync(NewClient(handler), Url, destination);

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Download_DiscardsAPartFileThatIsAlreadyBiggerThanTheDeclaredSize()
    {
        var payload = Payload(1000);
        var destination = Destination();
        await File.WriteAllBytesAsync(ResumableDownloader.GetPartPath(destination), Payload(1000));
        var handler = new FakeHandler(_ => Ok(payload));

        await NewDownloader().DownloadAsync(NewClient(handler), Url, destination, expectedSizeBytes: 1000);

        Assert.Null(handler.RangeHeaders[0]); // 陈旧 .part 被丢掉 → 首个请求不带 Range
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    // ── 哈希校验 ────────────────────────────────────────────

    [Fact]
    public async Task Download_VerifiesTheHashAndKeepsTheFileWhenItMatches()
    {
        var payload = Payload(1000);
        var handler = new FakeHandler(_ => Ok(payload));
        var destination = Destination();

        await NewDownloader().DownloadAsync(NewClient(handler), Url, destination,
            hashCheck: DownloadHashCheck.Md5(Md5Of(payload)));

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.False(File.Exists(ResumableDownloader.GetPartPath(destination)));
    }

    [Fact]
    public async Task Download_FailsAndDeletesThePartFileOnAHashMismatch()
    {
        var payload = Payload(1000);
        var handler = new FakeHandler(_ => Ok(payload));
        var destination = Destination();

        var failure = await Assert.ThrowsAsync<DownloadFailedException>(() => NewDownloader().DownloadAsync(
            NewClient(handler), Url, destination, hashCheck: DownloadHashCheck.Md5(Md5Of(Payload(999)))));

        Assert.Equal(DownloadFailureReason.HashMismatch, failure.Reason);
        // 坏数据不能变成目标文件，也不能留下 —— 留着只会让下次「续传」续出更坏的东西。
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(ResumableDownloader.GetPartPath(destination)));
    }

    [Fact]
    public async Task Download_DoesNotRetryAHashMismatch()
    {
        var payload = Payload(1000);
        var handler = new FakeHandler(_ => Ok(payload));

        await Assert.ThrowsAsync<DownloadFailedException>(() => NewDownloader(maxAttempts: 3).DownloadAsync(
            NewClient(handler), Url, Destination(), hashCheck: DownloadHashCheck.Md5(Md5Of(Payload(999)))));

        // 重试拿到的是同一份坏数据，只会白等退避时间。
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Download_SkipsVerificationWhenTheUpstreamGivesNoHash()
    {
        var payload = Payload(1000);
        var handler = new FakeHandler(_ => Ok(payload));
        var destination = Destination();

        // GameBanana 的 _sMd5Checksum 可能是空串：那叫「没给哈希」，不叫「校验失败」——
        // 拿空值去比会把一份好文件删掉。
        await NewDownloader().DownloadAsync(NewClient(handler), Url, destination, hashCheck: DownloadHashCheck.Md5(""));

        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Download_ReportsTheVerifyingPhaseBeforeCompleting()
    {
        var payload = Payload(1000);
        var handler = new FakeHandler(_ => Ok(payload));
        var progress = new CollectingProgress();

        await NewDownloader().DownloadAsync(NewClient(handler), Url, Destination(),
            hashCheck: DownloadHashCheck.Md5(Md5Of(payload)), progress: progress);

        // 校验阶段必须报出来：那段时间没有字节在动，界面不说明就会看起来像卡死。
        Assert.Contains(progress.Reports, report => report.Phase == DownloadPhase.Verifying);
        Assert.Equal(DownloadPhase.Completed, progress.Reports[^1].Phase);
    }

    // ── 失败分类与重试 ──────────────────────────────────────

    [Fact]
    public async Task Download_ResumesAfterTheConnectionDropsMidBody()
    {
        var payload = Payload(1000);
        var destination = Destination();
        var firstAttempt = true;
        var handler = new FakeHandler(request =>
        {
            if (firstAttempt)
            {
                firstAttempt = false;
                // 前 400 字节照常给，然后连接断掉 —— 最该被续上的那种失败。
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CutOffStream(payload[..400])) };
            }

            return Partial(payload[400..], from: 400, total: 1000);
        });

        var result = await NewDownloader(maxAttempts: 2).DownloadAsync(NewClient(handler), Url, destination);

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("bytes=400-", handler.RangeHeaders[1]); // 断点接着传，不是从头
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        Assert.Equal(1000, result.BytesWritten);
    }

    [Fact]
    public async Task Download_GivesUpAfterTheLastAttempt()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("host unreachable"));

        var failure = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            NewDownloader(maxAttempts: 3).DownloadAsync(NewClient(handler), Url, Destination()));

        Assert.Equal(DownloadFailureReason.Network, failure.Reason);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Download_TreatsA4xxAsPermanent()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var failure = await Assert.ThrowsAsync<DownloadFailedException>(() =>
            NewDownloader(maxAttempts: 3).DownloadAsync(NewClient(handler), Url, Destination()));

        // 404 再试一百次也还是 404 —— 归到「服务器拒绝」，不浪费时间退避。
        Assert.Equal(DownloadFailureReason.ServerRejected, failure.Reason);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Download_RetriesWhenTheConnectionGoesSilent()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new SilentStream())
        });

        var failure = await Assert.ThrowsAsync<DownloadFailedException>(() => NewDownloader(maxAttempts: 2, stallMs: 30)
            .DownloadAsync(NewClient(handler), Url, Destination()));

        // 「连上了但一个字节都不给」是最难发现的假死：必须靠活动超时打断，并按可重试处理。
        Assert.Equal(DownloadFailureReason.Stall, failure.Reason);
        Assert.Equal(2, handler.RequestCount);
    }

    // ── 取消 ────────────────────────────────────────────────

    [Fact]
    public async Task Download_KeepsThePartFileWhenTheUserCancels()
    {
        using var cts = new CancellationTokenSource();
        var payload = Payload(400);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CancelOnFirstReadStream(payload, cts))
        });
        var destination = Destination();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewDownloader(stallMs: 5000).DownloadAsync(NewClient(handler), Url, destination,
                cancellationToken: cts.Token));

        // 取消/暂停是正常操作：.part 留着，「继续」才有东西可续。
        Assert.True(File.Exists(ResumableDownloader.GetPartPath(destination)));
        Assert.False(File.Exists(destination));
    }

    // ── 进度 ────────────────────────────────────────────────

    [Fact]
    public async Task Download_ReportsProgressUpToTheFullSize()
    {
        var payload = Payload(200_000);
        var handler = new FakeHandler(_ => Ok(payload));
        var progress = new CollectingProgress();

        await NewDownloader().DownloadAsync(NewClient(handler), Url, Destination(), progress: progress);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal(DownloadPhase.Downloading, progress.Reports[0].Phase);
        Assert.Equal(100, progress.Reports[^1].Percent);
        Assert.Equal(200_000, progress.Reports[^1].BytesReceived);
        Assert.All(progress.Reports, report => Assert.True(report.BytesReceived <= 200_000));
    }

    [Fact]
    public async Task Download_FallsBackToTheDeclaredSizeWhenTheServerOmitsContentLength()
    {
        var payload = Payload(1000);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(payload)
        });
        var progress = new CollectingProgress();

        await NewDownloader().DownloadAsync(NewClient(handler), Url, Destination(),
            expectedSizeBytes: 1000, progress: progress);

        // 服务器不给 Content-Length 时若不用数据源声明的体积兜底，进度会永远停在 0%。
        Assert.Equal(100, progress.Reports[^1].Percent);
    }

    // ── 脚手架 ──────────────────────────────────────────────

    private static readonly Uri Url = new("https://gamebanana.com/dl/1");

    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
            data[i] = (byte)(i % 251); // 非 2 的幂的模数：位置错位/重复一眼就能看出来

        return data;
    }

    private static string Md5Of(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    private static HttpClient NewClient(HttpMessageHandler handler) => new(handler);

    private static ResumableDownloader NewDownloader(int maxAttempts = 1, int stallMs = 5000) =>
        new(options: new ResumableDownloaderOptions
        {
            MaxAttempts = maxAttempts,
            StallTimeout = TimeSpan.FromMilliseconds(stallMs),
            ProgressReportInterval = TimeSpan.Zero, // 每次读都报，断言才有确定的输入
            RetryBackoffBase = TimeSpan.FromMilliseconds(1),
        });

    private string Destination(string name = DefaultName) => Path.Combine(_directory, name);

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Partial(byte[] body, long from, long total)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(body)
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, from + body.Length - 1, total);
        return response;
    }

    /// <summary>记请求、按委托回响应的处理器。够用就好，不引第三方 mock 库。</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string?> RangeHeaders { get; } = [];

        public int RequestCount => RangeHeaders.Count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RangeHeaders.Add(request.Headers.Range?.ToString());
            return Task.FromResult(responder(request));
        }
    }

    /// <summary>同步收集的回调。不用 <c>Progress&lt;T&gt;</c>：那个会把回调 post 到线程池，断言就会和下载赛跑。</summary>
    private sealed class CollectingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = [];

        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    /// <summary>吐完给定字节就抛 <see cref="IOException"/> —— 模拟传到一半连接断掉。</summary>
    private sealed class CutOffStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length)
                throw new IOException("connection reset by peer");

            var take = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var temp = new byte[buffer.Length];
            var read = Read(temp, 0, temp.Length);
            temp.AsSpan(0, read).CopyTo(buffer.Span);
            return new ValueTask<int>(read);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>永远不给数据、也不结束 —— 模拟「假死」的连接（靠活动超时才能打断）。</summary>
    private sealed class SilentStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0; // 只有被取消时才走到这里
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>第一次读出数据后就把调用方取消掉 —— 用来在「已经写了几个字节」之后再模拟用户点暂停。</summary>
    private sealed class CancelOnFirstReadStream(byte[] data, CancellationTokenSource cts) : Stream
    {
        private bool _delivered;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delivered)
            {
                cts.Cancel();
                return ValueTask.FromException<int>(new OperationCanceledException(cancellationToken));
            }

            _delivered = true;
            var take = Math.Min(buffer.Length, data.Length);
            data.AsSpan(0, take).CopyTo(buffer.Span);
            return new ValueTask<int>(take);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>不回 <c>Content-Length</c> 的响应体（模拟分块传输）。</summary>
    private sealed class UnknownLengthContent(byte[] data) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            await stream.WriteAsync(data);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}