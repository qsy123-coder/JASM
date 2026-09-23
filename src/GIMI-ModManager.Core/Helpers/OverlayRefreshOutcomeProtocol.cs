namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 浮窗「勾选即刷新」这一步的最终结局。
///
/// 词表跟着**送键**那条路走（<c>GameKeySender</c>），不跟着助手管道的回复走：助手回了什么、
/// 为什么拒发，都是随 <c>GameKeySendResult.Detail</c> 一起上来的**动态原因**，由调用方原文显示，
/// 在这里再做一套分类只会多出第二份会失配的翻译表。
/// </summary>
public enum OverlayRefreshOutcome
{
    /// <summary>F10 已经送进游戏，游戏里会重载。</summary>
    Refreshed,

    /// <summary>没找到游戏窗口：游戏没跑 / Mod 环境没配好 / 没从 d3dx.ini 解析出目标进程。</summary>
    TargetNotFound,

    /// <summary>
    /// 游戏以管理员身份运行（完整性级别高于 JASM），UIPI 会把本进程的 <c>SendInput</c> 静默丢掉，
    /// 而提权助手也没能把按键代发出去（没随包安装 / 版本过旧 / 拉不起来 / 用户在 UAC 上点了否）。
    /// 到底是哪一种，见随结局一起显示的那句话。
    /// </summary>
    NeedsElevation,

    /// <summary>
    /// 按键没能送达：<c>SendInput</c> 插入的事件数少于请求数（典型原因：反作弊拦截合成输入），
    /// 或提权助手在跑但拒发 / 没回话。
    /// </summary>
    SendInputFailed,

    /// <summary>其它失败（进程启动失败、管道异常等）。</summary>
    Failed
}

/// <summary>
/// <see cref="OverlayRefreshOutcome"/> 的文案（纯逻辑，可单测）。
///
/// 这里只做一件事：把结局翻译成**能指导下一步动作**的一句话。
/// 「为什么失败」那层动态信息由送键那条路（<c>GameKeySender</c> → <c>ElevatorService</c>）给 ——
/// 助手回执里的 token（没抢到前台 / 被反作弊拦下 / 版本过旧…）每种都对应不同的下一步动作，
/// 调用方拿到的是拼好的一句话，这里不重复解释（写两份就会失配）。
/// </summary>
public static class OverlayRefreshOutcomeProtocol
{
    /// <summary>
    /// 给用户看的一句话。措辞与 <see cref="ElevatorKeySendProtocol.DescribeFailure"/> 同一口径：
    /// 先说清"发生了什么"，再说"你能做什么"。浮窗状态行地方小，但**不能只说"刷新失败"** ——
    /// 用户据此无从下手，只能去翻日志。
    /// </summary>
    public static string Describe(OverlayRefreshOutcome outcome) => outcome switch
    {
        OverlayRefreshOutcome.Refreshed => "已刷新",

        OverlayRefreshOutcome.TargetNotFound =>
            "没找到游戏窗口：确认游戏已经启动；如果刚改过 Mod 环境，先在设置页重跑一次一键配置。",

        // 到这一步说明助手那条路已经试过并失败了（具体原因随详情句一起显示，浮窗状态行会优先显示它），
        // 所以这里给的是真正还能改变结果的那两件事，而不是"再点一下游戏画面"这种死路。
        OverlayRefreshOutcome.NeedsElevation =>
            "游戏以管理员身份运行，JASM 的按键进不去，提权助手也没能代发。请更新 JASM 后重试；"
            + "或直接以管理员身份运行 JASM。",

        OverlayRefreshOutcome.SendInputFailed =>
            "按键没能送进游戏（可能被反作弊拦下，或没能把游戏切到前台）。请再试一次；每次都失败的话见日志。",

        _ => "刷新失败，详情见日志。"
    };
}