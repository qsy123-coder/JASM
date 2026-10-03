using System.Collections.Concurrent;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.Models;
using GIMI_ModManager.WinUI.Models;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModStore;

/// <summary>
/// 商店的取数门面：把 GameBanana 列表接口 + 客户端过滤（NSFW、非 Mod 记录）收在一处，
/// 页面只跟它打交道（与市场侧的 <c>ModMarketService</c> 同一分工）。
///
/// 板块 Id **不写死**：从当前游戏的 <c>game.json</c>（<see cref="IGameService.GameBananaUrl"/>）解析，
/// 鸣潮那份里就是 <c>https://gamebanana.com/games/20357</c>。当前游戏没有板块地址时给一条
/// 明确失败而不是硬编码回落到鸣潮 —— 那样会把别的游戏的商店悄悄变成鸣潮板块。
/// </summary>
public sealed class ModStoreService(
    ILogger logger,
    IApiGameBananaClient client,
    IGameService gameService)
{
    private readonly ILogger _logger = logger.ForContext<ModStoreService>();

    /// <summary>
    /// 连取多少页都筛不出东西就放弃。
    ///
    /// 需要这个上限是因为**整页被过滤掉是常态**：服务端没有分类参数、也没有可用的 NSFW 过滤，
    /// 于是「隐藏 NSFW」时一页 15 条可能一条不剩，而搜索端点本来就是混合类型。
    /// 不设上限的话，极端情况下会一路翻到第 200 页。
    /// </summary>
    private const int MaxConsecutiveEmptyPages = 4;

    /// <summary>
    /// false = 隐藏成人内容（默认）。由商店页在取数**之前**从 <c>ModStoreSettings</c> 设置好
    /// （设置页的复选框与页内那个下拉写的是同一份，见 PRD Phase 1 第 9 项）。
    ///
    /// 服务自己**不读设置**：它不知道设置存在哪，也不该知道 —— 这样换默认值 / 换存储位置
    /// 都只动读取方，取数这一层不必跟着改。
    /// </summary>
    public bool IncludeAdultContent { get; set; }

    /// <summary>浏览：板块内容流。<paramref name="startPage"/> 从 1 开始。</summary>
    /// <param name="character">
    /// 角色名（GameBanana 子分类名），null = 不筛。
    /// ⚠️ 按角色筛**不要**用这个重载直接配浏览 —— 见 <see cref="SearchAsync"/>。
    /// </param>
    public Task<ModStoreResult> BrowseAsync(GbSubfeedSort sort, int startPage, string? character = null,
        CancellationToken cancellationToken = default)
    {
        return FetchAsync("浏览", character,
            (page, ct) => client.GetGameSubfeedAsync(ResolveGameId(), sort, page, ct), startPage, cancellationToken);
    }

    /// <summary>
    /// 搜索：站内搜索端点（返回混合类型提交，非 Mod 会被丢掉）。
    ///
    /// 按角色筛选走的是**这条路，不是浏览**：一个角色的 mod 散落在几千条板块记录里
    /// （实测鸣潮 1333 条里 Jinhsi 只占 121 条），靠浏览翻页翻到它们得翻几十页。
    /// 实测 <c>_sSearchString=Jinhsi</c> 首页 11/11 全是该角色，再用
    /// <paramref name="character"/> 在客户端做一次同名过滤兜底。
    /// </summary>
    public Task<ModStoreResult> SearchAsync(string query, int startPage, string? character = null,
        CancellationToken cancellationToken = default)
    {
        return FetchAsync($"搜索「{query}」", character,
            (page, ct) => client.SearchGameModsAsync(ResolveGameId(), query, page, ct), startPage, cancellationToken);
    }

    /// <summary>
    /// 分类视图一页取多少条。Subfeed 的页大小写死 15，但 <c>Mod/Index</c> 的 <c>_nPerpage</c> 有效，
    /// 这里取 30：服务端已经按分类筛过了，客户端只剩 NSFW 一条过滤规则，页大一点能少翻几次。
    /// </summary>
    private const int CategoryPageSize = 30;

    // ─── 计数（服务是单例，同一会话里只问接口一次）────────────────

    /// <summary>
    /// 角色名 → 搜索摘要（命中数 + 角色图标）。
    ///
    /// 「问过但没问到」也记成 null，免得每次进页面都重打一遍接口。图标与计数来自**同一个**搜索请求，
    /// 所以它们必须一起缓存 —— 分开存会出现「数字有了但图标每次重问」的荒唐情况。
    ///
    /// 用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 是因为侧栏的计数是**后台并发补**的
    /// （见 <c>ModStoreViewModel.FillCountsAsync</c>），不是 UI 线程串行调用。
    /// </summary>
    private readonly ConcurrentDictionary<string, GbSearchSummary?> _characterSummaries =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ModStoreRootCategory>? _rootCategories;
    private int? _allModCount;

    /// <summary>
    /// 按**分类**浏览：<c>Mod/Index?_aFilters[Generic_Game]&amp;[Generic_Category]</c>，
    /// 服务端精确筛选（根分类 / 角色子分类 id 都吃）。
    ///
    /// 与 <see cref="SearchAsync"/> 的分工：那个是「按名字模糊找」，这个是「按分类精确定位」。
    /// 分类 id 不像角色名那样会出现「本地表叫 YangyangXuanling、GameBanana 上叫 Yangyang: Xuanling」
    /// 的错配 —— 只要 id 对得上就一定筛得准。
    /// </summary>
    public Task<ModStoreResult> BrowseCategoryAsync(int categoryId, int startPage,
        CancellationToken cancellationToken = default)
    {
        return FetchAsync("分类浏览", null,
            (page, ct) => client.GetGameModsByCategoryAsync(ResolveGameId(), categoryId, page, CategoryPageSize, ct),
            startPage, cancellationToken);
    }

    /// <summary>
    /// 板块的根分类（带条目数），供侧栏「分类」一节使用。**会话内缓存**（成功才缓存）。
    /// </summary>
    /// <returns>取不到时返回空表 —— 侧栏只剩「全部」，不影响看内容，所以不报错。</returns>
    public async Task<IReadOnlyList<ModStoreRootCategory>> GetRootCategoriesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_rootCategories is { } cached)
            return cached;

        try
        {
            var categories =
                await client.GetGameRootCategoriesAsync(ResolveGameId(), cancellationToken).ConfigureAwait(false);

            // null = 这次没取到，**不缓存**：下次进页面再试一次。
            if (categories is null)
                return [];

            _rootCategories = categories;
            return categories;
        }
        catch (InvalidOperationException)
        {
            // 当前游戏没有板块地址（ResolveGameId 抛的就是这个）。不缓存：配置随时可能补上。
            return [];
        }
    }

    /// <summary>
    /// 板块的 mod 总数（侧栏「全部」那一项的计数）。**会话内缓存**（成功才缓存）。
    /// </summary>
    /// <returns>取不到返回 null —— 界面就不显示数字，不显示假的 0。</returns>
    public async Task<int?> GetAllModCountAsync(CancellationToken cancellationToken = default)
    {
        if (_allModCount is { } cached)
            return cached;

        try
        {
            var count = await client.GetGameModCountAsync(ResolveGameId(), cancellationToken).ConfigureAwait(false);
            if (count is null)
                return null;

            _allModCount = count;
            return count;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// 按角色名问一次搜索摘要（侧栏「角色」那一节的数字与图标）。**会话内缓存**（含失败的 null）。
    /// </summary>
    /// <remarks>
    /// 只能走搜索接口 —— 实测没有「按名字筛」的列表端点（<c>Mod/Index</c> 的 <c>_sName</c> 被忽略）。
    /// 所以这是**模糊命中数**，不是该角色分类下的精确条目数；名字对不上时拿到 0，
    /// 0 是真实结果（GameBanana 上确实没有），别当失败去重试。
    ///
    /// 图标也来自这次请求：搜索记录带的子分类图标就是这个角色的图（见 <c>GameBananaSubCategoryIcons</c>）。
    /// </remarks>
    public async Task<GbSearchSummary?> GetCharacterSummaryAsync(string gbName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gbName))
            return null;

        var key = gbName.Trim();

        if (_characterSummaries.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var summary = await client.GetSearchSummaryAsync(ResolveGameId(), key, cancellationToken)
                .ConfigureAwait(false);

            _characterSummaries[key] = summary;
            return summary;
        }
        catch (InvalidOperationException)
        {
            // 没有板块地址时不缓存：等配置补上还要能用。
            return null;
        }
    }

    /// <summary>
    /// 取一条 mod 的详情（详情抽屉用）。**不缓存**：详情里的统计/版本会变，
    /// 而重开一个抽屉本来就是用户主动动作，多打两个请求比给用户看过期的下载量划算。
    ///
    /// 与列表那条链路同一约定：失败返回 null，由界面显示「加载失败 + 重试」，
    /// 不把异常抛进 UI 线程（<see cref="IApiGameBananaClient.GetModStoreDetailAsync"/> 内部已收敛）。
    /// </summary>
    /// <param name="gbModId">GameBanana mod id（字符串，卡片上带的就是它）</param>
    public async Task<ModStoreDetail?> GetDetailAsync(string gbModId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gbModId))
            return null;

        return await client.GetModStoreDetailAsync(new GbModId(gbModId.Trim()), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 取一「屏」数据：从 <paramref name="startPage"/> 开始，直到筛出至少一条、
    /// 或服务端说到头了、或连续空页到上限。
    /// </summary>
    private async Task<ModStoreResult> FetchAsync(string label, string? character,
        Func<int, CancellationToken, Task<ModStorePage?>> fetch, int startPage, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, startPage);
        var emptyPages = 0;
        var hasMore = false;
        var total = 0;
        List<ModStoreItem> items = [];

        while (true)
        {
            ModStorePage? result;
            try
            {
                result = await fetch(page, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger.Warning(e, "商店取数失败（{Label}，第 {Page} 页）", label, page);
                return ModStoreResult.Failure($"{label}失败：{e.Message}");
            }

            // null = 客户端已经把失败收敛成「没取到」（见 ApiGameBananaClient.GetModStorePageAsync），
            // 与「取到了但是空的」区分开。
            if (result is null)
                return ModStoreResult.Failure($"{label}失败：无法从 GameBanana 取到数据，请稍后重试。");

            total = result.TotalCount;
            hasMore = result.HasMore;
            page++;

            foreach (var mod in result.Items)
            {
                if (mod.IsAdult && !IncludeAdultContent) continue;

                // 角色过滤放在这里（而不是页面里）有原因：它必须跟「连续空页」的计数同层，
                // 否则一页 15 条全被角色筛掉时，外层以为「这页有内容」，就不再往后翻，
                // 用户看到的是「明明有一百多个 mod，滚两下没了」。
                if (character is not null && !CharacterMatches(mod.Character, character)) continue;

                items.Add(ModStoreItem.FromMod(mod));
            }

            if (items.Count > 0 || !hasMore) break;

            if (++emptyPages >= MaxConsecutiveEmptyPages)
            {
                // 到上限就如实返回「这一屏是空的」，但保留 hasMore —— 用户继续下拉仍能往后走。
                _logger.Debug("商店连续 {Count} 页没筛出内容，先停在这里（{Label}）", emptyPages, label);
                break;
            }
        }

        return ModStoreResult.Success(items, total, hasMore, page);
    }

    /// <summary>
    /// 角色名比对。用 <see cref="StringComparison.OrdinalIgnoreCase"/>：GameBanana 的子分类名
    /// 与本地角色表来自两个项目，大小写不保证一致（实测两边都是 <c>Jinhsi</c> 这种形式，
    /// 但没有契约保证）。
    /// </summary>
    private static bool CharacterMatches(string? modCharacter, string wanted) =>
        !string.IsNullOrWhiteSpace(modCharacter) &&
        modCharacter.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 当前游戏的 GameBanana 板块 Id。解析不出来时抛 —— 由 <see cref="FetchAsync"/> 收敛成失败结果，
    /// 页面显示引导态（当前游戏没有板块 / 配置里没写 GameBananaUrl）。
    /// </summary>
    private GbGameId ResolveGameId()
    {
        var gameBananaUrl = gameService.GameBananaUrl;

        if (!GameBananaUrlHelper.TryGetGameIdFromUrl(gameBananaUrl, out var gameId))
            throw new InvalidOperationException(
                $"当前游戏没有可用的 GameBanana 板块地址（game.json 的 GameBananaUrl = {gameBananaUrl}）。");

        return gameId;
    }
}