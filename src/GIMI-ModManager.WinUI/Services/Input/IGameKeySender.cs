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
    /// 目标游戏以管理员身份运行（完整性级别高于 JASM），UIPI 不允许把输入注入进去 ——
    /// 这种情况**发也白发**，所以发之前就先拦下，让用户用管理员身份启动 JASM。
    /// </summary>
    NeedsElevation,

    /// <summary>
    /// SendInput 插入的事件数少于请求数（典型原因：反作弊拦截合成输入；或完整性级别判断拿不到时的兜底）。
    /// </summary>
    SendInputFailed
}

public interface IGameKeySender
{
    /// <summary>
    /// 把 <paramref name="virtualKey"/>（按 <paramref name="modifierKeyCodes"/> 顺序按住修饰键）合成发送给游戏。
    ///
    /// 行为：先把游戏窗口切到前台 → 等焦点稳定 → 按下 → 保持约 80ms → 抬起。
    /// 焦点**留在游戏上**，不会把 JASM 抢回前台。
    ///
    /// 只有 <paramref name="ct"/> 被取消会抛 <see cref="OperationCanceledException"/>，
    /// 其余失败一律返回状态码（不抛异常）。
    /// </summary>
    Task<GameKeySendStatus> SendKeyAsync(ushort virtualKey, IReadOnlyList<ushort> modifierKeyCodes,
        CancellationToken ct = default);
}