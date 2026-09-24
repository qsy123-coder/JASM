namespace GIMI_ModManager.Core.Helpers;

/// <summary>提权助手（<c>Elevator.exe</c>）对「归还前台」命令的回复。</summary>
public enum ElevatorForegroundHandbackReply
{
    /// <summary>没读到可识别的回复（连接断了 / 读到 EOF）。对面多半是不认识 <c>4</c> 的旧版助手。</summary>
    None,

    /// <summary>前台已经落到指定窗口上。</summary>
    Ok,

    /// <summary>没能交还，原因见解析出的 token。</summary>
    Failure
}

/// <summary>
/// 主程序 ↔ <c>Elevator.exe</c> 的归还前台命令 <c>4</c>。
///
/// **为什么需要它**：浮窗里勾选一个 Mod 会触发 F10（见 <see cref="ElevatorKeySendProtocol"/>），
/// 而送键**必须先把游戏切到前台** —— F10 要打到前台窗口上，d3dx 才认；于是送完键，前台就留在游戏手里了。
/// 浮窗随后想把前台收回来时自己办不到：游戏是提权运行的（完整性「高」，JASM 是「中」），
/// 于是两条路都被堵死 ——
/// <list type="number">
/// <item>直接 <c>SetForegroundWindow</c>：前台锁只认「自己就是前台进程 / 最近收到输入的那个进程」，
/// 本进程两个都不是；</item>
/// <item>先注入一次输入把身份拿回来：那一下会被 UIPI **静默丢弃**（实测：前台是提权游戏时连拒多拍，
/// 同一招在前台是同级窗口时立刻成功）。</item>
/// </list>
/// 而**注入的输入算在注入者头上** —— 提权那一档的按键是助手注入的，所以那一刻的「输入所有者」是助手，
/// 也只有它能把前台交还（它刚刚注入过，<c>SetForegroundWindow</c> 立刻就能生效，
/// 与 <c>ForegroundWindowActivator</c> 里记的「助手刚被拉起那一次抢得动」是同一条规则）。
///
/// 线路格式沿用 <see cref="ElevatorKeySendProtocol"/> 的约定：
/// <code>
/// 客户端 → 助手          助手 → 客户端
/// 4                     OK
/// &lt;hwnd 十六进制&gt;        FAIL:&lt;reason&gt;
/// </code>
///
/// **为什么单开一条命令而不是并进送键 <c>3</c>**：<c>3</c> 那条路已经实机验证过，给它加一行载荷就等于改它，
/// 而旧助手不认识多出来的那一行、会把它当成**下一条命令**读走，管道从此错位。新命令的代价是
/// 旧助手静默忽略（不回执），主程序读到「无回执」即知版本过旧 —— 两种版本靠版本号确定性区分
/// （见 <see cref="MinimumFileVersionForForegroundHandback"/>）。
///
/// **尽力而为**：交还失败不该影响已经送出去的按键，所以没有给用户看的文案，
/// 调用方只把它记进日志。
/// </summary>
public static class ElevatorForegroundHandbackProtocol
{
    /// <summary>归还前台命令，后面跟一行载荷（hwnd 十六进制）。</summary>
    public const string HandbackCommand = "4";

    /// <summary>
    /// 助手会 <see cref="HandbackCommand"/> 的能力标记：<c>Elevator.exe</c> 的 FileVersion 下限。
    ///
    /// 每个能力各占一个版本号、且**只增不改**（<c>2.0.0.0</c> 带目标的刷新、<c>3.0.0.0</c> 送键）——
    /// 旧助手收到不认识的命令是**静默忽略**，只有版本号能确定性区分「不认识这条命令」与「这次没做成」。
    /// </summary>
    public const string MinimumFileVersionForForegroundHandback = "4.0.0.0";

    /// <summary>
    /// 这个 <c>Elevator.exe</c> 认不认 <see cref="HandbackCommand"/>。
    /// 版本读不出来时按「不认识」处理（保守方向 = 退回「前台留在游戏上」的既有行为）。
    /// </summary>
    public static bool SupportsForegroundHandback(string? elevatorFileVersion)
    {
        return Version.TryParse(elevatorFileVersion, out var actual)
               && Version.TryParse(MinimumFileVersionForForegroundHandback, out var minimum)
               && actual >= minimum;
    }

    /// <summary>
    /// 把 <c>4</c> 的载荷编成两行（命令行 + hwnd）。句柄那一行走
    /// <see cref="KeyHelperProtocol.BuildWindowLine"/>：与送键命令共用同一份格式，
    /// 助手那边也是同一个解析器（提权助手工程把该文件直接编进去）。
    /// </summary>
    public static string[] BuildPayload(nint window)
    {
        return
        [
            HandbackCommand,
            KeyHelperProtocol.BuildWindowLine(window)
        ];
    }

    /// <summary>
    /// 解析助手的回复行。空行 / EOF / 认不出的行都算 <see cref="ElevatorForegroundHandbackReply.None"/> ——
    /// 调用方据此区分「助手拒交（有原因）」与「助手压根没回话（版本过旧 / 已退出）」。
    /// </summary>
    public static ElevatorForegroundHandbackReply ParseReply(string? line, out string? failureReason)
    {
        failureReason = null;

        if (!KeyHelperProtocol.TryParseReply(line, out var succeeded, out var reason))
            return ElevatorForegroundHandbackReply.None;

        if (succeeded)
            return ElevatorForegroundHandbackReply.Ok;

        failureReason = reason;
        return ElevatorForegroundHandbackReply.Failure;
    }
}