namespace GIMI_ModManager.WinUI.Services.Input;

/// <summary>
/// 「点按键徽章 = 在真实键盘上按一次」的结果。
/// 只有 <see cref="Sent"/> 不需要打扰用户（游戏里已经能看到反应），其余每一项对应一句提示文案。
/// </summary>
public enum GameKeySendStatus
{
    /// <summary>按键已合成发送。</summary>
    Sent,

    /// <summary>危险组合键（Alt+F4 / Alt+Tab / Alt+Esc / 单发 Win 键），出于安全考虑没有发送。</summary>
    BlockedChord,

    /// <summary>找不到 d3dx.ini，或里面没有可用的 target —— 游戏信息没配好 / 游戏目录换过。</summary>
    TargetNotConfigured,

    /// <summary>d3dx.ini 里的目标进程当前没有运行。</summary>
    GameProcessNotRunning,

    /// <summary>进程在跑，但没有可见的顶层窗口（还没进游戏、只有启动器在跑）。</summary>
    GameWindowNotFound,

    /// <summary>
    /// 目标游戏以管理员身份运行（完整性级别高于 JASM），UIPI 不允许把输入注入进去，
    /// **而提权助手也没能把按键代发出去** —— 没随包安装 / 版本过旧 / 拉不起来 / 用户在 UAC 上点了否。
    /// 到这一步只剩「用户自己以管理员身份运行 JASM」这一条路了，具体是哪种见
    /// <see cref="GameKeySendResult.Detail"/>。
    /// </summary>
    NeedsElevation,

    /// <summary>
    /// 按键没能送达：本进程 <c>SendInput</c> 插入的事件数少于请求数（典型原因：反作弊拦截合成输入），
    /// 或提权助手在跑但拒发 / 没回话。具体原因见 <see cref="GameKeySendResult.Detail"/>。
    /// </summary>
    SendInputFailed
}

/// <summary>
/// 送键的结果。
///
/// <see cref="Detail"/> 是**给用户看**的一句话，只在失败原因**动态**时才有值 ——
/// 提权助手回执里的 token（没抢到前台 / 被反作弊拦下 / 版本过旧…）每种都对应不同的下一步动作
/// （「先点一下游戏画面再试」和「更新 JASM」不是一回事），一句写死的文案盖不住。
/// 为 null 时调用方按 <see cref="Status"/> 取通用文案。
/// </summary>
public readonly record struct GameKeySendResult(GameKeySendStatus Status, string? Detail = null)
{
    public static GameKeySendResult Sent { get; } = new(GameKeySendStatus.Sent);

    public static GameKeySendResult From(GameKeySendStatus status) => new(status);

    public static GameKeySendResult WithDetail(GameKeySendStatus status, string? detail) => new(status, detail);
}

public interface IGameKeySender
{
    /// <summary>
    /// 把 <paramref name="virtualKey"/>（按 <paramref name="modifierKeyCodes"/> 顺序按住修饰键）合成发送给游戏。
    ///
    /// 行为：先把游戏窗口切到前台 → 等焦点稳定 → 按下 → 保持约 80ms → 抬起。
    /// 焦点**留在游戏上**，不会把 JASM 抢回前台。
    ///
    /// 游戏以管理员身份运行时，本进程的 <c>SendInput</c> 会被 UIPI 静默丢弃，
    /// 这时改由提权助手代发（见 <c>ElevatorService.TrySendKeyChordAsync</c>）——
    /// 首次会弹一次 UAC，之后整个会话复用同一个提权进程。
    ///
    /// 只有 <paramref name="ct"/> 被取消会抛 <see cref="OperationCanceledException"/>，
    /// 其余失败一律返回结果（不抛异常）。
    /// </summary>
    Task<GameKeySendResult> SendKeyAsync(ushort virtualKey, IReadOnlyList<ushort> modifierKeyCodes,
        CancellationToken ct = default);
}