using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.WinUI.Contracts.ViewModels;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.Services;
using GIMI_ModManager.WinUI.Services.Notifications;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels;

public partial class ModMarketViewModel : ObservableRecipient, INavigationAware
{
    private readonly ILogger _logger;
    private readonly ModMarketService _modMarketService;
    private readonly NotificationManager _notificationManager;
    private CancellationTokenSource? _searchCts;

    // ─── Sidebar: Characters from Supabase ─────────────────────

    public ObservableCollection<ModMarketCategory> Categories { get; } = [];

    [ObservableProperty]
    private ModMarketCategory? _selectedCategory;

    // ─── Mod Card Grid ─────────────────────────────────────────

    public ObservableCollection<ModMarketMod> Mods { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>首次加载/重新加载(列表为空时),指示器显示在内容区顶部</summary>
    [ObservableProperty]
    private bool _isInitialLoading;

    /// <summary>滚动加载更多,指示器显示在卡片流末尾</summary>
    [ObservableProperty]
    private bool _isLoadingMore;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>
    /// 空状态那一屏显示什么。成功但 0 条是「没有找到 Mod」,加载失败是错误原因 ——
    /// 之前失败时只写了 StatusMessage,而那个文案只在 IsInitialLoading 期间可见,
    /// 于是整片区域全空、连重试按钮都没有。失败的可见性必须落在这一个属性上。
    /// </summary>
    [ObservableProperty]
    private string _emptyStateMessage = "没有找到 Mod";

    /// <summary>true = 当前这批数据来自 COS 兜底快照(Supabase 网关不可用)。</summary>
    [ObservableProperty]
    private bool _isDegraded;

    [ObservableProperty]
    private string _degradedMessage = string.Empty;

    // ─── Filters / Sort ────────────────────────────────────────

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategoryFilter = "全部分类";

    [ObservableProperty]
    private string _selectedContentFilter = "显示 NSFW";

    [ObservableProperty]
    private string _selectedSortOption = "默认";

    public IReadOnlyList<string> CategoryFilterOptions { get; } =
        ["全部分类", "仅Mods", "仅NSFW", "仅非NSFW", "含直链下载"];
    public IReadOnlyList<string> ContentFilterOptions { get; } = ["显示 NSFW", "模糊 NSFW", "隐藏 NSFW"];
    public IReadOnlyList<string> SortOptions { get; } = ["默认", "最新", "最近更新", "最多点赞", "最多浏览"];

    // ─── Pagination ────────────────────────────────────────────

    private int _currentPage = 1;
    private const int PageSize = 24;

    [ObservableProperty]
    private bool _hasMorePages = true;

    [ObservableProperty]
    private ModMarketMod? _selectedMod;

    [RelayCommand]
    private void OpenDownloadManager()
    {
        _notificationManager.ShowNotification("下载管理",
            "下载管理功能即将推出，敬请期待。",
            TimeSpan.FromSeconds(4));
    }

    public ModMarketViewModel(ILogger logger, ModMarketService modMarketService,
        NotificationManager notificationManager)
    {
        _logger = logger.ForContext<ModMarketViewModel>();
        _modMarketService = modMarketService;
        _notificationManager = notificationManager;
    }

    // ─── Navigation ────────────────────────────────────────────

    public async void OnNavigatedTo(object parameter)
    {
        IsInitialLoading = true;
        IsLoading = true;
        StatusMessage = "正在加载...";

        try
        {
            var categories = await _modMarketService.GetCharacterCategoriesAsync();
            Categories.Clear();
            foreach (var c in categories)
                Categories.Add(c);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load categories");
            StatusMessage = $"加载分类失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
            IsInitialLoading = false;
        }

        // Select "全部" after loading and IsLoading is false
        if (Categories.Count > 0)
            SelectedCategory = Categories[0];
    }

    public void OnNavigatedFrom() { }

    // ─── Property Changes ─────────────────────────────────────

    partial void OnSelectedCategoryChanged(ModMarketCategory? value) => _ = ReloadModsAsync();

    partial void OnSearchTextChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                if (!token.IsCancellationRequested)
                    App.MainWindow.DispatcherQueue.TryEnqueue(() => _ = ReloadModsAsync());
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    private bool _reloadPending;

    partial void OnSelectedCategoryFilterChanged(string value) => RequestReload();
    partial void OnSelectedContentFilterChanged(string value) => RequestReload();
    partial void OnSelectedSortOptionChanged(string value) => RequestReload();

    private void RequestReload()
    {
        if (IsLoading) { _reloadPending = true; return; }
        _ = ReloadModsAsync();
    }

    // ─── Commands ──────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        await LoadModsAsync(true);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadModsAsync();

    [RelayCommand]
    private void OpenModDetail(ModMarketMod? mod)
    {
        SelectedMod = mod;
    }

    [RelayCommand]
    private void CloseDetailPanel()
    {
        SelectedMod = null;
    }

    // ─── Data Loading ──────────────────────────────────────────

    private async Task ReloadModsAsync()
    {
        _currentPage = 1;
        Mods.Clear();
        // 横幅描述的是「当前这一屏数据」。数据都清了,横幅也必须跟着走,
        // 否则会出现「快照模式下加载失败 → 空白页 + 快照横幅」这种自相矛盾的画面。
        IsDegraded = false;
        DegradedMessage = string.Empty;
        await LoadModsAsync(false);
    }

    private async Task LoadModsAsync(bool append)
    {
        if (IsLoading)
        {
            return;
        }
        IsLoading = true;
        // 首次/重载 vs 加载更多,分别控制不同位置的指示器
        IsInitialLoading = !append;
        IsLoadingMore = append;

        // Only increment page when appending (load-more). Must be done after
        // the IsLoading guard to prevent races from LayoutUpdated / ViewChanged.
        if (append) _currentPage++;

        StatusMessage = "正在加载...";

        try
        {
            // Map filter options to service parameters
            var contentFilter = SelectedContentFilter switch
            {
                "隐藏 NSFW" => "SFW",
                "模糊 NSFW" => "Blur",
                _ => "All"
            };
            var sortBy = SelectedSortOption switch
            {
                "最近更新" => "Newest",
                "最新" => "Newest",
                "最多点赞" => "Most Liked",
                "最多浏览" => "Most Viewed",
                _ => "Default"
            };
            var categoryFilter = SelectedCategoryFilter == "仅Mods";

            // Category-level NSFW / direct-download filters
            // nsfwOnly takes precedence over contentFilter to avoid contradictory nsfw params
            bool? nsfwOnly = SelectedCategoryFilter switch
            {
                "仅NSFW" => true,
                "仅非NSFW" => false,
                _ => null
            };
            var directDownloadOnly = SelectedCategoryFilter == "含直链下载";

            // "最近更新": 只显示 3 天内更新的内容
            DateTime? updatedAfter = SelectedSortOption == "最近更新"
                ? DateTime.UtcNow.AddDays(-3) : null;

            var result = await _modMarketService.GetModsAsync(
                character: SelectedCategory?.Key,
                search: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                contentFilter: contentFilter,
                sortBy: sortBy,
                modsOnly: categoryFilter,
                nsfwOnly: nsfwOnly,
                directDownloadOnly: directDownloadOnly,
                updatedAfter: updatedAfter,
                page: _currentPage,
                pageSize: PageSize);

            var mods = result.Mods;
            var total = result.TotalCount;

            // 服务层把异常全收敛成 ErrorMessage 返回(它自己不抛),所以「失败」在这里而不在 catch 里。
            // 必须显式判一次:否则失败会被当成「成功但 0 条」,界面显示"没有找到 Mod",真相被盖掉。
            if (result.ErrorMessage is { Length: > 0 } error)
            {
                ShowLoadFailure(error, append);
                return;
            }

            if (!append) Mods.Clear();
            foreach (var m in mods)
            {
                m.IsNsfwBlurred = m.Nsfw && SelectedContentFilter == "模糊 NSFW";
                Mods.Add(m);
            }

            // When Content-Range is available (total != returned count),
            // trust it: there are more pages as long as we haven't
            // accumulated everything. When Content-Range is missing
            // (total == returned count), guess by page fullness.
            HasMorePages = total == mods.Count
                ? mods.Count >= PageSize
                : Mods.Count < total;

            IsEmpty = Mods.Count == 0;
            StatusMessage = IsEmpty ? "没有找到 Mod" : string.Empty;
            EmptyStateMessage = IsEmpty ? "没有找到 Mod" : string.Empty;

            // 快照降级是「能用」而不是「完美」—— 用横幅说明数据来源与快照时间,
            // 别让用户以为几百条 mod 在一夜之间全被删了。
            IsDegraded = result.IsFromSnapshot;
            DegradedMessage = BuildDegradedMessage(result);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load mods");
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
                _ = ReloadModsAsync();
            }
        }
    }

    /// <summary>
    /// 失败必须看得见。列表为空时把原因写进空状态区(那一屏带重试按钮);
    /// 已经有卡片时(「加载更多」失败)空状态区是收起的,只能靠通知,
    /// 但两条路都不能静默 —— 这正是这次事故里表现成「整片空白」的根因。
    /// </summary>
    private void ShowLoadFailure(string message, bool append)
    {
        _logger.Warning("Mod 市场加载失败:{Message}", message);

        StatusMessage = message;
        EmptyStateMessage = message;
        IsEmpty = Mods.Count == 0;

        // 这一页已经失败了,别让滚动事件反复重试同一页(那只会刷满日志)。
        // 重试按钮/切分类都会走重载,HasMorePages 会按新结果重算,不会卡死。
        if (append) HasMorePages = false;

        if (Mods.Count > 0)
            _notificationManager.ShowNotification("Mod 市场", message, TimeSpan.FromSeconds(6));
    }

    /// <summary>
    /// 降级横幅的正文。时刻取快照的 Last-Modified 并换算到本地时区 ——
    /// 用户关心的是"这堆数据有多旧",不是 UTC。
    /// </summary>
    private static string BuildDegradedMessage(ModMarketResult result)
    {
        if (!result.IsFromSnapshot) return string.Empty;

        var when = result.SnapshotGeneratedAt is { } generatedAt
            ? generatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "未知时间";

        return $"Supabase 暂不可用，当前显示 {when} 的数据快照";
    }
}
