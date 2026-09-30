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

    /// <summary>false = 隐藏成人内容（默认）。设置页开关见 PRD Phase 1 第 9 项。</summary>
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