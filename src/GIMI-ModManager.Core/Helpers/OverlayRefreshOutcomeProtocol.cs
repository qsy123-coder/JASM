namespace GIMI_ModManager.Core.Helpers;

/// <summary>浮窗「勾选即刷新」这一步的最终结局。</summary>
public enum OverlayRefreshOutcome
{
    /// <summary>已把游戏切到前台并送出 F10，游戏里会重载。</summary>
    Refreshed,

    /// <summary>提权助手没在运行，按键根本没人代发。</summary>
    ElevatorNotRunning,

    /// <summary>
    /// 助手版本过旧，不认识带目标窗口的刷新命令（<c>"2"</c>）。旧助手收到不认识的命令是**静默无视**，
    /// 所以必须在发出之前就据此拦下 —— 否则用户等到的只是「没有回话」，而去重启一个版本本来就旧的助手
    /// 解决不了任何问题。
    /// </summary>
    HelperTooOld,

    /// <summary>没找到游戏窗口：游戏没跑 / Mod 环境没配好 / 没从 d3dx.ini 解析出目标进程。</summary>
    TargetNotFound,

    /// <summary>助手收到了但拒发（原因见 token，如没能抢到前台）。</summary>
    Rejected,

    /// <summary>助手没回话：多半是不认识新命令的旧版助手，或连接中途断了。</summary>
    NoReply,

    /// <summary>其它失败（进程启动失败、管道异常等）。</summary>
    Failed
}

/// <summary>
/// <see cref="OverlayRefreshOutcome"/> 的分类与文案（纯逻辑，可单测）。
///
/// 失败原因 token 复用 <see cref="ElevatorRefreshProtocol"/> 里那一份，这里**不重新定义 token** ——
/// token 是主程序与助手之间的契约，写两份就会出现"助手发了 X、主程序只认得 Y"的静默失配。
/// 这里只做两件事：把助手的回复归到某个结局；把结局翻译成**能指导下一步动作**的一句话。
/// </summary>
public static class OverlayRefreshOutcomeProtocol
{
    /// <summary>把助手的回复归类。</summary>
    public static OverlayRefreshOutcome FromReply(ElevatorRefreshReply reply) => reply switch
    {
        ElevatorRefreshReply.Ok => OverlayRefreshOutcome.Refreshed,
        ElevatorRefreshReply.Failure => OverlayRefreshOutcome.Rejected,

        // None = 读到 EOF 或认不出的行。旧版助手收到不认识的命令会**直接断连**，走的就是这一支。
        _ => OverlayRefreshOutcome.NoReply
    };

    /// <summary>
    /// 给用户看的一句话。措辞与 <see cref="ElevatorKeySendProtocol.DescribeFailure"/> 同一口径：
    /// 先说清"发生了什么"，再说"你能做什么"。浮窗状态行地方小，但**不能只说"刷新失败"** ——
    /// 用户据此无从下手，只能去翻日志。
    /// </summary>
    public static string Describe(OverlayRefreshOutcome outcome, string? reasonToken = null) => outcome switch
    {
        OverlayRefreshOutcome.Refreshed => "已刷新",

        // 建议是"在设置页启动助手"而不是"随便点一下游戏"：UAC 弹窗会打断全屏，
        // 所以最好在**进游戏之前**就把助手起好。
        OverlayRefreshOutcome.ElevatorNotRunning =>
            "提权助手没在运行，游戏里不会重载。请在设置页启动助手后重试（建议进游戏前先启动，"
            + "否则全屏下会弹 UAC 打断画面）。",

        // 让用户「更新 JASM」而不是「重启助手」：助手是内嵌在主 exe 里的，版本跟着主程序走，
        // 重启同一个旧助手不会让它多认识一条命令。
        OverlayRefreshOutcome.HelperTooOld =>
            "提权助手版本过旧，不认识带目标的刷新命令，游戏里不会重载。更新 JASM 即可（助手随主程序一起更新）。",

        OverlayRefreshOutcome.TargetNotFound =>
            "没找到游戏窗口：确认游戏已经启动；如果刚改过 Mod 环境，先在设置页重跑一次一键配置。",

        OverlayRefreshOutcome.Rejected => DescribeRejection(reasonToken),

        OverlayRefreshOutcome.NoReply =>
            "提权助手没有回话（可能版本过旧或已退出）。请在设置页重启助手后重试。",

        _ => "刷新失败，详情见日志。"
    };

    /// <summary>
    /// 助手拒发时的文案。认不出的 token 原样带出（不吞掉）：助手将来加了原因，
    /// 用户至少还能看到原文，而不是一句没有信息量的"刷新失败"。
    /// </summary>
    private static string DescribeRejection(string? reasonToken) => reasonToken switch
    {
        // 与 ElevatorKeySendProtocol 里同一条原因的措辞保持一致：切前台本来就由 JASM 自己尝试，
        // 就算失败，让用户"再点一下游戏画面"也是死路 —— 再点一下，前台又回到游戏，
        // 而 JASM 那边依旧切不动。
        ElevatorRefreshProtocol.ReasonNotForeground =>
            "没能在刷新前把游戏切到前台，按键会打进别的窗口，所以没有发送。"
            + "请再试一次；如果每次都失败，先把游戏切成窗口化（或无边框）再试。",

        ElevatorRefreshProtocol.ReasonBadPayload =>
            "提权助手认为目标窗口句柄不合法（游戏可能刚好被关掉了）。",

        null or "" => "提权助手拒绝了这次刷新，没有说明原因。",
        _ => $"提权助手拒绝刷新：{reasonToken}"
    };
}