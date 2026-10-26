namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 配置静默检查的节流策略。
///
/// 「进修复页就查一次上游有没有新配置」是体验的一部分，但每次打开浮窗都发请求既慢又吵。
/// 规则：同一进程会话最多一次；跨会话 12 小时内不重复。失败也记时间，
/// 避免断网时每次切页都重试一次。
/// </summary>
public static class WuwaConfigCheckPolicy
{
    /// <summary>跨会话的最小检查间隔。</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(12);

    /// <summary>
    /// <paramref name="checkedThisSession"/> 由调用方在本进程内维护；
    /// <paramref name="lastCheckUtc"/> 来自设置文件，可能为 null 或来自未来（系统时间被改）。
    /// </summary>
    public static bool ShouldCheck(
        DateTimeOffset? lastCheckUtc,
        DateTimeOffset now,
        bool checkedThisSession)
    {
        if (checkedThisSession)
            return false;

        if (lastCheckUtc is not { } last)
            return true;

        // 时间回拨/时区折腾导致「上次检查在未来」时，宁可查一次，也别从此永远不查。
        if (last > now)
            return true;

        return now - last >= MinimumInterval;
    }
}