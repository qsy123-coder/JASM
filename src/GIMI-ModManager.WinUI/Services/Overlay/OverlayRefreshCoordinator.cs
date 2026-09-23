using CommunityToolkit.Mvvm.ComponentModel;
using GIMI_ModManager.Core.Helpers;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>
/// 浮窗「勾选即刷新」的执行体：把合并（<see cref="RefreshCoalescer"/>）与真发（<see cref="ElevatorService"/>）
/// 接起来，并把结局做成可绑定的状态给浮窗的状态行用。
///
/// 为什么要单独一层而不是让浮窗的 ViewModel 自己调 ElevatorService：合并这件事**有状态**
/// （在跑 / 待补发），状态一旦散在 ViewModel 里，重入与"点了没反应"这类问题就没人管得住。
/// 这里只做三件事：串行化、把结局翻译成给用户的一句话、把结局期间的"进行中"暴露出去。
///
/// 线程：**只从 UI 线程调用**（浮窗的点击处理）。状态属性直接绑到界面，所以内部刻意不写
/// <c>ConfigureAwait(false)</c>，让 await 的续体留在 UI 线程上。
/// </summary>
internal sealed partial class OverlayRefreshCoordinator : ObservableObject
{
    private readonly ElevatorService _elevatorService;
    private readonly ILogger _logger;
    private readonly RefreshCoalescer _coalescer = new();

    /// <summary>是否正在刷新（含已排上的那次补发）。浮窗据此显示"进行中"，避免连点看起来像卡死。</summary>
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>最近一次结局给用户看的一句话；还没刷过时为 <c>null</c>。</summary>
    [ObservableProperty] private string? _statusMessage;

    /// <summary>
    /// 最近一次结局本身。状态行要靠它区分"刷新成功"与"失败"（文案之外还要能换个颜色/图标），
    /// 光有字符串就没法判断了。
    /// </summary>
    [ObservableProperty] private OverlayRefreshOutcome? _lastOutcome;

    /// <summary>
    /// 最近一次结局算不算"没刷成"。浮窗很小，状态行只有一行字，光靠读完整句才知道成没成太费劲，
    /// 所以再给一个能直接换成图标/颜色的布尔值。
    /// </summary>
    public bool IsLastOutcomeFailure => LastOutcome is not null and not OverlayRefreshOutcome.Refreshed;

    /// <summary>状态行前面的对勾要不要露出来：只有真刷成了才露。</summary>
    public bool ShowSuccessIcon => LastOutcome == OverlayRefreshOutcome.Refreshed;

    /// <summary>状态行前面的警告图标要不要露出来。还没刷过时为 false（此时状态行本来就是空的，没有可警告的事）。</summary>
    public bool ShowFailureIcon => IsLastOutcomeFailure;

    /// <summary>
    /// 「刷新」按钮现在能不能点。点了也只是被合并进正在跑的那一次（见 <see cref="RequestRefreshAsync"/>），
    /// 与其让用户对着一个看着没反应的按钮连点，不如刷新期间直接把它灰掉。
    /// </summary>
    public bool CanRequestRefresh => !IsRefreshing;

    public OverlayRefreshCoordinator(ElevatorService elevatorService, ILogger logger)
    {
        _elevatorService = elevatorService;
        _logger = logger.ForContext<OverlayRefreshCoordinator>();
    }

    /// <summary>
    /// 上面三个属性都是从 <see cref="LastOutcome"/> 算出来的，源生成器不会替它们发通知 ——
    /// 不手动补一下，状态行换了结局但图标/颜色停在旧的那次。
    /// </summary>
    partial void OnLastOutcomeChanged(OverlayRefreshOutcome? value)
    {
        OnPropertyChanged(nameof(IsLastOutcomeFailure));
        OnPropertyChanged(nameof(ShowSuccessIcon));
        OnPropertyChanged(nameof(ShowFailureIcon));
    }

    /// <summary>同 <see cref="OnLastOutcomeChanged"/>：<see cref="CanRequestRefresh"/> 是从这里算出来的。</summary>
    partial void OnIsRefreshingChanged(bool value) => OnPropertyChanged(nameof(CanRequestRefresh));

    /// <summary>
    /// 请求一次刷新。可以进行多次调用：
    ///
    ///   - 当前没人在刷 → 立刻发（并在结束后把期间攒下的请求补发一次）；
    ///   - 当前正在刷 → 只记一笔账、**立刻返回**（不排队、不并发），结果照样会是最新的。
    ///
    /// 所以调用方（浮窗的点击处理）不必自己防抖，也不要等它跑完才允许下一次点击。
    /// </summary>
    public async Task RequestRefreshAsync()
    {
        if (!_coalescer.Request())
        {
            _logger.Debug("[浮窗] 刷新请求已合并进正在跑的那一次（跑完最多补发一次）");
            return;
        }

        try
        {
            bool rerun;

            do
            {
                IsRefreshing = true;

                // 不 ConfigureAwait(false)：本方法的调用方是浮窗的点击处理（UI 线程），
                // 状态属性要绑到界面，续体留在 UI 线程上最省事，也免得每次赋值都往 DispatcherQueue 里丢。
                var (outcome, reasonToken) = await _elevatorService.RefreshForOverlayAsync();

                LastOutcome = outcome;
                StatusMessage = OverlayRefreshOutcomeProtocol.Describe(outcome, reasonToken);

                rerun = _coalescer.Complete();
            }
            while (rerun);
        }
        catch (Exception e)
        {
            // RefreshForOverlayAsync 自己已把管道异常收成结局，这里是最后一道保险 ——
            // 但它**必须**先把合并器的账结清：漏一次 Complete，Request 就永远返回 false，
            // 浮窗从此再也刷不动，而且不报错，用户看到的只是"点了没反应"。
            // 用循环排空而不是只调一次：Complete 在"有补发"时会保持 in-flight（见其注释）。
            while (_coalescer.Complete())
            {
            }

            _logger.Error(e, "[浮窗] 刷新时发生未预期的异常");
            LastOutcome = OverlayRefreshOutcome.Failed;
            StatusMessage = OverlayRefreshOutcomeProtocol.Describe(OverlayRefreshOutcome.Failed);
        }
        finally
        {
            IsRefreshing = false;
        }
    }
}