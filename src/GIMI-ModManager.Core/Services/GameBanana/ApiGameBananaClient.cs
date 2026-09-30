using System.Diagnostics;
using System.Net;
using System.Text.Json;
using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;
using GIMI_ModManager.Core.Services.GameBanana.Models;
using Polly;
using Polly.RateLimiting;
using Polly.Registry;
using Serilog;

namespace GIMI_ModManager.Core.Services.GameBanana;

public sealed class ApiGameBananaClient(
    ILogger logger,
    HttpClient httpClient,
    ResiliencePipelineProvider<string> resiliencePipelineProvider)
    : IApiGameBananaClient
{
    private readonly ILogger _logger = logger.ForContext<ApiGameBananaClient>();
    private readonly HttpClient _httpClient = httpClient;
    private readonly ResiliencePipeline _resiliencePipeline = resiliencePipelineProvider.GetPipeline(HttpClientName);
    public const string HttpClientName = nameof(IApiGameBananaClient);

    private const string DownloadUrl = "https://gamebanana.com/dl/";
    private const string ApiUrl = "https://gamebanana.com/apiv11/Mod/";
    private const string HealthCheckUrl = "https://gamebanana.com/apiv11";

    /// <summary>板块内容流：<c>apiv11/Game/{gameId}/Subfeed</c>。</summary>
    private const string GameSubfeedApiUrl = "https://gamebanana.com/apiv11/Game/";

    /// <summary>站内搜索：<c>apiv11/Util/Search/Results</c>（跨类型，返回混合提交）。</summary>
    private const string SearchApiUrl = "https://gamebanana.com/apiv11/Util/Search/Results";

    /// <summary>列表索引端点：<c>apiv11/Mod/Index</c>（唯一支持服务端分类筛选的列表端点）。</summary>
    private const string ModIndexApiUrl = "https://gamebanana.com/apiv11/Mod/Index";

    /// <summary><c>Mod/Index</c> 的 <c>_nPerpage</c> 上限（实测 50 可用，100 报 400）。</summary>
    private const int ModIndexMaxPerPage = 50;

    /// <summary>板块主页：<c>apiv11/Game/{gameId}/ProfilePage</c>（根分类清单在这里）。</summary>
    private const string GameProfilePageApiUrl = "https://gamebanana.com/apiv11/Game/";

    public async Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(HealthCheckUrl, cancellationToken).ConfigureAwait(false);

        foreach (var (key, value) in response.Headers)
        {
            if (key.Contains("Deprecation", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("Deprecated", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("GameBanana API is deprecated: {Key}={Value}", key, value);
                Debugger.Break();
                break;
            }
        }

        return response.StatusCode == HttpStatusCode.OK;
    }

    public async Task<ApiModProfile?> GetModProfileAsync(GbModId modId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modId);

        var modPageApiUrl = GetModInfoUrl(modId);

        using var response = await SendRequest(modPageApiUrl, cancellationToken).ConfigureAwait(false);

        _logger.Debug("Got response from GameBanana: {response}", response.StatusCode);
        await using var contentStream =
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var apiResponse =
            await JsonSerializer.DeserializeAsync<ApiModProfile>(contentStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);


        if (apiResponse == null)
        {
            _logger.Error("Failed to deserialize GameBanana response: {content}", contentStream);
            throw new HttpRequestException(
                $"Failed to deserialize GameBanana response. Reason: {response?.ReasonPhrase}");
        }

        return apiResponse;
    }

    public async Task<ApiModFilesInfo?> GetModFilesInfoAsync(GbModId modId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modId);

        var requestUrl = GetModFilesInfoUrl(modId);

        using var response = await SendRequest(requestUrl, cancellationToken).ConfigureAwait(false);

        _logger.Debug("Got response from GameBanana: {response}", response.StatusCode);
        await using var contentStream =
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var apiResponse =
            await JsonSerializer.DeserializeAsync<ApiModFilesInfo>(contentStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);


        if (apiResponse == null)
        {
            _logger.Error("Failed to deserialize GameBanana response: {content}", contentStream);
            throw new HttpRequestException(
                $"Failed to deserialize GameBanana response. Reason: {response?.ReasonPhrase}");
        }

        return apiResponse;
    }

    public async Task<ApiModFileInfo?> GetModFileInfoAsync(GbModId modId, GbModFileId modFileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modFileId);

        var modFilesInfo = await GetModFilesInfoAsync(modId, cancellationToken).ConfigureAwait(false);

        return modFilesInfo?.Files.FirstOrDefault(x => x.FileId.ToString() == modFileId);
    }

    public async Task<bool> ModFileExists(GbModFileId modFileId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modFileId);

        var requestUrl = GetAltUrlForModInfo(modFileId);

        using var response = await SendRequest(requestUrl, cancellationToken).ConfigureAwait(false);

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return !content.Contains("error:", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri GetAltUrlForModInfo(GbModFileId modFileId)
    {
        return new Uri(
            $"https://api.gamebanana.com/Core/Item/Data?itemid={modFileId}&itemtype=File&fields=file");
    }

    public Task<ModStorePage?> GetGameSubfeedAsync(GbGameId gameId, GbSubfeedSort sort, int page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        // _csvModelInclusions=Mod 是**服务端**过滤，实测有效（6090 条提交 → 3062 个 mod）——
        // 与搜索接口不同，这里可以放心只要 Mod。
        var requestUrl = new Uri(GameSubfeedApiUrl + gameId + "/Subfeed" +
                                $"?_nPage={page}" +
                                $"&_csvModelInclusions={ApiSubfeedRecord.ModModelName}" +
                                $"&_sSort={sort.ToApiValue()}");

        return GetModStorePageAsync(requestUrl, cancellationToken);
    }

    public Task<ModStorePage?> SearchGameModsAsync(GbGameId gameId, string searchQuery, int page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchQuery);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        // 搜索接口的混合类型是**服务端行为**：这里没有 _csvModelInclusions 可用
        // （实测该参数在 Search 上无效），只能取回来再在客户端过滤。
        var requestUrl = new Uri(SearchApiUrl +
                                $"?_sSearchString={Uri.EscapeDataString(searchQuery)}" +
                                $"&_idGameRow={gameId}" +
                                $"&_nPage={page}");

        return GetModStorePageAsync(requestUrl, cancellationToken);
    }

    public Task<ModStorePage?> GetGameModsByCategoryAsync(GbGameId gameId, int categoryId, int page,
        int? perPage = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        ArgumentOutOfRangeException.ThrowIfLessThan(categoryId, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        if (perPage is { } size && (size < 1 || size > ModIndexMaxPerPage))
            throw new ArgumentOutOfRangeException(nameof(perPage), size,
                $"Mod/Index 的 _nPerpage 只接受 1..{ModIndexMaxPerPage}（实测 100 报 400）。");

        // 过滤器参数名带方括号（_aFilters[Generic_Game]），.NET 的 Uri 能原样带上，不需要转义。
        // 这两个 filter 是**服务端**生效的（实测：根分类 29524 → 2817 条、子分类 46598 → 2 条），
        // 所以分类视图不用再靠客户端过滤翻页找内容。
        var requestUrl = new Uri(ModIndexApiUrl +
                                $"?_nPage={page}" +
                                (perPage is { } p ? $"&_nPerpage={p}" : string.Empty) +
                                $"&_aFilters[Generic_Game]={gameId}" +
                                $"&_aFilters[Generic_Category]={categoryId}");

        return GetModStorePageAsync(requestUrl, cancellationToken);
    }

    public async Task<IReadOnlyList<ModStoreRootCategory>?> GetGameRootCategoriesAsync(GbGameId gameId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameId);

        var requestUrl = new Uri(GameProfilePageApiUrl + gameId + "/ProfilePage");

        try
        {
            using var response = await SendRequest(requestUrl, cancellationToken).ConfigureAwait(false);

            await using var contentStream =
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var apiResponse = await JsonSerializer
                .DeserializeAsync<ApiGameProfilePageResponse>(contentStream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (apiResponse?.ModRootCategories is null)
                return [];

            return apiResponse.ModRootCategories
                .Select(ModStoreRootCategory.FromApi)
                .Where(category => category is not null)
                .Select(category => category!)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.Warning(e, "获取 GameBanana 板块根分类失败，商店侧栏将只显示「全部」 | Url: {Url}", requestUrl);
            return null;
        }
    }

    public Task<int?> GetGameModCountAsync(GbGameId gameId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameId);

        // _nPerpage=1：只为了拿 metadata 里的总数，别把一整页 15 条记录也拖回来。
        var requestUrl = new Uri(ModIndexApiUrl +
                                 $"?_nPage=1&_nPerpage=1&_aFilters[Generic_Game]={gameId}");

        return GetCountAsync(requestUrl, sectionModelName: null, cancellationToken);
    }

    public Task<int?> GetSearchModCountAsync(GbGameId gameId, string searchQuery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameId);
        ArgumentException.ThrowIfNullOrWhiteSpace(searchQuery);

        var requestUrl = new Uri(SearchApiUrl +
                                 $"?_sSearchString={Uri.EscapeDataString(searchQuery)}" +
                                 $"&_idGameRow={gameId}&_nPage=1");

        return GetCountAsync(requestUrl, ApiSubfeedRecord.ModModelName, cancellationToken);
    }

    /// <summary>
    /// 只读一个数字的取数路径（总数 / 分类型命中数），不解析记录体。
    ///
    /// <paramref name="sectionModelName"/> 为 null = 取 <c>_nRecordCount</c>（列表端点：这是**筛完的总数**）；
    /// 非 null = 取 <c>_aSectionMatchCounts</c> 里该模型的命中数（搜索端点：<c>_nRecordCount</c> 是
    /// **所有类型**的总数，拿它当 mod 数会虚高，实测搜 Jinhsi 是 203 vs Mod 130）。
    ///
    /// 失败一律返回 null（「不知道」）而不是 0 —— 侧栏里 0 和「没取到」显示得不一样。
    /// </summary>
    private async Task<int?> GetCountAsync(Uri requestUrl, string? sectionModelName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendRequest(requestUrl, cancellationToken).ConfigureAwait(false);

            await using var contentStream =
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var apiResponse = await JsonSerializer
                .DeserializeAsync<ApiSubfeedResponse>(contentStream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (apiResponse?.Metadata is not { } metadata)
                return null;

            if (sectionModelName is null)
                return metadata.RecordCount >= 0 ? metadata.RecordCount : null;

            if (metadata.SectionMatchCounts is null)
                return null;

            // 命中数为 0 时服务端不会给这一项，所以「有清单但没有 Mod 项」= 0 条，不是未知。
            var section = metadata.SectionMatchCounts
                .FirstOrDefault(s => string.Equals(s.ModelName, sectionModelName, StringComparison.OrdinalIgnoreCase));

            return section is { MatchCount: >= 0 } ? section.MatchCount : 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.Warning(e, "获取 GameBanana 计数失败 | Url: {Url}", requestUrl);
            return null;
        }
    }

    ///
    /// 与 <see cref="GetModProfileAsync"/> 那族**刻意不同**：那些方法失败就抛（调用方是后台服务，
    /// 需要知道失败了），而商店是个用户正在看的页面 —— 一次翻页失败应该显示空态/重试，
    /// 不该把异常抛进 UI 线程。所以这里失败记 Warning 并返回 null（=「没取到」，
    /// 与「取到了但是空的」<see cref="ModStorePage.Empty"/> 区分开）。
    /// </summary>
    private async Task<ModStorePage?> GetModStorePageAsync(Uri requestUrl, CancellationToken cancellationToken)
    {
        try
        {
            var apiResponse = await GetStoreJsonAsync<ApiSubfeedResponse>(requestUrl, cancellationToken)
                .ConfigureAwait(false);

            return ModStorePage.FromApi(apiResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户切页/关页导致的取消不该被当成失败吞掉。
            throw;
        }
        catch (Exception e)
        {
            _logger.Warning(e, "获取 GameBanana 列表失败，商店页将按空态处理 | Url: {Url}", requestUrl);
            return null;
        }
    }

    /// <summary>
    /// 取一份 JSON 并反序列化。<c>SendRequest</c> 走的仍是同一条 Polly 通道（重试 + 限流）——
    /// 商店这条链路只是把「失败降级成 null」的决定权留在各自的公开方法里。
    /// </summary>
    /// <returns>反序列化结果；响应体是 <c>null</c> 字面量时同样返回 null。</returns>
    private async Task<T?> GetStoreJsonAsync<T>(Uri requestUrl, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await SendRequest(requestUrl, cancellationToken).ConfigureAwait(false);

        await using var contentStream =
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        return await JsonSerializer.DeserializeAsync<T>(contentStream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 取一条 mod 的详情：两个端点并发，合成一个 <see cref="ModStoreDetail"/>。
    ///
    /// 两个请求都在同一个 try 里：**任一端失败就当详情整体失败**返回 null ——
    /// 只有文件清单或只有简介的半份详情，比一句「加载失败，请重试」更让人困惑。
    /// </summary>
    public async Task<ModStoreDetail?> GetModStoreDetailAsync(GbModId modId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modId);

        try
        {
            var profileTask = GetStoreJsonAsync<ApiModProfile>(GetModInfoUrl(modId), cancellationToken);
            var filesTask = GetStoreJsonAsync<ApiModFilesInfo>(GetModFilesInfoUrl(modId), cancellationToken);

            await Task.WhenAll(profileTask, filesTask).ConfigureAwait(false);

            return ModStoreDetail.TryCreate(await profileTask.ConfigureAwait(false),
                await filesTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户关掉抽屉/切到别的 mod 导致的取消不该被当成失败吞掉。
            throw;
        }
        catch (Exception e)
        {
            _logger.Warning(e, "获取 GameBanana mod 详情失败，商店详情将按失败态处理 | ModId: {ModId}",
                modId.ModId);
            return null;
        }
    }

    public async Task DownloadModAsync(GbModFileId modFileId, FileStream destinationFile, IProgress<int>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modFileId, nameof(modFileId));
        ArgumentNullException.ThrowIfNull(destinationFile);
        var downloadUrl = DownloadUrl + modFileId;


        using var response = await _httpClient
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Mod not found.");

        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"Failed to download mod from GameBanana. Reason: {response?.ReasonPhrase}");

        var contentLength = response.Content.Headers.ContentLength;

        await using var downloadStream =
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        if (contentLength is not null && progress is not null)
            _ = Task.Run(() => DownloadMonitor(contentLength.Value, destinationFile.Name, progress, cancellationToken),
                cancellationToken);

        await downloadStream.CopyToAsync(destinationFile, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DownloadMonitor(long totalSizeBytes, string downloadFilePath, IProgress<int> progress,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var file = new FileInfo(downloadFilePath);
            while (!cancellationToken.IsCancellationRequested && file.Length < totalSizeBytes)
            {
                file.Refresh();
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                var fileSize = file.Length;
                progress.Report((int)Math.Round((decimal)fileSize / (decimal)totalSizeBytes * 100));
            }
        }
        catch (Exception e)
        {
#if DEBUG
            throw;
#endif
        }
    }

    private Uri GetModFilesInfoUrl(GbModId gbModId)
    {
        return new Uri(ApiUrl + gbModId + "/DownloadPage");
    }

    private Uri GetModInfoUrl(GbModId gbModId)
    {
        return new Uri(ApiUrl + gbModId + "/ProfilePage");
    }

    private async Task<HttpResponseMessage> SendRequest(Uri downloadsApiUrl, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        retry:
        try
        {
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);

            if (IgnorePollyLimiterScope.IsIgnored)
            {
                response = await _httpClient.GetAsync(downloadsApiUrl, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Use anonymous state object to avoid closure allocation
                var state = new { url = downloadsApiUrl, httpClient = _httpClient };

                response = await _resiliencePipeline.ExecuteAsync(
                        async (context, token) => await context.httpClient.GetAsync(context.url, token)
                            .ConfigureAwait(false),
                        state, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (RateLimiterRejectedException e)
        {
            _logger.Debug("Rate limit exceeded, retrying after {retryAfter}", e.RetryAfter);
            var delay = e.RetryAfter ?? TimeSpan.FromSeconds(2);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            goto retry;
        }


        if (!response.IsSuccessStatusCode)
        {
            _logger.Error("Failed to get mod info from GameBanana: {response} | Url: {Url}", response,
                downloadsApiUrl);
            throw new HttpRequestException(
                $"Failed to get mod info from GameBanana. Reason: {response?.ReasonPhrase ?? "Unknown"} | Url: {downloadsApiUrl}");
        }

        _logger.Debug("Response received {0} | {1}", DateTime.Now, downloadsApiUrl);

        return response;
    }
}