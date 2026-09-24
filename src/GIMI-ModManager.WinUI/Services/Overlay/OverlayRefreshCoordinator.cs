using CommunityToolkit.Mvvm.ComponentModel;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.Input;
using Serilog;
using Windows.Win32.Foundation;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>
/// 浮窗「勾选即刷新」的执行体：把合并（<see cref="RefreshCoalescer"/>）与真发（<see cref="IGameKeySender"/>）
/// 接起来，并把结局做成可绑定的状态给浮窗的状态行用。
///
/// 为什么要单独一层而不是让浮窗的 ViewModel 自己调送键：合并这件事**有状态**
/// （在跑 / 待补发），状态一旦散在 ViewModel 里，重入与"点了没反应"这类问题就没人管得住。
/// 这里只做三件事：串行化、把结局翻译成给用户的一句话、把结局期间的"进行中"暴露出去。
///
/// 送键本身**不在这一层**：直发还是请提权助手代发（游戏以管理员身份运行时唯一进得去的路）、
/// 切前台、UIPI 前置判断，全部走 <see cref="IGameKeySender"/> —— 与点 Mod 的「按键徽章」是同一条
/// 已实机验证过的路。这里只发一个 F10（不带修饰键）。
///
/// 焦点：**送完要把前台交还浮窗**（条件见 <see cref="OverlayWindowHandle"/> 与
/// <see cref="ResolveForegroundHandbackTarget"/>）。送键前的同步段必然把游戏切到前台（F10 要打到前台
/// 窗口上，d3dx 才认），而游戏是提权运行的话，之后浮窗自己再也拿不回来 —— 前台锁只认「最近收到输入的
/// 那个进程」，而那一刻的所有者是刚注入按键的**提权助手**；本进程既抢不回前台，注入解锁那一手又会被
/// UIPI 静默丢掉（实测）。所以交还只能请助手做。不交还的话，用户每勾选一次都得重新唤出浮窗。
///
/// 节奏：两发 F10 之间由 <see cref="RefreshSendPacer"/> 把关（至少隔 <see cref="RefreshSendPacer.MinimumInterval"/>）。
/// 合并器决定"连着来的一串请求发几次"，拦不住"第二发比游戏的重载还快" —— 那种白送的 F10
/// 看着和成功一模一样（助手回 Sent），游戏却停在上一件上。
///
/// 线程：**只从 UI 线程调用**（浮窗的点击处理）。状态属性直接绑到界面，所以内部刻意不写
/// <c>ConfigureAwait(false)</c>，让 await 的续体留在 UI 线程上。切前台那一段要求调用方在**同步段**
/// 里就发起（见 <see cref="IGameKeySender"/> 与 <c>ForegroundWindowActivator</c> 的注释），
/// 所以调用方必须从 UI 线程直接 await 本方法，不能先丢进 <c>Task.Run</c>。
/// </summary>
internal sealed partial class OverlayRefreshCoordinator : ObservableObject
{
    /// <summary>
    /// 重载键：F10。与助手里写死的 <c>VK_F10</c>（<c>src/Elevator/Program.cs</c> 的
    /// <c>const int VK_F10 = 0x79</c>，注释「3DMigoto 的重载键」）同一个键 ——
    /// 两条路发的是同一个键，换了通道也不该悄悄换个键。
    /// </summary>
    private const ushort ReloadHotkeyVirtualKey = 0x79;

    private readonly IGameKeySender _gameKeySender;
    private readonly ILogger _logger;
    private readonly RefreshCoalescer _coalescer = new();

    /// <summary>
    /// 送键闸门：两次 F10 之间至少隔 <see cref="RefreshSendPacer.MinimumInterval"/>。
    /// 合并器只管"发几次"，管不了"后一发砸在游戏还没跑完的重载上"—— 那才是连点失效的模样。
    /// </summary>
    private readonly RefreshSendPacer _sendPacer = new();

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

    /// <summary>
    /// 浮窗自己的窗口句柄（<c>OverlayWindow</c> 拿到 hwnd 时写一次；<c>0</c> = 还不知道）。
    ///
    /// 只是把句柄转交给 <see cref="RequestRefreshAsync"/>：刷新完之后要把前台交还给谁，
    /// 只有浮窗自己知道。写成属性而不是给 <see cref="RequestRefreshAsync"/> 加参数，
    /// 是因为两个调用点都在 ViewModel 里，而 ViewModel 手上没有窗口 —— 句柄本来只有窗口知道。
    /// </summary>
    public nint OverlayWindowHandle { get; set; }

    public OverlayRefreshCoordinator(IGameKeySender gameKeySender, ILogger logger)
    {
        _gameKeySender = gameKeySender;
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
    /// 补发那一发要过送键闸门（见 <see cref="WaitForSendSlotAsync"/>）：连点时它是被**推迟**的，
    /// 不是不发 —— 所以「最后一次勾选一定生效」这条没有变，只是慢一点落地。
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

                await WaitForSendSlotAsync();

                // 每次送键前重新判一次：交还与否取决于光标此刻在不在浮窗上（见 ResolveForegroundHandbackTarget）。
                // 这一步必须在等闸门**之后**：等的那几秒里光标可能已经移开浮窗、或者浮窗已经被热键收起来了。
                var handbackTarget = ResolveForegroundHandbackTarget();

                // 不 ConfigureAwait(false)：本方法的调用方是浮窗的点击处理（UI 线程），
                // 状态属性要绑到界面，续体留在 UI 线程上最省事，也免得每次赋值都往 DispatcherQueue 里丢。
                var result = await _gameKeySender
                    .SendKeyAsync(ReloadHotkeyVirtualKey, Array.Empty<ushort>(), handbackTarget);

                // 送键结束（无论成败）都要记：失败那一发也可能已经让游戏开始重载，那口重载一样会吃掉下一发
                _sendPacer.MarkSent(DateTimeOffset.UtcNow);

                // 先算成局部变量再赋给属性：属性是 OverlayRefreshOutcome?（"还没刷过"要能表达），
                // 而 Describe 收的是非空 —— 直接拿属性去调会被可空性挡住。
                var outcome = ClassifyKeySend(result.Status);
                LastOutcome = outcome;

                // 动态原因优先：助手给了"为什么没发出去/为什么拒发"的原话（没抢到前台、被反作弊拦下…），
                // 那句比下面的通用文案有用得多，别被盖掉。
                StatusMessage = result.Detail ?? OverlayRefreshOutcomeProtocol.Describe(outcome);

                rerun = _coalescer.Complete();
            }
            while (rerun);
        }
        catch (Exception e)
        {
            // SendKeyAsync 自己已把送键的失败收成结果，这里是最后一道保险 ——
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

    /// <summary>
    /// 等送键闸门放行（见 <see cref="RefreshSendPacer"/>）。离上一发 F10 太近就等够了再发 ——
    /// 不等的话，连点产生的补发会砸进游戏还没跑完的那口重载里：键送出去了、日志一切正常，
    /// 游戏却不切（用户报的原话是「勾上了，但游戏里没切过去」）。
    ///
    /// 第一次送键永远不等待（闸门自己保证），所以单次勾选的手感不受影响。
    /// 等待期间 <see cref="IsRefreshing"/> 保持 true（由调用方设）：这会儿确实还没刷完，界面该显示进行中。
    /// 每次等待都记一行 Information：阈值合不合适只能从实机日志看，靠猜改不出来。
    /// </summary>
    private async Task WaitForSendSlotAsync()
    {
        var wait = _sendPacer.GetWaitBeforeSend(DateTimeOffset.UtcNow);
        if (wait <= TimeSpan.Zero)
            return;

        _logger.Information(
            "[浮窗] 距上一发 F10 不足 {Interval} 秒，等 {Wait:0.0} 秒再发（避开游戏的重载窗口）",
            RefreshSendPacer.MinimumInterval.TotalSeconds,
            wait.TotalSeconds);

        // 不 ConfigureAwait(false)：理由同 RequestRefreshAsync 里的那条 —— 续体要留在 UI 线程上
        await Task.Delay(wait);
    }

    /// <summary>
    /// 这次刷新要不要把前台交还给浮窗（要 → 返回浮窗句柄；不要 → <c>0</c>）。
    ///
    /// 两个条件都满足才交还：① 句柄已经知道（浮窗建过窗口）；② **光标此刻压在浮窗上**。
    ///
    /// ② 与 <c>OverlayWindow.EnsureForeground</c> 用同一条判据（<see cref="OverlayStackProbe"/> 的
    /// 那个只读查询），理由也一样：光标在浮窗上 = 用户正打算点它，前台就该归他；
    /// 光标不在（键盘导航时用户人在游戏里）就不该把前台从游戏手里拿走 —— 那等于把用户从游戏里踢出去。
    /// 顺带挡住另一个情形：用户勾完立刻用热键把浮窗收起来了，下一拍不该再让一个看不见的窗口抢前台。
    /// </summary>
    private nint ResolveForegroundHandbackTarget()
    {
        if (OverlayWindowHandle == 0)
            return 0;

        return OverlayStackProbe.IsCursorOverOwnProcessWindow((HWND)OverlayWindowHandle)
            ? OverlayWindowHandle
            : 0;
    }

    /// <summary>
    /// 送键结果 → 浮窗的结局。只做分类：动态原因（没抢到前台、被反作弊拦下、助手为什么没代发）
    /// 随 <see cref="GameKeySendResult.Detail"/> 原文上来，由状态行优先显示，这里不重复解释。
    ///
    /// 三种"没找到可以送键的窗口"合成一类：用户要做的事都是"确认游戏在跑 / 重跑一键配置"。
    /// </summary>
    private static OverlayRefreshOutcome ClassifyKeySend(GameKeySendStatus status) => status switch
    {
        GameKeySendStatus.Sent => OverlayRefreshOutcome.Refreshed,

        GameKeySendStatus.TargetNotConfigured => OverlayRefreshOutcome.TargetNotFound,
        GameKeySendStatus.GameProcessNotRunning => OverlayRefreshOutcome.TargetNotFound,
        GameKeySendStatus.GameWindowNotFound => OverlayRefreshOutcome.TargetNotFound,

        GameKeySendStatus.NeedsElevation => OverlayRefreshOutcome.NeedsElevation,

        // BlockedChord 理论上到不了（F10 不是危险组合键），但它同样是"键没发出去"，归这里不冤
        GameKeySendStatus.BlockedChord => OverlayRefreshOutcome.SendInputFailed,
        GameKeySendStatus.SendInputFailed => OverlayRefreshOutcome.SendInputFailed,

        _ => OverlayRefreshOutcome.Failed
    };
}