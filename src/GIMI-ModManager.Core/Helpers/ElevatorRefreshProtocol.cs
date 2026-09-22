using System.Globalization;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>提权助手（<c>Elevator.exe</c>）对刷新命令的回复。</summary>
public enum ElevatorRefreshReply
{
    /// <summary>没读到可识别的回复（连接断了 / 读到 EOF）。对面多半是旧版助手，不认识新命令。</summary>
    None,

    /// <summary>已把目标切到前台并发出 F10。</summary>
    Ok,

    /// <summary>拒发，原因见解析出的 token。</summary>
    Failure
}

/// <summary>
/// 主程序 ↔ <c>Elevator.exe</c> 的刷新命令（纯字符串逻辑，不碰文件系统也不碰 win32，所以放在 Core 里可单测）。
///
/// 线路格式沿用仓库里既有的约定（<c>HandleCopyCommand</c> 与 <see cref="KeyHelperProtocol"/> 同一套）：
/// <code>
/// 客户端 → 助手                 助手 → 客户端
/// 0                            （不回，历史命令）
/// 2                            OK
/// &lt;hwnd 十进制&gt;                 FAIL:&lt;reason&gt;
/// </code>
///
/// **为什么载荷是 hwnd 而不是进程名**：找游戏窗口这件事必须用 <c>EnumWindows</c> 现找，
/// 不能用 <c>Process.MainWindowHandle</c>（首次访问即缓存，游戏进出全屏 / 换分辨率重建窗口后就陈旧了，
/// 见 <c>GameKeySender</c> 顶部第 2 条约束）。而游戏身份（进程名/ini 在哪）只有主程序知道 ——
/// 助手连 d3dx.ini 在哪都不知道，所以它只做「提权才能做的那一段」：
/// 还原窗口 → 切前台 → 回读校验 → 发 F10。顺带满足「载荷里每个字段都是数字」这条既有安全约定。
/// </summary>
public static class ElevatorRefreshProtocol
{
    /// <summary>历史刷新命令：助手内部写死目标（原神），**单向无回复**。保留是为了兼容旧版助手。</summary>
    public const string LegacyRefreshCommand = "0";

    /// <summary>带目标的刷新命令，后面跟一行 hwnd。</summary>
    public const string TargetedRefreshCommand = "2";

    /// <summary>成功回复。</summary>
    public const string OkReply = "OK";

    /// <summary>失败回复前缀。</summary>
    public const string FailurePrefix = "FAIL:";

    // ── 失败原因 token（助手产出，客户端映射成日志文案）────────────
    /// <summary>载荷不是合法的窗口句柄 / 窗口已经不存在。</summary>
    public const string ReasonBadPayload = "bad-payload";

    /// <summary>窗口在前台没抢到 —— 此时发 F10 会打进别的窗口，所以助手拒发。</summary>
    public const string ReasonNotForeground = "not-foreground";

    /// <summary>
    /// 助手会 <see cref="TargetedRefreshCommand"/> 的能力标记：<c>Elevator.exe</c> 的 FileVersion 下限。
    ///
    /// 旧版 <c>Elevator.exe</c> 没有版本号（默认 1.0.0.0），所以「版本低于它」就等价于「不认识 <c>2</c>」。
    /// 为什么不用「按进程名判断」之类的推断：旧助手收到不认识的命令会**静默忽略**，
    /// 只有版本是能确定性区分两者的信息。读磁盘上的版本即可 —— 正在运行的 exe 无法被覆盖（文件被锁），
    /// 所以磁盘版本 == 正在跑的那个进程的版本。
    /// </summary>
    public const string MinimumFileVersionForTargetedRefresh = "2.0.0.0";

    /// <summary>
    /// 这个 <c>Elevator.exe</c> 认不认 <see cref="TargetedRefreshCommand"/>。
    /// 参数是 <c>FileVersionInfo.FileVersion</c> 那个字符串（这里不收 <c>FileVersionInfo</c>：
    /// 它没有公开构造函数，收了就没法单测）。
    /// 版本读不出来（没有版本信息 / 不是版本号）时按「不认识」处理 —— 保守方向是不改变既有行为。
    /// </summary>
    public static bool SupportsTargetedRefresh(string? elevatorFileVersion)
    {
        return Version.TryParse(elevatorFileVersion, out var actual)
               && Version.TryParse(MinimumFileVersionForTargetedRefresh, out var minimum)
               && actual >= minimum;
    }

    /// <summary>
    /// 把 <c>2</c> 的载荷编成两行（命令行 + hwnd）。
    /// hwnd 用十进制而不是十六进制：两边的解析都是 <c>long</c>，少一层进制约定就少一个出错的地方。
    /// </summary>
    public static string[] BuildTargetedRefreshPayload(nint targetWindow)
    {
        return
        [
            TargetedRefreshCommand,
            ((long)targetWindow).ToString(CultureInfo.InvariantCulture)
        ];
    }

    /// <summary>解析助手的回复行。空行 / EOF / 认不出的行都算 <see cref="ElevatorRefreshReply.None"/>。</summary>
    public static ElevatorRefreshReply ParseReply(string? line, out string? failureReason)
    {
        failureReason = null;

        var trimmed = line?.TrimEnd('\r').Trim();
        if (string.IsNullOrEmpty(trimmed))
            return ElevatorRefreshReply.None;

        if (trimmed.Equals(OkReply, StringComparison.OrdinalIgnoreCase))
            return ElevatorRefreshReply.Ok;

        if (!trimmed.StartsWith(FailurePrefix, StringComparison.OrdinalIgnoreCase))
            return ElevatorRefreshReply.None;

        failureReason = trimmed[FailurePrefix.Length..].Trim();
        return failureReason.Length > 0 ? ElevatorRefreshReply.Failure : ElevatorRefreshReply.None;
    }
}