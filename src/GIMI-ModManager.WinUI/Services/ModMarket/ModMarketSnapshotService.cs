using System.IO.Compression;
using System.Text.Json;
using GIMI_ModManager.WinUI.Models;
using Microsoft.Extensions.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModMarket;

/// <summary>
/// 从 COS 拉兜底快照，带内存缓存与失败冷却。
///
/// 触发场景：Supabase 出口配额超限时，网关对 REST / Auth / Storage 一律回 402，
/// 而 COS 完全不受影响 —— 快照是那段时间里唯一还能拿到市场数据的路径。
///
/// 缓存策略（与 WaveMod 站点那侧对齐）：
/// <list type="bullet">
///   <item>内存缓存 10 分钟 —— 与 <c>ModMarketService</c> 的分类缓存 TTL 一致。</item>
///   <item>刷新失败后冷却 60s，避免网关持续不通时每次翻页都重下一次 500KB。</item>
///   <item>冷却期内**返回旧快照而不是 null**：TTL 过期后刷新失败时，陈旧数据远好于空页。</item>
/// </list>
/// </summary>
public sealed class ModMarketSnapshotService
{
    public const string HttpClientName = "ModMarketSnapshot";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 解压后的字节上限。快照解压出来约 5MB；这个上限是防「对象被换成一个 gzip 炸弹」——
    /// 那是唯一能把用户机器内存吃干净的输入，而 URL 来自可配置项。
    /// </summary>
    private const long MaxInflatedBytes = 32L * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly string _url;

    /// <summary>冷启动时搜索防抖与切分类会同时触发第一次加载，不串行化就会并发下好几份 500KB。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ModMarketSnapshot? _cached;
    private DateTime _cachedAt;
    private DateTime _failedAt = DateTime.MinValue;

    public ModMarketSnapshotService(
        IHttpClientFactory httpClientFactory,
        IOptions<ModMarketOptions> options,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger.ForContext<ModMarketSnapshotService>();
        _url = options.Value.SnapshotUrl;
    }

    /// <summary>取一份可用的快照；连旧快照都没有时返回 null（调用方据此显示失败态）。</summary>
    public async Task<ModMarketSnapshot?> GetAsync(CancellationToken ct = default)
    {
        if (TryGetFresh(out var fresh)) return fresh;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 等锁期间别的调用可能已经把快照刷好了。
            if (TryGetFresh(out fresh)) return fresh;

            if (DateTime.UtcNow - _failedAt < FailureCooldown)
            {
                _logger.Debug("快照刷新仍在冷却中，沿用旧快照（有缓存：{HasCache}）", _cached is not null);
                return _cached;
            }

            return await DownloadAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFresh(out ModMarketSnapshot? snapshot)
    {
        snapshot = _cached;
        return _cached is not null && DateTime.UtcNow - _cachedAt < CacheTtl;
    }

    private async Task<ModMarketSnapshot?> DownloadAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_url))
        {
            _logger.Warning("未配置 Supabase:SnapshotUrl，快照降级不可用");
            _failedAt = DateTime.UtcNow;
            return _cached;
        }

        try
        {
            _logger.Information("下载兜底快照 {Url}", _url);

            // 按需新建(而不是在构造里存一个长命 HttpClient):与 ModMarketService 的用法一致，
            // 也让工厂的连接池轮换照常生效。
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(_url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var generatedAt = response.Content.Headers.LastModified ?? DateTimeOffset.UtcNow;

            await using var compressed = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            await CopyBoundedAsync(gzip, inflated, ct).ConfigureAwait(false);
            inflated.Position = 0;

            // 这份 JsonDocument 绝不能 Dispose（也别用 using）：行里的 drive_links 是 JsonElement，
            // 文档一释放，详情面板再取网盘链接就是 ObjectDisposedException。
            // JsonElement 自身持有对文档的引用，所以不额外保存字段也能保证它活着。
            var document = JsonDocument.Parse(inflated);
            var (mods, rawCount, dropped) = ModMarketJson.ReadRows(document.RootElement, _logger);

            _cached = new ModMarketSnapshot(mods, generatedAt, dropped.Count);
            _cachedAt = DateTime.UtcNow;

            _logger.Information("兜底快照就绪：{Count} 行（原始 {Raw}，丢弃 {Dropped}），生成于 {GeneratedAt}",
                mods.Count, rawCount, dropped.Count, generatedAt);

            return _cached;
        }
        catch (Exception ex)
        {
            // 置冷却但**不清缓存**：让上面那条路径继续把旧快照端出去。
            _failedAt = DateTime.UtcNow;
            _logger.Error(ex, "下载兜底快照失败，{Seconds}s 内不再重试", FailureCooldown.TotalSeconds);
            return _cached;
        }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[81920];

        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read <= 0) return;

            if (destination.Length + read > MaxInflatedBytes)
                throw new InvalidDataException($"快照解压后超过 {MaxInflatedBytes} 字节上限，已中止");

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
}
