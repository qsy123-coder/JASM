using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.Services.GameBanana.Models;
using GIMI_ModManager.WinUI.Contracts.ViewModels;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.Services.ModStore;
using GIMI_ModManager.WinUI.Services.Notifications;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels;

/// <summary>
/// 「Mod 商店」页 —— 布局照搬 Mod 市场，数据来源完全独立（GameBanana 直连，不经过 Supabase）。
///
/// 取数模式由左侧栏那一项的身份 + 搜索框决定，优先级从高到低：
/// <list type="number">
///   <item>搜索框有词 → 搜索端点，词就是关键词（此时左侧栏只当「再按角色收窄」用）；</item>
///   <item>左侧栏选了**角色** → 搜索端点，角色名当关键词（不能浏览，理由见
///         <see cref="ModStoreService.SearchAsync"/>）；</item>
///   <item>左侧栏选了**根分类** → 按分类 id 走服务端筛选（见
///         <see cref="ModStoreService.BrowseCategoryAsync"/>）；</item>
///   <item>都没有（「全部」）→ 板块内容流，此时排序才有意义。</item>
/// </list>
/// 所以排序下拉只在第 4 种模式下可用，见 <see cref="CanSort"/>。
/// </summary>
public partial class ModStoreViewModel : ObservableRecipient, INavigationAware
{
    /// <summary>搜索防抖窗口。跟市场页同值 —— 同一套手感，没有理由不一致。</summary>
    private const int SearchDebounceMs = 400;

    /// <summary>
    /// 侧栏计数同时问几个。一个角色一个请求（没有批量端点，实测），五十多个角色全量补计数时
    /// 别把接口打满、也别让用户等一串串请求。
    /// </summary>
    private const int CountProbeConcurrency = 2;

    private const string ShowNsfwOption = "显示 NSFW";
    private const string HideNsfwOption = "隐藏 NSFW";

    private readonly ILogger _logger;
    private readonly ModStoreService _storeService;
    private readonly IGameService _gameService;
    private readonly NotificationManager _notificationManager;

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _sidebarCts;
    private CancellationTokenSource? _detailCts;

    /// <summary>抽屉当前这条 mod 对应的列表记录 —— 重试时要从它重新开一次。</summary>
    private ModStoreItem? _detailSource;

    /// <summary>正在重建侧栏列表 —— 期间的选中变化是内部行为，不是用户操作。</summary>
    private bool _rebuildingSidebar;

    public ModStoreViewModel(ILogger logger, ModStoreService storeService, IGameService gameService,
        NotificationManager notificationManager)
    {
        _logger = logger.ForContext<ModStoreViewModel>();
        _storeService = storeService;
        _gameService = gameService;
        _notificationManager = notificationManager;
    }

    // ─── 左侧栏 ────────────────────────────────────────────────

    /// <summary>
    /// 左侧栏：<c>全部</c> + 板块根分类 + 角色，一条平铺列表（照搬 Mod 市场的样子）。
    /// 角色来自本地游戏数据，分类与计数来自 GameBanana，见 <see cref="LoadSidebarAsync"/>。
    /// </summary>
    public ObservableCollection<ModStoreSidebarItem> Filters { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSort))]
    private ModStoreSidebarItem? _selectedFilter;

    // ─── 卡片列表 ──────────────────────────────────────────────

    public ObservableCollection<ModStoreItem> Mods { get; } = [];

    [ObservableProperty]
    private bool _isInitialLoading;

    [ObservableProperty]
    private bool _isLoadingMore;

    /// <summary>有没有取数在飞。既做重入闸门，也做「加载更多」的滚动判定。</summary>
    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>
    /// 空状态那一屏显示什么。成功但 0 条是「没有找到 Mod」，取数失败是错误原因 ——
    /// 失败可见性必须落在这一个属性上（市场页那次事故的教训：只写 StatusMessage 的话，
    /// 那个文案仅在初始加载期间可见，失败会表现成一片空白）。
    /// </summary>
    [ObservableProperty]
    private string _emptyStateMessage = "没有找到 Mod";

    [ObservableProperty]
    private bool _hasMorePages = true;

    // ─── 详情抽屉 ──────────────────────────────────────────────

    /// <summary>
    /// 抽屉里正在看的那条。**非 null = 抽屉打开** —— 页面监听这个属性的变化去 Show/Hide，
    /// 「关闭」就是把它置回 null（与市场页 <c>SelectedMod</c> 同一套做法）。
    /// </summary>
    [ObservableProperty]
    private ModStoreDetailItem? _detailItem;

    // ─── 搜索 / 排序 / 内容筛选 ─────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSort))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedSortOption = "默认";

    public IReadOnlyList<string> SortOptions { get; } = ["默认", "最新", "最近更新"];

    /// <summary>
    /// 成人内容开关。默认**隐藏**（PRD 的硬性决定），打开后卡片上会带 NSFW 角标。
    ///
    /// 这一版是页面内的下拉，会话级、不落盘；PRD Phase 1 第 9 项说的「设置页开关」还没做，
    /// 到时候两边共用一个设置项即可（服务层只有一个 <c>IncludeAdultContent</c>）。
    /// </summary>
    [ObservableProperty]
    private string _selectedContentFilter = HideNsfwOption;

    public IReadOnlyList<string> ContentFilterOptions { get; } = [HideNsfwOption, ShowNsfwOption];

    /// <summary>
    /// 排序只对板块内容流有效：搜索端点不吃排序参数，分类端点（Mod/Index）更是直接拒绝
    /// <c>_sSort</c>（任何值都报 400），所以筛了东西就禁用下拉。
    /// </summary>
    public bool CanSort =>
        string.IsNullOrWhiteSpace(SearchText) && SelectedFilter?.Kind == ModStoreSidebarKind.All;

    // ─── 分页游标 ──────────────────────────────────────────────

    /// <summary>
    /// 下一页要从哪一页取。**由服务层的结果驱动**（<see cref="ModStoreResult.NextPage"/>）——
    /// 服务层会因为整页被过滤掉而一次连取好几页，页面自己 +1 会重复取或漏取。
    /// </summary>
    private int _nextPage = 1;

    private bool _reloadPending;

    // ─── 导航 ──────────────────────────────────────────────────

    public async void OnNavigatedTo(object parameter)
    {
        await LoadSidebarAsync();
        await ReloadAsync();
    }

    public void OnNavigatedFrom()
    {
        // 离开页面就别再补计数/发搜索/补详情了 —— 那些请求的结果没人看，还占着接口。
        _sidebarCts?.Cancel();
        _searchCts?.Cancel();
        _detailCts?.Cancel();
    }

    // ─── 属性变化 ──────────────────────────────────────────────

    partial void OnSelectedFilterChanged(ModStoreSidebarItem? value)
    {
        if (_rebuildingSidebar) return;
        RequestReload();
    }

    partial void OnSelectedSortOptionChanged(string value) => RequestReload();

    partial void OnSelectedContentFilterChanged(string value)
    {
        // 服务端没有可用的 NSFW 过滤参数（实测），所以过滤在服务层做：改开关 + 重取。
        _storeService.IncludeAdultContent = value == ShowNsfwOption;
        RequestReload();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SearchDebounceMs, token);
                if (!token.IsCancellationRequested)
                    App.MainWindow.DispatcherQueue.TryEnqueue(() => _ = ReloadAsync());
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    /// <summary>
    /// 有请求在飞时不重入，改为记一个「待重载」的标记，等这一趟结束再跑 ——
    /// 中途丢掉用户最后一次操作会让界面停在一个不存在的筛选结果上。
    /// </summary>
    private void RequestReload()
    {
        if (IsLoading) { _reloadPending = true; return; }
        _ = ReloadAsync();
    }

    // ─── 命令 ──────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadMoreAsync() => await LoadAsync(true);

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    /// <summary>
    /// 打开详情抽屉：先把卡片上已有的字段画出来（<see cref="ModStoreDetailItem.FromCard"/>），
    /// 再补详情。这样点一下就有反应，而不是等两个请求回来才弹。
    /// </summary>
    [RelayCommand]
    private async Task OpenModDetail(ModStoreItem? mod)
    {
        if (mod is null)
            return;

        // 上一次的详情请求作废：用户已经在看别的 mod 了。
        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;
        _detailSource = mod;

        var item = ModStoreDetailItem.FromCard(mod);
        DetailItem = item;
        item.IsLoading = true;

        try
        {
            var detail = await _storeService.GetDetailAsync(mod.GbModId, cts.Token);

            // 期间用户关了抽屉或点了别的 mod → 这次结果没人要，丢掉（别写进已换掉的那份）。
            if (cts.Token.IsCancellationRequested || !ReferenceEquals(DetailItem, item))
                return;

            if (detail is null)
            {
                item.ErrorMessage = "详情加载失败，请重试";
                return;
            }

            item.ApplyDetail(detail);
        }
        catch (OperationCanceledException)
        {
            // 关掉抽屉 / 换了 mod，正常。
        }
        catch (Exception e)
        {
            _logger.Warning(e, "Mod 商店详情加载失败 | ModId: {ModId}", mod.GbModId);
            if (ReferenceEquals(DetailItem, item))
                item.ErrorMessage = "详情加载失败，请重试";
        }
        finally
        {
            if (ReferenceEquals(DetailItem, item))
                item.IsLoading = false;
        }
    }

    /// <summary>抽屉里「重试」按钮：拿当前这条 mod 重新补一次详情。</summary>
    [RelayCommand]
    private Task RetryModDetail() => _detailSource is { } source ? OpenModDetail(source) : Task.CompletedTask;

    /// <summary>关抽屉。置空而不是让面板自己 Collapsed —— 面板的状态（标签页/滚动）由 Show 重置。</summary>
    [RelayCommand]
    private void CloseDetailPanel() => DetailItem = null;

    /// <summary>
    /// 工具栏那个下载按钮。下载管理队列是 PRD Phase 1 第 6 项，还没做 ——
    /// 先跟市场页一样给一条占位提示，别让按钮点了没反应。
    /// </summary>
    [RelayCommand]
    private void OpenDownloadManager()
    {
        _notificationManager.ShowNotification("下载管理",
            "下载管理功能即将推出，敬请期待。",
            TimeSpan.FromSeconds(4));
    }

    // ─── 侧栏 ──────────────────────────────────────────────────

    /// <summary>
    /// 建左侧栏。
    ///
    /// 顺序上有讲究：**先把名字摆出来**（分类要一次接口，角色是本地数据），计数再慢慢补 ——
    /// 五十多个角色一个个问接口要好几十秒，等齐了再渲染等于页面一直空着。
    /// </summary>
    private async Task LoadSidebarAsync()
    {
        _sidebarCts?.Cancel();
        var cts = new CancellationTokenSource();
        _sidebarCts = cts;
        var token = cts.Token;

        var previous = SelectedFilter;

        // 重建列表期间 ListView 会先把选中置空、再选回新实例，那两次变化都不该触发取数
        // （否则一次导航要打三次接口）。用这个闸门挡掉，选完再把闸门打开。
        _rebuildingSidebar = true;
        try
        {
            Filters.Clear();
            Filters.Add(ModStoreSidebarItem.CreateAll());

            foreach (var category in await _storeService.GetRootCategoriesAsync(token))
                Filters.Add(ModStoreSidebarItem.FromRootCategory(category));

            foreach (var (gbName, displayName) in GetLocalCharacters())
                Filters.Add(ModStoreSidebarItem.FromCharacter(gbName, displayName));

            // 保住上次的选择：角色表来自本地数据、顺序稳定，按身份 + 名字还能找回原来那一项。
            SelectedFilter = FindSameFilter(previous) ?? Filters[0];
        }
        finally
        {
            _rebuildingSidebar = false;
        }

        if (!token.IsCancellationRequested)
            _ = FillCountsAsync(token);
    }

    private ModStoreSidebarItem? FindSameFilter(ModStoreSidebarItem? previous)
    {
        if (previous is null)
            return null;

        return Filters.FirstOrDefault(item =>
            item.Kind == previous.Kind &&
            string.Equals(item.GbName, previous.GbName, StringComparison.OrdinalIgnoreCase) &&
            item.RootCategoryId == previous.RootCategoryId);
    }

    /// <summary>
    /// 侧栏角色表：来自**本地游戏数据**（<c>characters.json</c>），不是 GameBanana 的分类接口。
    ///
    /// 两个理由：侧栏要和游戏里的角色一一对应（后面「一键部署」得落到对应角色的 mod 文件夹），
    /// 而 GameBanana 根本没有「列出板块子分类」的端点（实测 <c>Game/{id}/Categories</c> 404、
    /// <c>Mod/Category</c> 那族不按板块分页）。
    /// </summary>
    private List<(string GbName, string DisplayName)> GetLocalCharacters()
    {
        try
        {
            return _gameService.GetAllModdableObjectsAsCategory<ICharacter>()
                .Where(character => !IsPseudoCharacter(character))
                .Select(character => (GbName: character.InternalName.Id, DisplayName: character.DisplayName))
                .Where(character => !string.IsNullOrWhiteSpace(character.GbName))
                .OrderBy(character => character.DisplayName, StringComparer.CurrentCulture)
                .ToList();
        }
        catch (Exception e)
        {
            // 本地数据读不出来不该让页面整个空掉：退化成「只有分类」照样能看内容。
            _logger.Warning(e, "读取本地角色表失败，商店侧栏将只显示分类");
            return [];
        }
    }

    /// <summary>
    /// 鸣潮把「武器」这个聚合项写在本地化角色表的头几条里（内部名 <c>Weapons</c>），
    /// 它不是角色 —— <see cref="GameService.GlidersCharacterInternalName"/> 给的是原神那边的
    /// <c>Gliders</c>（风之翼），只认那两个属性会漏掉它（实机侧栏上就多出一行「武器」）。
    /// </summary>
    private const string WeaponsAggregateInternalName = "Weapons";

    /// <summary>
    /// 「其他角色」「武器」这类伪角色要排除：GameBanana 上没有对应的 mod 分类，留下的后果不是
    /// 空列表而是**误导性**结果 —— 侧栏是按角色名走搜索端点的，而搜索是**全文匹配**：
    /// 实测「武器」能搜出 127 条（全是正文里提到 weapons 的 mod），看着像一百多个武器 mod。
    /// </summary>
    private bool IsPseudoCharacter(ICharacter character) =>
        character.InternalNameEquals(_gameService.OtherCharacterInternalName) ||
        character.InternalNameEquals(_gameService.GlidersCharacterInternalName) ||
        character.InternalNameEquals(WeaponsAggregateInternalName);

    /// <summary>
    /// 给侧栏补计数，「全部」与根分类各一个请求、角色一个请求一个。
    ///
    /// 尽力而为：限并发、可取消、失败就空着（界面对 null 不显示数字），
    /// 不让一件锦上添花的事情把页面拖住或者刷满日志。
    /// </summary>
    private async Task FillCountsAsync(CancellationToken token)
    {
        try
        {
            var total = await _storeService.GetAllModCountAsync(token).ConfigureAwait(false);
            SetCount(item => item.Kind == ModStoreSidebarKind.All, total);

            var pending = Filters
                .Where(item => item.Kind == ModStoreSidebarKind.Character && !string.IsNullOrWhiteSpace(item.GbName))
                .ToArray();

            if (pending.Length == 0)
                return;

            using var throttle = new SemaphoreSlim(CountProbeConcurrency);

            await Task.WhenAll(pending.Select(async item =>
            {
                await throttle.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var count = await _storeService.GetCharacterModCountAsync(item.GbName!, token)
                        .ConfigureAwait(false);

                    if (!token.IsCancellationRequested)
                        SetCount(target => ReferenceEquals(target, item), count);
                }
                finally
                {
                    throttle.Release();
                }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 用户换了筛选/离开页面，正常。
        }
        catch (Exception e)
        {
            _logger.Warning(e, "商店侧栏计数失败（界面只是不显示数字）");
        }
    }

    /// <summary>计数是后台线程取回来的，改绑定源必须回 UI 线程。</summary>
    private void SetCount(Func<ModStoreSidebarItem, bool> predicate, int? count)
    {
        // null = 不知道（没取到 / 接口不给），界面不显示数字；0 是真实结果，要显示。
        if (count is null)
            return;

        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var item in Filters.Where(predicate))
                item.ItemCount = count;
        });
    }

    // ─── 取数 ──────────────────────────────────────────────────

    private async Task ReloadAsync()
    {
        _nextPage = 1;
        Mods.Clear();
        await LoadAsync(false);
    }

    private async Task LoadAsync(bool append)
    {
        if (IsLoading) return;

        IsLoading = true;
        IsInitialLoading = !append;
        IsLoadingMore = append;
        StatusMessage = "正在加载...";

        try
        {
            var result = await FetchAsync();

            // 服务层把异常收敛成 ErrorMessage 返回（它自己不抛），所以「失败」在这里而不在 catch 里。
            // 必须显式判一次，否则失败会被当成「成功但 0 条」，界面显示「没有找到 Mod」，真相被盖掉。
            if (result.ErrorMessage is { Length: > 0 } error)
            {
                ShowLoadFailure(error, append);
                return;
            }

            if (!append) Mods.Clear();
            foreach (var item in result.Items) Mods.Add(item);

            // 失败时**不动**游标：Failure 的 NextPage 是 1，照抄会让「加载更多」失败后重取第一页。
            _nextPage = result.NextPage;
            HasMorePages = result.HasMore;

            IsEmpty = Mods.Count == 0;
            EmptyStateMessage = IsEmpty ? BuildEmptyMessage() : string.Empty;
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Mod 商店取数失败");
            ShowLoadFailure($"加载失败：{ex.Message}", append);
        }
        finally
        {
            IsLoading = false;
            IsInitialLoading = false;
            IsLoadingMore = false;

            if (_reloadPending)
            {
                _reloadPending = false;
                _ = ReloadAsync();
            }
        }
    }

    private Task<ModStoreResult> FetchAsync()
    {
        var query = SearchText.Trim();
        var filter = SelectedFilter;

        if (query.Length > 0)
            return _storeService.SearchAsync(query, _nextPage, filter?.GbName);

        // 用属性模式而不是 switch：顺手把「filter 不为 null」证给编译器看，
        // 免得每个分支都得写一次 ! 或者吃一条 CS8602。
        if (filter is { Kind: ModStoreSidebarKind.RootCategory, RootCategoryId: { } categoryId })
            return _storeService.BrowseCategoryAsync(categoryId, _nextPage);

        if (filter is { Kind: ModStoreSidebarKind.Character, GbName: { } character })
            return _storeService.SearchAsync(character, _nextPage, character);

        return _storeService.BrowseAsync(ParseSort(SelectedSortOption), _nextPage);
    }

    private static GbSubfeedSort ParseSort(string option) => option switch
    {
        "最新" => GbSubfeedSort.New,
        "最近更新" => GbSubfeedSort.Updated,
        _ => GbSubfeedSort.Default
    };

    /// <summary>
    /// 空结果的原因不止一种，说清楚点击的筛选条件，别让用户以为整个板块都没 mod。
    /// 另外提醒一句 NSFW 是被藏起来的 —— 这是「明明有却搜不到」的常见来源。
    /// </summary>
    private string BuildEmptyMessage()
    {
        var hint = SelectedContentFilter == HideNsfwOption ? "（成人内容已隐藏，可切换上方筛选显示）" : string.Empty;

        if (!string.IsNullOrWhiteSpace(SearchText))
            return $"没有找到与「{SearchText.Trim()}」匹配的 Mod{hint}";

        var filter = SelectedFilter;

        if (filter is { Kind: ModStoreSidebarKind.Character })
            return $"没有找到「{filter.DisplayName}」的 Mod{hint}";

        if (filter is { Kind: ModStoreSidebarKind.RootCategory })
            return $"「{filter.DisplayName}」分类下没有找到 Mod{hint}";

        return $"没有找到 Mod{hint}";
    }

    /// <summary>
    /// 失败必须看得见。列表为空时原因写进空状态区（那一屏带重试按钮）；已经有卡片时
    /// （「加载更多」失败）空状态区是收起的，只能靠通知，但两条路都不能静默。
    /// </summary>
    private void ShowLoadFailure(string message, bool append)
    {
        _logger.Warning("Mod 商店加载失败：{Message}", message);

        StatusMessage = message;
        EmptyStateMessage = message;
        IsEmpty = Mods.Count == 0;

        // 这一页已经失败了，别让滚动事件反复重试同一页（那只会刷满日志）。
        // 重试按钮 / 换筛选条件都会走重载，HasMorePages 会按新结果重算，不会卡死。
        if (append) HasMorePages = false;

        if (Mods.Count > 0)
            _notificationManager.ShowNotification("Mod 商店", message, TimeSpan.FromSeconds(6));
    }
}