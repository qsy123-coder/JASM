using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.WinUI.Contracts.ViewModels;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.Services;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels;

public partial class ModMarketViewModel : ObservableRecipient, INavigationAware
{
    private readonly ILogger _logger;
    private readonly ModMarketService _modMarketService;
    private CancellationTokenSource? _searchCts;

    // ─── Sidebar: Characters from Supabase ─────────────────────

    public ObservableCollection<ModMarketCategory> Categories { get; } = [];

    [ObservableProperty]
    private ModMarketCategory? _selectedCategory;

    // ─── Mod Card Grid ─────────────────────────────────────────

    public ObservableCollection<ModMarketMod> Mods { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isEmpty;

    // ─── Filters / Sort ────────────────────────────────────────

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedCategoryFilter = "仅Mods";

    [ObservableProperty]
    private string _selectedContentFilter = "显示 NSFW";

    [ObservableProperty]
    private string _selectedSortOption = "最新";

    public IReadOnlyList<string> CategoryFilterOptions { get; } = ["仅Mods", "全部分类"];
    public IReadOnlyList<string> ContentFilterOptions { get; } = ["显示 NSFW", "模糊 NSFW", "隐藏 NSFW"];
    public IReadOnlyList<string> SortOptions { get; } = ["最新", "最近更新", "默认"];

    // ─── Pagination ────────────────────────────────────────────

    private int _currentPage = 1;
    private const int PageSize = 24;

    [ObservableProperty]
    private bool _hasMorePages = true;

    public ModMarketViewModel(ILogger logger, ModMarketService modMarketService)
    {
        _logger = logger.ForContext<ModMarketViewModel>();
        _modMarketService = modMarketService;
    }

    // ─── Navigation ────────────────────────────────────────────

    public async void OnNavigatedTo(object parameter)
    {
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

    partial void OnSelectedCategoryFilterChanged(string value) => _ = ReloadModsAsync();
    partial void OnSelectedContentFilterChanged(string value) => _ = ReloadModsAsync();
    partial void OnSelectedSortOptionChanged(string value) => _ = ReloadModsAsync();

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
        if (mod?.DownloadUrl is not null)
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(mod.DownloadUrl));
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
        if (IsLoading) return;
        IsLoading = true;

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
                "最近更新" => "RecentlyUpdated",
                "默认" => "Newest",
                _ => "Newest"
            };
            var categoryFilter = SelectedCategoryFilter == "仅Mods";

            var (mods, total) = await _modMarketService.GetModsAsync(
                character: SelectedCategory?.Key,
                search: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                contentFilter: contentFilter,
                sortBy: sortBy,
                modsOnly: categoryFilter,
                page: _currentPage,
                pageSize: PageSize);

            if (!append) Mods.Clear();
            foreach (var m in mods) Mods.Add(m);

            // If we got a full page but total == returned count,
            // the Content-Range header might be missing. Assume more.
            HasMorePages = total == mods.Count
                ? mods.Count >= PageSize
                : mods.Count >= PageSize && Mods.Count < total;

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
        }
    }
}
