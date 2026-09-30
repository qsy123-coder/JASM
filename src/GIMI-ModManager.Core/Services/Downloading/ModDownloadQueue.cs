using Serilog;

namespace GIMI_ModManager.Core.Services.Downloading;

/// <summary>
/// 商店的下载队列：**串行**（同一时刻只有一个任务在传），支持暂停 / 继续 / 取消与进度
/// （PRD Story 3）。它只负责「把文件完整地落到暂存目录」，落下来之后干什么由
/// <see cref="CompletedHandler"/> 决定（入库 / 拉起安装向导）。
///
/// 几个刻意的取舍：
/// <list type="bullet">
///   <item><b>只在内存里排队</b>：应用重启不恢复任务（PRD 明确不做跨进程续传）。但暂存目录里的
///         <c>.part</c> 留着 —— 同一个文件再次入队时路径一模一样，于是自动接着传。</item>
///   <item><b>暂存目录独立于归档缓存</b>：<c>ModArchiveRepository</c> 在初始化时会删掉归档目录里
///         所有不合规名字的文件，<c>.part</c> 放那儿会被当场清掉。所以下载先落暂存，下完再入库。</item>
///   <item><b>重试/退避/活动超时都不在这里</b>：那是 <see cref="ResumableDownloader"/> 的活，
///         队列只负责「谁先谁后」和「用户想停还是想取消」。</item>
/// </list>
///
/// 事件与快照可能来自**任意线程**（工作线程 / 下载回调），界面侧订阅 <see cref="Changed"/>
/// 后要自己切回 UI 线程。
/// </summary>
public sealed class ModDownloadQueue : IDisposable
{
    /// <summary>下载用的 <see cref="HttpClient"/> 的 DI 名字（要的是长超时、不带 API 限流的那个 client）。</summary>
    public const string HttpClientName = "ModStoreDownload";

    private readonly HttpClient _httpClient;
    private readonly ResumableDownloader _downloader;
    private readonly ILogger _logger;

    /// <summary>所有状态都在它下面改；<see cref="Changed"/> 一律在锁**外**触发（订阅方可能回调队列）。</summary>
    private readonly object _gate = new();

    private readonly List<ModDownloadItem> _items = [];
    private readonly Dictionary<ModDownloadKey, ModDownloadItem> _byKey = [];

    /// <summary>「有新活了」的信号：入队 / 继续时 Release 一次，工作线程消费。</summary>
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    private readonly CancellationTokenSource _shutdown = new();

    private CancellationTokenSource? _currentCancellation;
    private ModDownloadItem? _current;
    private Task? _worker;
    private bool _disposed;

    /// <param name="httpClient">下载用的 client（建议 <see cref="HttpClientName"/> 那个具名注册）。</param>
    /// <param name="stagingDirectory">暂存目录（<c>.part</c> 与成品都放这儿）。目录不存在会自动建。</param>
    /// <param name="logger">不传就用 Serilog 静态 logger。</param>
    /// <param name="downloaderOptions">下载器参数（重试次数 / 活动超时 / 退避），不传用默认。</param>
    public ModDownloadQueue(HttpClient httpClient, string stagingDirectory, ILogger? logger = null,
        ResumableDownloaderOptions? downloaderOptions = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);

        _httpClient = httpClient;
        _logger = logger ?? Log.ForContext<ModDownloadQueue>();
        _downloader = new ResumableDownloader(_logger, downloaderOptions);

        StagingDirectory = Path.GetFullPath(stagingDirectory);
    }

    /// <summary>暂存目录全路径。</summary>
    public string StagingDirectory { get; }

    /// <summary>
    /// 下载成功（校验也过了）之后调用，在队列工作线程上 <c>await</c>。
    ///
    /// ⚠️ 实现里**不要等用户交互** —— 拉起安装向导后立刻返回。队列在这一步是串行的，
    /// 停在这儿后面的任务就全排着不动了。
    /// </summary>
    public Func<ModDownloadItem, CancellationToken, Task>? CompletedHandler { get; set; }

    /// <summary>队列内容或任一任务状态变化时触发（可能与上一次同值 —— 订阅方按「整行刷新」处理即可）。</summary>
    public event EventHandler? Changed;

    /// <summary>当前队列的**快照**（按入队顺序）。</summary>
    public IReadOnlyList<ModDownloadItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToArray();
            }
        }
    }

    /// <summary>
    /// 入队一个文件；同一个文件已经在队里时**不重复排**，返回既有任务
    /// （用户连点两下不该下两份）。既有任务若已失败/已暂停，就顺手让它重新排队 ——
    /// 「再点一次部署」的自然含义就是「再试一次」。
    /// </summary>
    public ModDownloadItem Enqueue(ModDownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        ModDownloadItem item;
        bool wake;
        lock (_gate)
        {
            if (_byKey.TryGetValue(request.Key, out var existing))
            {
                wake = Retry(existing);
                _logger.Debug("Download already queued for {Key} (state {State})", request.Key, existing.State);
                item = existing;
            }
            else
            {
                item = new ModDownloadItem(request, BuildDestinationPath(request));
                _items.Add(item);
                _byKey[item.Key] = item;
                EnsureWorker();
                wake = true;
            }
        }

        if (wake)
            _wake.Release();

        Raise();
        return item;
    }

    /// <summary>按标识找任务（界面用它判断某个文件是不是已经在队里）。</summary>
    public bool TryGetItem(ModDownloadKey key, out ModDownloadItem item)
    {
        lock (_gate)
        {
            return _byKey.TryGetValue(key, out item!);
        }
    }

    /// <summary>
    /// 暂停。正在下载的会被立刻打断（<c>.part</c> **保留**，继续时走 <c>Range</c> 从断点续）；
    /// 还没轮到的就直接标成已暂停，工作线程会跳过它。
    /// </summary>
    public void Pause(ModDownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            if (item.IsFinished || item.State == ModDownloadState.Paused)
                return;

            item.PauseRequested = true;
            item.BytesPerSecond = 0;
            item.State = ModDownloadState.Paused;

            if (ReferenceEquals(_current, item))
                toCancel = _currentCancellation;
        }

        // 在锁外打断：下载回调可能正好在往锁里挤，别把它们串成一串。
        CancelQuietly(toCancel);
        Raise();
    }

    /// <summary>
    /// 继续（暂停中或失败的都能继续）。失败的从已有 <c>.part</c> 接着传；
    /// 若失败原因是哈希不符（<c>.part</c> 已被删除）则从头重下。
    /// </summary>
    public void Resume(ModDownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        lock (_gate)
        {
            if (!Retry(item))
                return;
        }

        _wake.Release();
        Raise();
    }

    /// <summary>
    /// 取消：任务立刻从队列里消失，<c>.part</c> 与它的暂存目录一并删掉
    /// （在下载中的要等工作线程把文件句柄放开才能删，所以删除会稍晚几毫秒）。
    /// </summary>
    public void Cancel(ModDownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        bool deferred = false;
        CancellationTokenSource? toCancel = null;
        lock (_gate)
        {
            var isCurrent = ReferenceEquals(_current, item);
            item.CancelRequested = true;

            if (isCurrent)
            {
                // 正在下载的必须**立刻**打断。只把它从列表里摘掉是不够的：下载线程会继续读到
                // 活动超时为止（几秒），这段时间里用户看到的是「取消没反应」，
                // 而且半截文件还被占用着删不掉。
                deferred = true;
                toCancel = _currentCancellation;
            }

            RemoveLocked(item);
        }

        CancelQuietly(toCancel);

        if (!deferred)
            DeleteStaging(item);

        Raise();
    }

    /// <summary>取消全部未完成的任务（含正在下载的那个）。</summary>
    public void CancelAll()
    {
        var active = Items.Where(i => !i.IsFinished).ToArray();
        foreach (var item in active)
            Cancel(item);
    }

    /// <summary>移掉已完成/已失败的行（界面上的「清除」）。</summary>
    public void ClearFinished()
    {
        List<ModDownloadItem> finished;
        lock (_gate)
        {
            finished = _items.Where(i => i.IsFinished).ToList();
            foreach (var item in finished)
                RemoveLocked(item);
        }

        foreach (var item in finished)
            DeleteStaging(item);

        if (finished.Count > 0)
            Raise();
    }

    /// <summary>
    /// 停掉队列并等工作线程退出（应用退出 / 测试收尾）。
    /// **不删</b> <c>.part</c>：应用退出不是用户按了取消，半截文件要留到下次。
    /// </summary>
    public async Task StopAsync()
    {
        Task? worker;
        CancellationTokenSource? toCancel;
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested)
                return;
            _shutdown.Cancel();
            worker = _worker;
            toCancel = _currentCancellation;
        }

        CancelQuietly(toCancel);
        _wake.Release();

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消本来就是我们要的。
            }
        }
    }

    /// <summary>
    /// 只取消、不等待（退出路径上用）。刻意不做 <c>StopAsync().Wait()</c>：
    /// <see cref="CompletedHandler"/> 可能要等 UI 线程，而退出时 UI 线程正卡在 Dispose 上。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        CancellationTokenSource? toCancel;
        lock (_gate)
        {
            toCancel = _currentCancellation;
            if (!_shutdown.IsCancellationRequested)
                _shutdown.Cancel();
        }

        CancelQuietly(toCancel);
        _wake.Release(); // 把工作线程从等待里叫醒，让它自己看到 shutdown 退出
        _shutdown.Dispose();
    }

    /// <summary>把任务重新排到队尾（暂停/失败的都能重排）。返回是否真的改了状态。</summary>
    private static bool Retry(ModDownloadItem item)
    {
        if (item.State is ModDownloadState.Queued or ModDownloadState.Downloading or ModDownloadState.Verifying)
            return false;

        item.PauseRequested = false;
        item.CancelRequested = false;
        item.ErrorMessage = null;
        item.FailureReason = null;
        item.FollowUpError = null;
        item.BytesPerSecond = 0;
        item.State = ModDownloadState.Queued;
        return true;
    }

    /// <summary>起工作线程（只在第一个任务入队时起，队列空着时不占线程）。</summary>
    private void EnsureWorker() => _worker ??= Task.Run(WorkerLoopAsync);

    /// <summary>
    /// 工作线程：取下一个排队中的任务 → 跑完 → 再取。整体套一层兜底，
    /// **绝不能让它因为意外异常退出** —— 它一退出队列就永久不干活，且表面上看不出来。
    /// </summary>
    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            try
            {
                await _wake.WaitAsync().ConfigureAwait(false);

                if (_shutdown.IsCancellationRequested)
                    return;

                var item = TakeNextQueued();
                if (item is null)
                    continue; // 只有暂停/完成/失败的任务：回去等下一次唤醒，不能空转

                await RunItemAsync(item).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return; // Dispose 收了信号量：正常收尾
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Download queue worker hit an unexpected error, continuing");
            }
        }
    }

    private ModDownloadItem? TakeNextQueued()
    {
        lock (_gate)
        {
            return _items.FirstOrDefault(i => i.State == ModDownloadState.Queued);
        }
    }

    /// <summary>跑一个任务：下载 → 校验 → 交给 <see cref="CompletedHandler"/>。异常一律就地转成任务状态。</summary>
    private async Task RunItemAsync(ModDownloadItem item)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);

        lock (_gate)
        {
            // 用户可能正好在「取到它」和「开跑」之间按了暂停/取消：那种情况下不能开跑，
            // 否则这次暂停会被下面的 State = Downloading 悄悄抹掉。
            if (item.PauseRequested || item.CancelRequested)
                return;

            _current = item;
            _currentCancellation = cancellation;
            item.State = ModDownloadState.Downloading;
            item.ErrorMessage = null;
            item.FailureReason = null;
            item.FollowUpError = null;
        }

        Raise();

        // 上游没给 md5 时**跳过校验**（不是校验失败）：拿空值去比会删掉一份好文件。
        DownloadHashCheck? hashCheck = string.IsNullOrWhiteSpace(item.ExpectedMd5)
            ? null
            : DownloadHashCheck.Md5(item.ExpectedMd5);

        try
        {
            var result = await _downloader.DownloadAsync(_httpClient, item.DownloadUrl, item.DestinationPath,
                    hashCheck, item.FileSizeBytes, new ItemProgressReporter(this, item), cancellation.Token)
                .ConfigureAwait(false);

            lock (_gate)
            {
                item.State = ModDownloadState.Completed;
                item.BytesReceived = result.BytesWritten;
                item.TotalBytes = result.BytesWritten;
                item.BytesPerSecond = 0;
                // 文件已经下完并落盘（句柄已放开），从这一刻起它不再算「正在跑」：
                // 用户此时取消会立刻删掉暂存文件，而不是等一个已经结束的下载。
                _current = null;
                _currentCancellation = null;
            }

            _logger.Information("Download finished for {Key}: {File} ({Bytes} bytes)", item.Key, item.FileName,
                result.BytesWritten);
            Raise();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (item.CancelRequested)
            {
                // 取消：半截文件没有价值，删掉（连同它的暂存目录）。
                DeleteStaging(item);
                _logger.Information("Download canceled for {Key}, staged files removed", item.Key);
            }
            else
            {
                // 暂停 / 应用退出：.part 留着，继续时从这里续。
                lock (_gate)
                {
                    // 用户可能已经在这一瞬间按了「继续」（那时状态已经是 Queued）——
                    // 不能被这里覆盖回 Paused：那样任务就永远停在「已暂停」，怎么点都没反应。
                    if (item.State is ModDownloadState.Downloading or ModDownloadState.Verifying)
                        item.State = ModDownloadState.Paused;

                    item.BytesPerSecond = 0;
                }

                _logger.Information("Download interrupted for {Key} at {Bytes} bytes, .part kept", item.Key,
                    item.BytesReceived);
            }
        }
        catch (DownloadFailedException ex)
        {
            lock (_gate)
            {
                item.State = ModDownloadState.Failed;
                item.ErrorMessage = ex.Message;
                item.FailureReason = ex.Reason;
                item.BytesPerSecond = 0;
            }

            _logger.Warning(ex, "Download failed for {Key} ({File})", item.Key, item.FileName);
        }
        catch (Exception ex)
        {
            // 下载器理论上只会抛上面两类；真出了别的，也不能让任务卡在「正在下载」上。
            lock (_gate)
            {
                item.State = ModDownloadState.Failed;
                item.ErrorMessage = $"下载失败：{ex.Message}";
                item.BytesPerSecond = 0;
            }

            _logger.Error(ex, "Unexpected error while downloading {Key} ({File})", item.Key, item.FileName);
        }
        finally
        {
            lock (_gate)
            {
                _current = null;
                _currentCancellation = null;
            }

            Raise();
        }

        if (item.State == ModDownloadState.Completed && CompletedHandler is { } handler)
        {
            try
            {
                await handler(item, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 交接阶段被取消：文件是好的，什么都不用做。
            }
            catch (Exception ex)
            {
                // 文件本身没问题，所以不标 Failed（那会让「继续」白下一遍）——单独记一条后续错误。
                lock (_gate)
                {
                    item.FollowUpError = ex.Message;
                }

                _logger.Error(ex, "Post-download handling failed for {Key}", item.Key);
                Raise();
            }
        }
    }

    private void RemoveLocked(ModDownloadItem item)
    {
        _items.Remove(item);
        // 只在下标确实指向它时才移除：同一 Key 的新任务可能已经顶替上去了。
        if (_byKey.TryGetValue(item.Key, out var current) && ReferenceEquals(current, item))
            _byKey.Remove(item.Key);
    }

    /// <summary>
    /// 每个文件一个暂存子目录。文件名固定 = 同一个文件再次下载时会命中同一路径，
    /// <c>.part</c> 于是自动接上；取消时整目录删掉也不会碰到别人的半截文件。
    /// </summary>
    private string BuildDestinationPath(ModDownloadRequest request)
    {
        var folder = Path.Combine(StagingDirectory, $"{request.Key.ModId}_{request.Key.ModFileId}");
        return Path.Combine(folder, SanitizeFileName(request.FileName, request.Key));
    }

    /// <summary>
    /// 远端给的 <c>_sFile</c> 要当本地文件名用：先剥掉任何路径成分（防 <c>../</c> 之类），
    /// 再换掉 Windows 不允许的字符 —— 否则 <c>FileStream</c> 会直接抛，用户只看到「下载失败」。
    /// </summary>
    private static string SanitizeFileName(string? fileName, ModDownloadKey key)
    {
        var fallback = $"mod_{key.ModId}_file_{key.ModFileId}.bin";
        var name = Path.GetFileName(fileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    private void DeleteStaging(ModDownloadItem item)
    {
        // 整个子目录删掉：里面有 .part（如果有）和成品。目录名是从 Key 拼的，不可能指到别处。
        var folder = Path.GetDirectoryName(item.DestinationPath);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return;

        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            // 删不掉（文件还被谁占着）不该影响队列本身，下次入队时会覆盖同一路径。
            _logger.Warning(ex, "Could not remove staging folder {Folder}", folder);
        }
    }

    private static void CancelQuietly(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务已经跑完并回收了 CTS，没什么可取消的。
        }
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // 订阅方（界面）抛异常不能把工作线程带走。
            _logger.Warning(ex, "A download queue subscriber threw on Changed");
        }
    }

    /// <summary>
    /// 下载器在**它自己的线程**上回调，所以这里用一个同步的 <see cref="IProgress{T}"/>：
    /// <c>Progress&lt;T&gt;</c> 会把回调 Post 到捕获的上下文，测试里会变成线程池上的乱序更新
    /// （甚至落在任务结束之后），进度就不可断言了。
    /// </summary>
    private sealed class ItemProgressReporter(ModDownloadQueue queue, ModDownloadItem item) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            lock (queue._gate)
            {
                // 用户刚按下暂停/取消时，可能还有一次回调在路上：不能让它把状态改回「正在下载」。
                if (item.PauseRequested || item.CancelRequested)
                    return;

                item.BytesReceived = value.BytesReceived;
                if (value.TotalBytes is { } total)
                    item.TotalBytes = total;
                item.BytesPerSecond = value.BytesPerSecond;

                // Completed 那一拍紧接着就是工作线程收尾（它会置成 Completed），这里保持「正在下载」。
                if (value.Phase == DownloadPhase.Verifying)
                    item.State = ModDownloadState.Verifying;
            }

            queue.Raise();
        }
    }
}