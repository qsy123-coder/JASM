using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services.Downloading;
using GIMI_ModManager.Core.Services.GameBanana.Models;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.Services.Notifications;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels;

/// <summary>
/// 「下载管理」面板的 ViewModel。**注册成单例**：下载是跨页面的事 —— 用户在商店里点下载、
/// 切去别处再回来，队列和面板里的行都得还在（这也是队列本身是单例的原因）。
///
/// 它只做两件事：把队列的快照同步成一列 <see cref="ModDownloadItemViewModel"/>，
/// 以及把「用户点了什么」翻译成队列调用。**自己不发请求、不碰文件、不管入库** ——
/// 那些是 <see cref="ModDownloadQueue"/> 与它下游（第 7 项的入库）的事。
/// </summary>
public partial class ModDownloadManagerViewModel : ObservableObject
{
    private readonly ModDownloadQueue _queue;
    private readonly NotificationManager _notificationManager;
    private readonly ILogger _logger;

    /// <summary>Key → 行。刷新时复用已有行：重建会让 ListView 闪，也会丢掉滚动位置。</summary>
    private readonly Dictionary<ModDownloadKey, ModDownloadItemViewModel> _rows = [];

    public ModDownloadManagerViewModel(ModDownloadQueue queue, NotificationManager notificationManager,
        ILogger logger)
    {
        _queue = queue;
        _notificationManager = notificationManager;
        _logger = logger.ForContext<ModDownloadManagerViewModel>();

        // 队列可能在任意线程发这个事件（进度回调就在下载线程上），处理里会切回 UI 线程。
        // 这里不主动同步一次队列：面板 VM 是入队的唯一入口，它被建出来的时候队列必然是空的。
        _queue.Changed += OnQueueChanged;
    }

    /// <summary>面板里的任务行，按入队顺序。</summary>
    public ObservableCollection<ModDownloadItemViewModel> Items { get; } = [];

    /// <summary>面板开着没有。<c>ModDownloadPanel</c> 监听它做滑入 / 滑出。</summary>
    [ObservableProperty]
    private bool _isOpen;

    public bool HasItems => Items.Count > 0;

    /// <summary>还有没跑完的（「全部取消」按钮的可用性）。</summary>
    public bool HasUnfinished => Items.Any(row => !row.Item.IsFinished);

    public bool HasFinished => Items.Any(row => row.Item.IsFinished);

    /// <summary>标题下面那行小字：几个任务、几个在跑。</summary>
    public string SummaryText
    {
        get
        {
            if (Items.Count == 0)
                return "还没有下载任务";

            var parts = new List<string> { $"{Items.Count} 个任务" };

            var active = Items.Count(row => row.Item.IsActive);
            if (active > 0)
                parts.Add($"{active} 个进行中");

            var waiting = Items.Count(row => row.Item.State == ModDownloadState.Queued);
            if (waiting > 0)
                parts.Add($"{waiting} 个等待");

            return string.Join(" · ", parts);
        }
    }

    /// <summary>空列表时那句提示（面板里只在没有任务时显示）。</summary>
    public string EmptyText => "还没有下载任务。在 Mod 详情里选好文件，点「下载选中文件」。";

    // ─── 命令 ──────────────────────────────────────────────────

    /// <summary>工具栏那个「下载管理」按钮：开合面板（再点一次收起来）。</summary>
    [RelayCommand]
    private void TogglePanel() => IsOpen = !IsOpen;

    [RelayCommand]
    private void ClosePanel() => IsOpen = false;

    /// <summary>
    /// 取消全部未完成的任务。**故意不加二次确认**：下载随时可以重来（重新入队会命中同一个
    /// 暂存路径继续传），而每个任务自己也有独立的取消按钮 —— 加个弹窗反而挡住常用操作。
    /// </summary>
    [RelayCommand]
    private void CancelAll() => _queue.CancelAll();

    /// <summary>把已完成 / 已失败的行从列表里移掉（连同它们的暂存文件）。</summary>
    [RelayCommand]
    private void ClearFinished() => _queue.ClearFinished();

    // ─── 入队 ──────────────────────────────────────────────────

    /// <summary>
    /// 从商店详情里下一个文件。地址 / 哈希 / 体积都在 Core 的 <paramref name="file"/> 上
    /// （上游给的 <c>_sDownloadUrl</c> 优先，缺失时按文件 id 拼），这里不重复判断。
    ///
    /// 顺手把「下完装哪儿」（<paramref name="detail"/> 上的角色）和「它是哪个 mod 页面」
    /// 一起塞进请求：队列下完就把请求交给部署阶段，那一步不该再回来问界面 ——
    /// 用户那时可能已经翻到别的 mod 上去了。
    ///
    /// 入队后**顺手把面板打开**：用户按了「下载」总得看见它去了哪。
    /// 同一个文件已经在队里时队列不会重复排（连点两下不会下两份）。
    /// </summary>
    public ModDownloadItem? EnqueueFromDetail(ModStoreDetailItem detail, ModStoreFile file)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(file);

        var request = ModDownloadRequest.FromStoreFile(new GbModId(detail.GbModId), file, detail.Title,
            detail.Character, detail.ModPageUrl);
        if (request is null)
        {
            // 正常路径下不会走到这儿（既没有地址、又拼不出地址）。但按钮点了必须有点反应。
            _logger.Warning("商店文件没有可用的下载地址 | ModId: {ModId} FileId: {FileId}",
                detail.GbModId, file.FileId);
            _notificationManager.ShowNotification("下载", "这个文件没有可用的下载地址，无法下载。",
                TimeSpan.FromSeconds(5));
            return null;
        }

        var item = _queue.Enqueue(request);
        IsOpen = true;
        return item;
    }

    // ─── 队列 → 界面 ───────────────────────────────────────────

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher is null)
            return;

        if (dispatcher.HasThreadAccess)
            SyncRows();
        else
            dispatcher.TryEnqueue(SyncRows);
    }

    /// <summary>
    /// 把队列快照同步到行列表：新来的补行、走了的删行、还在的整行重刷。
    ///
    /// 每次都整个过一遍，而不是只更新「变化的那条」—— <c>Changed</c> 不带载荷，
    /// 而且同一时刻队列里最多几个任务，这点遍历比维护一套增量逻辑便宜，也不会漏。
    /// </summary>
    private void SyncRows()
    {
        var items = _queue.Items;
        var alive = new HashSet<ModDownloadKey>(items.Count);

        foreach (var item in items)
        {
            alive.Add(item.Key);

            if (_rows.TryGetValue(item.Key, out var row))
            {
                row.Refresh();
                continue;
            }

            row = new ModDownloadItemViewModel(item, _queue);
            _rows[item.Key] = row;
            Items.Add(row);
        }

        // 倒着删：取消 / 清除会让任务从队列里消失，行也得跟着走。
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (alive.Contains(Items[i].Item.Key))
                continue;

            _rows.Remove(Items[i].Item.Key);
            Items.RemoveAt(i);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasUnfinished));
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(SummaryText));
    }
}