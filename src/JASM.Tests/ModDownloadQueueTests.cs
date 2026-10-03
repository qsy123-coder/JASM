using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services.Downloading;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;
using GIMI_ModManager.Core.Services.GameBanana.Models;
using Serilog;

namespace JASM.Tests;

/// <summary>
/// 下载队列（<see cref="ModDownloadQueue"/>）的行为锁。
///
/// 与 <see cref="ResumableDownloaderTests"/> 一样全部用假 <see cref="HttpMessageHandler"/>：
/// 这里要验的是**队列的调度**（串行、暂停、继续、取消、去重），网络那层已经在下载器的测试里验过了。
/// 假处理器诚实地支持 <c>Range</c>，所以「暂停后继续」走的是真的 206 续传，不是假装。
///
/// 队列是异步的，断言前一律 <see cref="WaitUntilAsync"/> 等它自己走到那一步 —— 不用固定 <c>Delay</c>
/// （那在 CI 上必然偶发）。
/// </summary>
public class ModDownloadQueueTests : IDisposable
{
    private static readonly byte[] DefaultPayload = Payload(1000);

    private readonly string _directory;

    public ModDownloadQueueTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "jasm-download-queue-tests", Guid.NewGuid().ToString("N"));
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
            // 临时目录清不掉不影响任何断言。
        }
    }

    // ── 基本调度 ────────────────────────────────────────────

    [Fact]
    public async Task Enqueue_DownloadsAndMarksCompleted()
    {
        var payload = Payload(2048);
        var handler = new StoreHandler(payload);
        using var queue = NewQueue(handler);

        var item = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));

        await WaitUntilAsync(() => item.State == ModDownloadState.Completed, "任务应当完成");

        Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.Equal(payload.Length, item.BytesReceived);
        Assert.Equal(100, item.Percent);
        Assert.Null(item.ErrorMessage);
        // 完成后 .part 必须已经被改名成成品，不能留个半截文件在暂存目录里。
        Assert.False(File.Exists(item.PartPath));
    }

    [Fact]
    public async Task Enqueue_IgnoresTheSameFileTwice()
    {
        var handler = new StoreHandler(DefaultPayload);
        using var queue = NewQueue(handler);

        var first = queue.Enqueue(Request("1", md5: Md5Of(DefaultPayload), size: DefaultPayload.Length));
        var second = queue.Enqueue(Request("1", md5: Md5Of(DefaultPayload), size: DefaultPayload.Length));
        await WaitUntilAsync(() => first.State == ModDownloadState.Completed, "任务应当完成");

        // 连点两下不该下两份：同一个文件只排一次，第二次返回同一个任务。
        Assert.Same(first, second);
        Assert.Single(queue.Items);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Enqueue_RunsOneTaskAtATime()
    {
        var gate = new GatedStream(DefaultPayload, firstBlockBytes: 400);
        var handler = new StoreHandler(DefaultPayload)
        {
            Override = (_, index) => index == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gate) }
                : null
        };
        using var queue = NewQueue(handler);

        var first = queue.Enqueue(Request("1", md5: Md5Of(DefaultPayload), size: DefaultPayload.Length));
        var second = queue.Enqueue(Request("2", name: "b.zip", md5: Md5Of(DefaultPayload),
            size: DefaultPayload.Length));

        // 第一个卡在半路时，第二个必须还在排队 —— 串行是队列存在的理由（一次一个活动任务）。
        await WaitUntilAsync(() => first.State == ModDownloadState.Downloading, "第一个任务应当开始");
        await WaitUntilAsync(() => first.BytesReceived > 0, "第一个任务应当已经收到数据");
        Assert.Equal(ModDownloadState.Queued, second.State);

        gate.Release();
        await WaitUntilAsync(() => first.State == ModDownloadState.Completed && second.IsFinished,
            "两个任务都应当完成");
    }

    [Fact]
    public async Task Enqueue_KeepsQueuedOrder()
    {
        var handler = new StoreHandler(DefaultPayload);
        using var queue = NewQueue(handler);

        var first = queue.Enqueue(Request("1", md5: Md5Of(DefaultPayload), size: DefaultPayload.Length));
        var second = queue.Enqueue(Request("2", name: "b.zip", md5: Md5Of(DefaultPayload),
            size: DefaultPayload.Length));

        await WaitUntilAsync(() => first.IsFinished && second.IsFinished, "两个任务都应当完成");

        // 先入队的先跑（队列语义），请求顺序就是证据。
        Assert.Equal(first, queue.Items[0]);
        Assert.Equal(second, queue.Items[1]);
        Assert.Equal(new[] { "/dl/1", "/dl/2" }, handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task TryGetItem_FindsQueuedFiles()
    {
        var handler = new StoreHandler(DefaultPayload);
        using var queue = NewQueue(handler);

        var item = queue.Enqueue(Request("1", md5: Md5Of(DefaultPayload), size: DefaultPayload.Length));

        Assert.True(queue.TryGetItem(item.Key, out var found));
        Assert.Same(item, found);
        Assert.False(queue.TryGetItem(new ModDownloadKey("100", "999"), out _));

        await queue.StopAsync();
    }

    // ── 暂停 / 继续 ─────────────────────────────────────────

    [Fact]
    public async Task Pause_KeepsThePartFileAndLetsTheNextTaskRun()
    {
        var payload = Payload(1000);
        var gate = new GatedStream(payload, firstBlockBytes: 400);
        var handler = new StoreHandler(payload)
        {
            Override = (_, index) => index == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gate) }
                : null
        };
        using var queue = NewQueue(handler);

        var paused = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        var other = queue.Enqueue(Request("2", name: "b.zip", md5: Md5Of(payload), size: payload.Length));

        await WaitUntilAsync(() => paused.BytesReceived > 0, "第一个任务应当已经收到数据");
        queue.Pause(paused);

        // 暂停不是取消：.part 留着（下次从这里续），成品不存在，后面的任务顶上。
        await WaitUntilAsync(() => other.State == ModDownloadState.Completed, "第二个任务应当接着跑完");
        Assert.Equal(ModDownloadState.Paused, paused.State);
        Assert.True(File.Exists(paused.PartPath));
        Assert.Equal(400, new FileInfo(paused.PartPath).Length);
        Assert.False(File.Exists(paused.DestinationPath));
    }

    [Fact]
    public async Task Pause_OnAQueuedTask_KeepsItFromStarting()
    {
        var payload = Payload(1000);
        var gate = new GatedStream(payload, firstBlockBytes: 400);
        var handler = new StoreHandler(payload)
        {
            Override = (_, index) => index == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gate) }
                : null
        };
        using var queue = NewQueue(handler);

        var running = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        var queued = queue.Enqueue(Request("2", name: "b.zip", md5: Md5Of(payload), size: payload.Length));

        await WaitUntilAsync(() => running.BytesReceived > 0, "第一个任务应当已经收到数据");
        queue.Pause(queued); // 还没轮到就暂停
        gate.Release();

        await WaitUntilAsync(() => running.State == ModDownloadState.Completed, "第一个任务应当完成");
        await Task.Delay(50); // 给工作线程一点时间去「错误地」启动第二个任务
        Assert.Equal(ModDownloadState.Paused, queued.State);
        Assert.DoesNotContain("/dl/2", handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task Resume_ContinuesWithARangeRequest()
    {
        var payload = Payload(1000);
        var gate = new GatedStream(payload, firstBlockBytes: 400);
        var handler = new StoreHandler(payload)
        {
            Override = (_, index) => index == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gate) }
                : null
        };
        using var queue = NewQueue(handler);

        var item = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => item.BytesReceived > 0, "任务应当已经收到数据");
        queue.Pause(item);
        await WaitUntilAsync(() => item.State == ModDownloadState.Paused, "任务应当处于暂停");

        queue.Resume(item);
        await WaitUntilAsync(() => item.State == ModDownloadState.Completed,
            () => $"继续后应当下完（当前 {item.State} / {item.ErrorMessage}）");

        // 续传必须带上断点（而不是重头下），并且最终内容是完整的。
        var ranges = handler.Requests.Where(r => r.Url == "/dl/1").Select(r => r.Range).ToArray();
        Assert.Equal("bytes=400-", ranges[^1]);
        Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.Equal(100, item.Percent);
    }

    // ── 取消 ────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_RemovesTheTaskAndDeletesTheStagingFiles()
    {
        var payload = Payload(1000);
        var gate = new GatedStream(payload, firstBlockBytes: 400);
        var handler = new StoreHandler(payload)
        {
            Override = (_, index) => index == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gate) }
                : null
        };
        using var queue = NewQueue(handler);

        var item = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => item.BytesReceived > 0, "任务应当已经收到数据");

        var folder = Path.GetDirectoryName(item.DestinationPath)!;
        queue.Cancel(item);

        // 取消后立刻从队列里消失；文件句柄还在下载线程手里，所以目录删除会稍晚一点。
        Assert.Empty(queue.Items);
        await WaitUntilAsync(() => !Directory.Exists(folder),
            () => $"取消后暂存目录应当被清掉（{item.State} / {item.FailureReason} / {item.ErrorMessage}；还剩 " +
                  $"{(Directory.Exists(folder) ? string.Join(", ", Directory.GetFiles(folder)) : "无目录")}）");
        Assert.False(File.Exists(item.DestinationPath));
        Assert.False(File.Exists(item.PartPath));
    }

    [Fact]
    public async Task Cancel_OnAQueuedTask_RemovesItWithoutAskingTheServer()
    {
        var payload = Payload(1000);
        var gate = new GatedStream(payload, firstBlockBytes: 400);
        var handler = new StoreHandler(payload)
        {
            Override = (_, index) => index == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gate) }
                : null
        };
        using var queue = NewQueue(handler);

        var running = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        var queued = queue.Enqueue(Request("2", name: "b.zip", md5: Md5Of(payload), size: payload.Length));

        await WaitUntilAsync(() => running.BytesReceived > 0, "第一个任务应当已经收到数据");
        queue.Cancel(queued);
        gate.Release();

        await WaitUntilAsync(() => running.State == ModDownloadState.Completed, "第一个任务应当完成");
        Assert.Single(queue.Items);
        Assert.DoesNotContain("/dl/2", handler.Requests.Select(r => r.Url));
    }

    [Fact]
    public async Task CancelAll_ClearsEverythingNotFinished()
    {
        var payload = Payload(1000);
        var gate = new GatedStream(payload, firstBlockBytes: 400);
        var handler = new StoreHandler(payload)
        {
            Override = (_, index) => index == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gate) }
                : null
        };
        using var queue = NewQueue(handler);

        queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        queue.Enqueue(Request("2", name: "b.zip", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => queue.Items.Any(i => i.BytesReceived > 0), "第一个任务应当已经收到数据");

        queue.CancelAll();

        Assert.Empty(queue.Items);
    }

    [Fact]
    public async Task ClearFinished_RemovesTheFinishedRowsOnly()
    {
        var payload = Payload(1000);
        var handler = new StoreHandler(payload);
        using var queue = NewQueue(handler);

        var item = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => item.State == ModDownloadState.Completed, "任务应当完成");

        queue.ClearFinished();

        Assert.Empty(queue.Items);
        Assert.False(Directory.Exists(Path.GetDirectoryName(item.DestinationPath)!));
    }

    // ── 失败与重试 ──────────────────────────────────────────

    [Fact]
    public async Task FailedDownload_KeepsThePartFileAndCanBeResumed()
    {
        var payload = Payload(1000);
        var handler = new StoreHandler(payload) { FailFirstRequests = 1 };
        using var queue = NewQueue(handler);

        var item = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => item.State == ModDownloadState.Failed, "第一次应当失败");
        Assert.Equal(DownloadFailureReason.Network, item.FailureReason);
        Assert.False(string.IsNullOrWhiteSpace(item.ErrorMessage));

        // 失败不是终局：用户点「继续」时要接着下，而不是把它踢出队列。
        queue.Resume(item);
        await WaitUntilAsync(() => item.State == ModDownloadState.Completed, "继续后应当下完");

        Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
        Assert.Null(item.ErrorMessage);
    }

    [Fact]
    public async Task Enqueue_OnAFailedItem_RetriesIt()
    {
        var payload = Payload(1000);
        var handler = new StoreHandler(payload) { FailFirstRequests = 1 };
        using var queue = NewQueue(handler);

        var item = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => item.State == ModDownloadState.Failed, "第一次应当失败");

        // 「再点一次部署」的自然含义就是「再试一次」。
        var again = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        Assert.Same(item, again);

        await WaitUntilAsync(() => item.State == ModDownloadState.Completed, "再入队后应当下完");
    }

    [Fact]
    public async Task HashMismatch_FailsWithoutLeavingAFileAndRetriesFromScratch()
    {
        var payload = Payload(1000);
        var handler = new StoreHandler(payload)
        {
            // 第一次给一份坏数据（模拟 CDN 上的残包），之后给对的。
            Override = (_, index) => index == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload(999)) }
                : null
        };
        using var queue = NewQueue(handler);

        var item = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => item.State == ModDownloadState.Failed, "校验应当失败");

        Assert.Equal(DownloadFailureReason.HashMismatch, item.FailureReason);
        Assert.False(File.Exists(item.DestinationPath));
        // 坏数据留着只会在下次续出更坏的东西，所以 .part 被删了 -> 重试必须从头下。
        Assert.False(File.Exists(item.PartPath));

        queue.Resume(item);
        await WaitUntilAsync(() => item.State == ModDownloadState.Completed, "重试应当成功");
        Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
    }

    [Fact]
    public async Task Enqueue_SkipsVerificationWhenTheApiGaveNoHash()
    {
        var payload = Payload(1000);
        var handler = new StoreHandler(payload);
        using var queue = NewQueue(handler);

        // 上游 _sMd5Checksum 可能是空串：那时只能不校验（拿空值去比会删掉一份好文件）。
        var item = queue.Enqueue(Request("1", md5: null, size: payload.Length));

        await WaitUntilAsync(() => item.State == ModDownloadState.Completed, "任务应当完成");
        Assert.Equal(payload, await File.ReadAllBytesAsync(item.DestinationPath));
    }

    // ── 进度 ────────────────────────────────────────────────

    [Fact]
    public async Task ReportsProgressAndTheVerifyingPhase()
    {
        var payload = Payload(5000);
        var handler = new StoreHandler(payload);
        using var queue = NewQueue(handler);
        var seenStates = new List<ModDownloadState>();
        queue.Changed += (_, _) =>
        {
            lock (seenStates)
            {
                seenStates.Add(queue.Items[0].State);
            }
        };

        var item = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => item.State == ModDownloadState.Completed, "任务应当完成");

        lock (seenStates)
        {
            // 校验阶段没有字节在动，界面得能区分出来（否则进度条看起来就是卡住了）。
            Assert.Contains(ModDownloadState.Verifying, seenStates);
            Assert.Contains(ModDownloadState.Downloading, seenStates);
        }

        Assert.Equal(100, item.Percent);
        Assert.Equal(0, item.BytesPerSecond); // 停下来之后不该还显示着收尾那一拍的速度
    }

    // ── 交接与暂存路径 ──────────────────────────────────────

    [Fact]
    public async Task CompletedHandler_GetsTheFinishedFile()
    {
        var payload = Payload(1000);
        var handler = new StoreHandler(payload);
        ModDownloadItem? handed = null;
        byte[]? content = null;
        using var queue = NewQueue(handler, (item, _) =>
        {
            handed = item;
            content = File.ReadAllBytes(item.DestinationPath);
            return Task.CompletedTask;
        });

        var item = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => handed is not null, "交接回调应当被调用");

        Assert.Same(item, handed);
        Assert.Equal(payload, content);
    }

    [Fact]
    public async Task CompletedHandler_ThatThrowsDoesNotPoisonTheQueue()
    {
        var payload = Payload(1000);
        var handler = new StoreHandler(payload);
        using var queue = NewQueue(handler, (_, _) => throw new InvalidOperationException("装不上"));

        var first = queue.Enqueue(Request("1", md5: Md5Of(payload), size: payload.Length));
        await WaitUntilAsync(() => first.FollowUpError is not null, "交接失败应当被记下来");

        // 文件本身是好的：不能标成下载失败（那会让「继续」白下一遍），只记一条后续错误。
        Assert.Equal(ModDownloadState.Completed, first.State);
        Assert.Equal("装不上", first.FollowUpError);
        Assert.True(File.Exists(first.DestinationPath));

        // 而且队列不能被这一次异常带停。
        var second = queue.Enqueue(Request("2", name: "b.zip", md5: Md5Of(payload),
            size: payload.Length));
        await WaitUntilAsync(() => second.State == ModDownloadState.Completed, "后面的任务应当照常跑");
    }

    [Fact]
    public async Task DestinationPath_IsDerivedFromTheKeyAndSanitized()
    {
        var handler = new StoreHandler(DefaultPayload);
        using var queue = NewQueue(handler);

        var item = queue.Enqueue(Request("1831976", modId: "658343",
            name: "../../其它目录/坏:名字?.zip", md5: null, size: DefaultPayload.Length));

        // 远端给的文件名要当本地文件名用：不能带路径成分，也不能留 Windows 不认的字符。
        Assert.StartsWith(Path.GetFullPath(Path.Combine(queue.StagingDirectory, "658343_1831976")),
            item.DestinationPath);
        var fileName = Path.GetFileName(item.DestinationPath);
        Assert.DoesNotContain("..", fileName);
        Assert.DoesNotContain(":", fileName);
        Assert.DoesNotContain("?", fileName);
        Assert.EndsWith(".zip", fileName);

        await queue.StopAsync();
    }

    [Fact]
    public async Task ANewQueue_DoesNotResumeOldTasksButKeepsThePartFile()
    {
        var payload = Payload(1000);
        var gate = new GatedStream(payload, firstBlockBytes: 400);
        var handler = new StoreHandler(payload)
        {
            Override = (_, index) => index == 0
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(gate) }
                : null
        };

        // 第一个队列 = 上一个进程：下到一半「应用退出」。
        string destination;
        using (var first = NewQueue(handler))
        {
            var item = first.Enqueue(Request("7", name: "big.zip", md5: Md5Of(payload), size: payload.Length));
            await WaitUntilAsync(() => item.BytesReceived > 0, "任务应当已经收到数据");
            destination = item.DestinationPath;

            await first.StopAsync(); // 退出不是取消：.part 必须留着
        }

        Assert.True(File.Exists(ResumableDownloader.GetPartPath(destination)));

        // 第二个队列 = 重启后的进程：不自动恢复任何任务（内存里排的队），但同一个文件再入队时
        // 路径一模一样，于是自动接着上次的断点续传。
        var restarted = new StoreHandler(payload);
        using var second = NewQueue(restarted);
        Assert.Empty(second.Items);

        var again = second.Enqueue(Request("7", name: "big.zip", md5: Md5Of(payload), size: payload.Length));
        Assert.Equal(destination, again.DestinationPath);

        await WaitUntilAsync(() => again.State == ModDownloadState.Completed, "重新入队后应当续传完成");
        Assert.Equal("bytes=400-", restarted.Requests[0].Range);
        Assert.Equal(payload, await File.ReadAllBytesAsync(again.DestinationPath));
    }

    // ── 商店文件的映射 ──────────────────────────────────────

    [Fact]
    public void FromStoreFile_UsesTheUrlTheApiGaveUs()
    {
        var file = StoreFile(fileId: 12345, downloadUrl: "https://gamebanana.com/dl/12345");

        var request = ModDownloadRequest.FromStoreFile(new GbModId(658343), file, "某个 mod");

        Assert.NotNull(request);
        Assert.Equal("https://gamebanana.com/dl/12345", request!.DownloadUrl.ToString());
        Assert.Equal(new ModDownloadKey("658343", "12345"), request.Key);
        Assert.Equal("模组包.zip", request.FileName);
        Assert.Equal(4096, request.FileSizeBytes);
        Assert.Equal(Md5Of(DefaultPayload), request.ExpectedMd5);
    }

    [Fact]
    public void FromStoreFile_FallsBackToTheDownloadUrlBuiltFromTheFileId()
    {
        var file = StoreFile(fileId: 1798486, downloadUrl: null);

        var request = ModDownloadRequest.FromStoreFile(new GbModId(658343), file, null);

        // 上游偶尔不给 _sDownloadUrl；按文件 id 拼是仓库里下载一直用的办法（实测 302 两次后 206）。
        Assert.NotNull(request);
        Assert.Equal("https://gamebanana.com/dl/1798486", request!.DownloadUrl.ToString());
    }

    [Fact]
    public void FromStoreFile_FallsBackToTheModLevelVersion()
    {
        // 文件级 _sVersion 经常整个键都不存在（实测 mod 575376 的 4 个文件一个都没有）。
        var file = StoreFile(fileId: 12345, downloadUrl: null, fileVersion: null, modVersion: "2.1");

        var request = ModDownloadRequest.FromStoreFile(new GbModId(658343), file, "某个 mod");

        Assert.Equal("2.1", request!.Version);
        Assert.Equal("某个 mod", request.ModName);
    }

    [Fact]
    public void FromStoreFile_KeepsTheDeploymentBits()
    {
        // 角色与 mod 页面地址下载时用不上，但**必须跟着请求走**：队列下完就把请求交给部署阶段，
        // 那时界面早翻到别的 mod 上去了，回头再查「这条是谁的」是查不到的。
        var file = StoreFile(fileId: 12345, downloadUrl: null);
        var modPageUrl = new Uri("https://gamebanana.com/mods/658343");

        var request = ModDownloadRequest.FromStoreFile(new GbModId(658343), file, "某个 mod", "Jinhsi", modPageUrl);

        Assert.Equal("Jinhsi", request!.Character);
        Assert.Equal(modPageUrl, request.ModPageUrl);
    }

    [Fact]
    public void FromStoreFile_LeavesTheCharacterNullForUiMods()
    {
        // UI 类 mod 在 GameBanana 上没有子分类（实测）：角色得是 null，部署阶段据此退回「Others」。
        var file = StoreFile(fileId: 12345, downloadUrl: null);

        var request = ModDownloadRequest.FromStoreFile(new GbModId(575376), file, "某个 UI mod");

        Assert.Null(request!.Character);
        Assert.Null(request.ModPageUrl);
    }

    // ── 脚手架 ──────────────────────────────────────────────

    private static readonly Uri DownloadUri = new("https://gamebanana.com/dl/1");

    /// <summary>
    /// 队列不传 logger 时会退回**全局**的 <c>Log.Logger</c>，而 <see cref="GameServiceInitializationTests"/>
    /// 会把全局 logger 换成它自己的 MockLogger，再断言「里面没有 Warning / Error」。
    /// 这个文件里的失败用例（哈希不符、传输中断……）是**故意**要写出 Error 级日志的，
    /// 一落进那个共享列表就会让那边的断言翻车（并发写还会撞坏它的 List）。
    /// 所以这里显式给一个什么都不写的 logger，把两边彻底隔开。
    /// </summary>
    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();

    private static byte[] Payload(int size)
    {
        var data = new byte[size];
        for (var i = 0; i < size; i++)
            data[i] = (byte)(i % 251);

        return data;
    }

    private static string Md5Of(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    private ModDownloadQueue NewQueue(HttpMessageHandler handler,
        Func<ModDownloadItem, CancellationToken, Task>? onCompleted = null, int maxAttempts = 1)
    {
        return new ModDownloadQueue(new HttpClient(handler), Path.Combine(_directory, "staging"),
            SilentLogger,
            downloaderOptions: new ResumableDownloaderOptions
            {
                MaxAttempts = maxAttempts,
                StallTimeout = TimeSpan.FromMilliseconds(500),
                ProgressReportInterval = TimeSpan.Zero, // 每次读都报，断言才有确定的输入
                RetryBackoffBase = TimeSpan.FromMilliseconds(1),
            })
        {
            CompletedHandler = onCompleted
        };
    }

    private static ModDownloadRequest Request(string fileId, string name = "mod.zip", string modId = "100",
        string? md5 = null, long? size = null)
        => new(new ModDownloadKey(modId, fileId), new Uri($"https://gamebanana.com/dl/{fileId}"), name, md5, size);

    private static ModStoreFile StoreFile(int fileId, string? downloadUrl, string? fileVersion = null,
        string? modVersion = null)
    {
        var file = new ApiModFileInfo
        {
            FileId = fileId,
            FileName = "模组包.zip",
            DownloadUrl = downloadUrl!,
            FileSize = 4096,
            Md5Checksum = Md5Of(DefaultPayload),
            Version = fileVersion
        };

        var storeFile = ModStoreFile.TryCreate(file, modVersion, fromArchivedList: false);
        Assert.NotNull(storeFile);
        return storeFile!;
    }

    /// <summary>轮询到条件成立；超时就把条件名报出来，比「断言失败」好查得多。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 5000) =>
        await WaitUntilAsync(condition, () => because, timeoutMs);

    /// <summary>要报的东西只有在超时那一刻才有意义（任务状态、目录内容），所以用惰性消息。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, Func<string> because, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(5);
        }

        Assert.True(condition(), $"等待超时：{because()}");
    }

    /// <summary>
    /// 假 GameBanana：默认**诚实支持** <c>Range</c>（带 <c>Range</c> 就回 206 + 剩余部分），
    /// 于是队列的「暂停后继续」走的是真的断点续传。需要特殊响应时用 <see cref="Override"/>。
    /// </summary>
    private sealed class StoreHandler(byte[] payload) : HttpMessageHandler
    {
        private readonly List<(string Url, string? Range)> _requests = [];

        /// <summary>请求序号（从 0 起）与请求本身 → 响应；返回 null 表示按默认处理。</summary>
        public Func<HttpRequestMessage, int, HttpResponseMessage?>? Override { get; init; }

        /// <summary>前 N 次请求直接抛连接错误（模拟断网 / 连接被重置）。</summary>
        public int FailFirstRequests { get; init; }

        public IReadOnlyList<(string Url, string? Range)> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int index;
            lock (_requests)
            {
                index = _requests.Count;
                _requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Range?.ToString()));
            }

            if (index < FailFirstRequests)
                return Task.FromException<HttpResponseMessage>(new IOException("connection reset by peer"));

            if (Override?.Invoke(request, index) is { } custom)
                return Task.FromResult(custom);

            var start = 0L;
            if (request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } from)
                start = from;

            if (start >= payload.Length)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

            var body = payload[(int)start..];
            return Task.FromResult(start > 0
                ? Partial(body, start, payload.Length)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private static HttpResponseMessage Partial(byte[] body, long from, long total)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(body)
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, from + body.Length - 1, total);
        return response;
    }

    /// <summary>
    /// 先吐 <c>firstBlockBytes</c> 个字节，然后就卡住等 <see cref="Release"/>（或被取消）。
    /// 用它把任务「按」在正在下载的状态上，才能去测暂停/取消。
    /// </summary>
    private sealed class GatedStream(byte[] data, int firstBlockBytes) : Stream
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public void Release() => _released.TrySetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position == 0 && firstBlockBytes > 0)
            {
                var take = Math.Min(Math.Min(buffer.Length, firstBlockBytes), data.Length);
                data.AsSpan(0, take).CopyTo(buffer.Span);
                _position = take;
                return take;
            }

            // 这里等的是「测试放行」，任务被暂停/取消时由 token 打断 —— 正是要的行为。
            await _released.Task.WaitAsync(cancellationToken);

            var remaining = Math.Min(buffer.Length, data.Length - _position);
            if (remaining <= 0)
                return 0;

            data.AsSpan(_position, remaining).CopyTo(buffer.Span);
            _position += remaining;
            return remaining;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}