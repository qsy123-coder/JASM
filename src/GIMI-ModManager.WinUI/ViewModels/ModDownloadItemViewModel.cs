using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.Core.Services.Downloading;
using GIMI_ModManager.WinUI.Models;

namespace GIMI_ModManager.WinUI.ViewModels;

/// <summary>
/// 下载面板里的一行 —— 包着 Core 的 <see cref="ModDownloadItem"/>。
///
/// 这一层**不订阅任何东西**：Core 那边是「可变状态 + 一个 <c>Changed</c> 事件」（不逐项通知），
/// 队列一变，<see cref="ModDownloadManagerViewModel"/> 就在 UI 线程上把每行整个重刷一遍
/// （<see cref="Refresh"/>）。逐行订阅会在「某个文件被取消后又重新入队」这类情况下
/// 留下指向旧对象的野订阅，整行重刷没有这个问题。
/// </summary>
public partial class ModDownloadItemViewModel(ModDownloadItem item, ModDownloadQueue queue) : ObservableObject
{
    /// <summary>
    /// 会变的那些属性。<see cref="Refresh"/> 挨个点名通知 ——
    /// 界面用的是 x:Bind，它只认「属性名匹配」的通知，空名字那种「全都变了」是无效的。
    /// </summary>
    private static readonly string[] RefreshedProperties =
    [
        nameof(DisplayName),
        nameof(SubtitleText),
        nameof(StatusText),
        nameof(ProgressText),
        nameof(ProgressValue),
        nameof(IsIndeterminate),
        nameof(ShowProgress),
        nameof(CanPause),
        nameof(CanResume),
        nameof(CanCancel),
        nameof(HasError),
        nameof(ErrorText)
    ];

    /// <summary>Core 的那一条。三个命令要把它原样递回队列，所以是公开的。</summary>
    public ModDownloadItem Item { get; } = item;

    /// <summary>主标题：mod 名（面板是跨 mod 的，光看文件名不知道是哪个 mod）；没有 mod 名就用文件名。</summary>
    public string DisplayName => Item.ModName ?? Item.FileName;

    /// <summary>副标题：文件名 + 版本。作者常靠文件名区分「有图版 / 无图版」，所以文件名不能省。</summary>
    public string SubtitleText =>
        string.IsNullOrWhiteSpace(Item.Version) ? Item.FileName : $"{Item.FileName} · v{Item.Version}";

    public string StatusText => Item.State switch
    {
        ModDownloadState.Queued => "排队中",
        ModDownloadState.Downloading => "正在下载",
        ModDownloadState.Verifying => "正在校验",
        ModDownloadState.Paused => "已暂停",
        ModDownloadState.Completed => "已完成",
        ModDownloadState.Failed => "失败",
        _ => string.Empty
    };

    /// <summary>在跑、但总长未知（响应也没给 Content-Length）时走「不确定」样式，否则进度条会一直停在 0%。</summary>
    public bool IsIndeterminate =>
        (Item.State is ModDownloadState.Downloading or ModDownloadState.Verifying) && Item.TotalBytes is null;

    /// <summary>完成的行不再显示进度条：它只会停在 100%，白占一行。</summary>
    public bool ShowProgress => Item.State != ModDownloadState.Completed;

    public double ProgressValue => Item.Percent ?? 0;

    /// <summary>进度条下面那行小字，按状态给不同说法 —— 让用户知道「停在哪 / 还要多少」。</summary>
    public string ProgressText
    {
        get
        {
            var done = ModStoreFileItem.FormatSize(Item.BytesReceived);
            var total = Item.TotalBytes is { } bytes ? ModStoreFileItem.FormatSize(bytes) : null;
            var sofar = total is null ? done : $"{done} / {total}";

            return Item.State switch
            {
                // 还没开始：只说总量（用户据此决定要不要等下去）。
                ModDownloadState.Queued => total ?? string.Empty,

                // 已经完成：前面那个「已下载」和总量是同一个数，只报一个。
                ModDownloadState.Completed => total ?? done,

                // 停下了（暂停/失败）：说清楚到手多少 / 一共多少。
                ModDownloadState.Paused or ModDownloadState.Failed => sofar,

                // 正在跑：加上速度，否则进度条看着像静止的。
                _ => SpeedText is { } speed ? $"{sofar} · {speed}" : sofar
            };
        }
    }

    /// <summary>速度文本；不足 1 B/s（退出时清零、刚起步）时为 null，那一格就不显示。</summary>
    private string? SpeedText =>
        Item.BytesPerSecond >= 1 ? $"{ModStoreFileItem.FormatSize((long)Item.BytesPerSecond)}/s" : null;

    /// <summary>还没跑完的都能暂停（排队中的也算 —— 免得「下一个」正好是用户不想下的那个）。</summary>
    public bool CanPause =>
        Item.State is ModDownloadState.Queued or ModDownloadState.Downloading or ModDownloadState.Verifying;

    /// <summary>暂停与失败的都能继续（失败的从已有 <c>.part</c> 接着传）。</summary>
    public bool CanResume => Item.State is ModDownloadState.Paused or ModDownloadState.Failed;

    public bool CanCancel => !Item.IsFinished;

    /// <summary>
    /// 下载本身成功但后续动作（入库 / 拉起安装向导）失败的原因也算在这里 ——
    /// 那种情况 <see cref="ModDownloadItem.State"/> 是「已完成」，但用户必须看到那句话，
    /// 否则文件下好了、什么都没发生，界面上一片祥和。
    /// </summary>
    public string ErrorText => Item.ErrorMessage ?? Item.FollowUpError ?? string.Empty;

    public bool HasError => ErrorText.Length > 0;

    [RelayCommand]
    private void Pause() => queue.Pause(Item);

    [RelayCommand]
    private void Resume() => queue.Resume(Item);

    [RelayCommand]
    private void Cancel() => queue.Cancel(Item);

    /// <summary>队列状态变了：把界面要的那几个属性整个重报一遍（x:Bind 只认点名通知）。</summary>
    public void Refresh()
    {
        foreach (var name in RefreshedProperties)
            OnPropertyChanged(name);
    }
}