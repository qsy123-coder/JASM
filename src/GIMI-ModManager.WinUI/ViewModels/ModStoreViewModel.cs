using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
/// 三种取数模式，优先级从高到低：
/// <list type="number">
///   <item>搜索框有词 → 搜索端点，词就是关键词；</item>
///   <item>左侧选了角色 → 搜索端点，**角色名**当关键词（不能浏览，理由见
///         <see cref="ModStoreService.SearchAsync"/>）；</item>
///   <item>都没有 → 板块内容流，此时排序才有意义。</item>
/// </list>
/// 所以排序下拉只在第 3 种模式下可用，见 <see cref="CanSort"/>。
/// </summary>
public partial class ModStoreViewModel : ObservableRecipient, INavigationAware
{
    private readonly ILogger _logger;
    private readonly ModStoreService _storeService;
    private readonly NotificationManager _notificationManager;
    private CancellationTokenSource? _searchCts;

    /// <summary>搜索防抖窗口。跟市场页同值 —— 同一套手感，没有理由不一致。</summary>
    private const int SearchDebounceMs = 400;

    // ─── 左侧栏 ────────────────────────────────────────────────

    /// <summary>
    /// 这一版**只有「全部」**：角色表要接本地游戏数据（内部名即 GameBanana 子分类名），
    /// 属于 PRD Phase 1 第 3 项的剩余部分。筛选链路（服务端参数 + 客户端比对）已就位，
    /// 补上列表即生效。
    /// </summary>
    public ObservableCollection<ModStoreCharacter> Characters { get; } = [ModStoreCharacter.All];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSort))]
    private ModStoreCharacter? _selectedCharacter = ModStoreCharacter.All;

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

    // ─── 搜索 / 排序 ───────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSort))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedSortOption = "默认";

    public IReadOnlyList<string> SortOptions { get; } = ["默认", "最新", "最近更新"];

    /// <summary>排序只对板块内容流有效（搜索端点不吃排序参数），所以筛了角色或搜了词就禁用下拉。</summary>
    public bool CanSort => SelectedCharacter?.GbName is null && string.IsNullOrWhiteSpace(SearchText);

    // ─── 分页游标 ──────────────────────────────────────────────

    /// <summary>
    /// 下一页要从哪一页取。**由服务层的结果驱动**（<see cref="ModStoreResult.NextPage"/>）——
    /// 服务层会因为整页被过滤掉而一次连取好几页，页面自己 +1 会重复取或漏取。
    /// </summary>
    private int _nextPage = 1;

    private bool _reloadPending;

    public ModStoreViewModel(ILogger logger, ModStoreService storeService, NotificationManager notificationManager)
    {
        _logger = logger.ForContext<ModStoreViewModel>();
        _storeService = storeService;
        _notificationManager = notificationManager;
    }

    // ─── 导航 ──────────────────────────────────────────────────

    public async void OnNavigatedTo(object parameter)
    {
        await ReloadAsync();
    }

    public void OnNavigatedFrom() { }

    // ─── 属性变化 ──────────────────────────────────────────────

    partial void OnSelectedCharacterChanged(ModStoreCharacter? value) => RequestReload();

    partial void OnSelectedSortOptionChanged(string value) => RequestReload();

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
        var character = SelectedCharacter?.GbName;

        if (query.Length > 0)
            return _storeService.SearchAsync(query, _nextPage, character);

        // 选了角色又没有搜索词：拿角色名当关键词打搜索端点（浏览翻不到那么深）。
        if (character is not null)
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
    /// 另外提醒一句 NSFW 是被默认藏起来的 —— 这是「明明有却搜不到」的常见来源。
    /// </summary>
    private string BuildEmptyMessage()
    {
        var who = SelectedCharacter?.GbName;
        if (who is not null)
            return $"没有找到「{who}」的 Mod（成人内容默认隐藏，可在设置里打开）";

        return string.IsNullOrWhiteSpace(SearchText)
            ? "没有找到 Mod（成人内容默认隐藏，可在设置里打开）"
            : $"没有找到与「{SearchText.Trim()}」匹配的 Mod（成人内容默认隐藏，可在设置里打开）";
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