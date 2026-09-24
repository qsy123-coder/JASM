namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 送键闸门：两次刷新键（F10）之间至少隔 <see cref="MinimumInterval"/>，不够就等够了再发。
///
/// **为什么需要**（2026-09-24 实机）：游戏收到 F10 之后要重载一遍 Mod 池，而**重载窗口里再来的 F10
/// 会被它吃掉** —— 键确实送出去了（助手回 Sent、日志一切正常），游戏却停在上一件 Mod 上。
/// 浮窗上连点同一个角色的不同 Mod 时正好撞上：点第二下时第一次刷新还在跑，请求被
/// <see cref="RefreshCoalescer"/> 合并，而合并的补发是**立刻**发的（连"在跑"都不清），
/// 于是第二发 F10 砸进第一个重载还没吃完的那口 —— 用户看到的正是「勾上了，但游戏里没切过去」。
///
/// 分工：<see cref="RefreshCoalescer"/> 管「连着来的多次请求合成几次送键」（不碰时钟），
/// 本类管「两次送键之间要隔多久」（只碰时钟）。所以两件事各自可单测，互不牵扯。
///
/// 纯逻辑：时间由调用方传进来，本类不读时钟 —— 与 <see cref="RefreshCoalescer"/> 同一取舍。
/// 与之不同的是这里**不加锁**：调用方只有浮窗的协调器一个，且只从 UI 线程调（见其类注释）。
/// </summary>
public sealed class RefreshSendPacer
{
    /// <summary>
    /// 两次送键之间的最小间隔。**这是"手感 vs 可靠性"唯一的那颗旋钮** ——
    /// 调大更稳（绝不再白发），调小更快但可能重新撞进游戏的重载窗口；要改就改这一个数，
    /// 别在别处再塞一份常量。
    ///
    /// 取 2.5s 的依据：用户实机观察游戏那边**一秒内**就换过来了，2.5s 留了一倍多的余量；
    /// 而它只拖"连点里的后面几发"，单次勾选仍是立刻发（见 <see cref="GetWaitBeforeSend"/>）。
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2.5);

    /// <summary>上一次送键**结束**的时刻；还没送过时为 <c>null</c>。</summary>
    private DateTimeOffset? _lastSentAt;

    /// <summary>
    /// 现在要发的话还得等多久（<see cref="TimeSpan.Zero"/> = 可以立刻发）。
    ///
    /// **第一次送键永远是 <see cref="TimeSpan.Zero"/>**：单次勾选必须立刻生效，
    /// 不能为了治连点就把每一下都拖成慢动作 —— 那是把毛病搬到别处。
    /// </summary>
    public TimeSpan GetWaitBeforeSend(DateTimeOffset now)
    {
        if (_lastSentAt is not { } lastSentAt)
            return TimeSpan.Zero;

        var elapsed = now - lastSentAt;
        return elapsed >= MinimumInterval ? TimeSpan.Zero : MinimumInterval - elapsed;
    }

    /// <summary>
    /// 记一次送键**结束**（成功或失败都要记 —— 失败的那一次也可能已经让游戏开始重载了，
    /// 那口重载一样会把紧跟着来的 F10 吃掉）。
    ///
    /// 计时从"结束"起算而不是从"发起"起算：送键本身还要花掉切前台 + 管道往返 + 按住那 80ms，
    /// 拿发起时刻算会把这段算进间隔里，而间隔要量的是「上一发 F10 之后游戏喘够了气没有」。
    /// </summary>
    public void MarkSent(DateTimeOffset completedAt) => _lastSentAt = completedAt;
}