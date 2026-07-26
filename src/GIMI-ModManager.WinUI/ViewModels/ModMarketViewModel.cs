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
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to load mods");
            StatusMessage = $"加载失败：{ex.Message}";
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
}
