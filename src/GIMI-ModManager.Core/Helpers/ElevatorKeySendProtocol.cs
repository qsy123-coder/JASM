namespace GIMI_ModManager.Core.Helpers;

/// <summary>提权助手（<c>Elevator.exe</c>）对送键命令的回复。</summary>
public enum ElevatorKeySendReply
{
    /// <summary>没读到可识别的回复（连接断了 / 读到 EOF）。对面多半是不认识 <c>3</c> 的旧版助手。</summary>
    None,

    /// <summary>按键已由提权助手合成发出。</summary>
    Ok,

    /// <summary>拒发，原因见解析出的 token。</summary>
    Failure
}

/// <summary>
/// 主程序 ↔ <c>Elevator.exe</c> 的送键命令 <c>3</c>。
///
/// **为什么送键必须由助手来做**：游戏从 JASM 启动时是提权的（XXMI 注入需要，
/// <c>ModEnvSetupFacade</c> 把启动命令的 <c>RunAsAdmin</c> 配成 true），完整性级别是「高」；
/// 而 JASM 自己的 manifest 是 asInvoker（「中」）。用户态完整性控制（UIPI）会把
/// 「中 → 高」的 <c>SendInput</c> **静默丢弃** —— 发送方拿到的返回值是成功的，游戏却收不到。
/// 所以 <c>SendInput</c> 必须由一个**高完整性**进程发起，系统里唯一那个就是本助手。
///
/// 线路格式沿用 <see cref="ElevatorRefreshProtocol"/> 的约定：
/// <code>
/// 客户端 → 助手                 助手 → 客户端
/// 3                            OK
/// &lt;vk 十进制&gt;                   FAIL:&lt;reason&gt;
/// &lt;mods 逗号分隔十进制，可为空&gt;
/// &lt;hwnd 十进制&gt;
/// </code>
///
/// 三行载荷与回执的编解码**全部委托给 <see cref="KeyHelperProtocol"/>**，不在这里重写一份：
/// 那套协议当初就是为「主程序发键给提权助手」写的，而 <see cref="KeyChordGuard"/> 的危险组合键护栏
/// 只有一份实现 —— 助手那侧是提权的，护栏分家会直接变成「主程序拦得住、助手放过去了」。
/// （本条只借用它的**编解码**：命令字是 Elevator 管道的数字 <c>3</c>，不是它原本的 <c>SENDKEY</c>。）
///
/// **为什么窗口句柄由主程序给、而不是助手自己找**：<c>Process.MainWindowHandle</c> 首次访问即缓存，
/// 游戏进出全屏 / 换分辨率重建窗口后就陈旧了，只有 <c>EnumWindows</c> 现找才可靠；
/// 而「哪个游戏」这件事只有主程序知道（它读得到 JASM 设置里的 d3dx.ini）。
/// 助手只做「提权才能做的那一段」：还原窗口 → 切前台 → 回读校验 → 送键。
/// </summary>
public static class ElevatorKeySendProtocol
{
    /// <summary>送键命令，后面跟三行载荷（vk / mods / hwnd）。</summary>
    public const string SendKeyCommand = "3";

    /// <summary>
    /// 助手会 <see cref="SendKeyCommand"/> 的能力标记：<c>Elevator.exe</c> 的 FileVersion 下限。
    ///
    /// 每个能力各占一个版本号、且**只增不改**：<c>2.0.0.0</c> 是「认识带目标的刷新 <c>2</c>」，
    /// <c>3.0.0.0</c> 才是「认识送键 <c>3</c>」。不能把送键也记在 <c>2.0.0.0</c> 名下 ——
    /// 2.0.0.0 的助手确实存在过（那次 CI 改动），它不认识 <c>3</c>，而旧助手收到不认识的命令是
    /// **静默忽略**，只有版本号能确定性区分两者。
    /// </summary>
    public const string MinimumFileVersionForKeySend = "3.0.0.0";

    /// <summary>
    /// 这个 <c>Elevator.exe</c> 认不认 <see cref="SendKeyCommand"/>。
    /// 参数是 <c>FileVersionInfo.FileVersion</c> 那个字符串（不收 <c>FileVersionInfo</c>：它没有公开构造函数，收了就没法单测）。
    /// 版本读不出来时按「不认识」处理 —— 保守方向是退回「请用户手动以管理员身份运行 JASM」的既有行为。
    /// </summary>
    public static bool SupportsKeySend(string? elevatorFileVersion)
    {
        return Version.TryParse(elevatorFileVersion, out var actual)
               && Version.TryParse(MinimumFileVersionForKeySend, out var minimum)
               && actual >= minimum;
    }

    /// <summary>
    /// 把 <c>3</c> 的载荷编成四行（命令行 + vk + mods + hwnd），交给管道逐行写。
    /// 后三行直接借用 <see cref="KeyHelperProtocol.BuildSendKeyPayload"/>：那边的「字段全是数字」
    /// 与「修饰键白名单」两条约束正是这里要的，重写一遍只会多出一个可能写错的解析器。
    /// </summary>
    public static string[] BuildSendKeyPayload(ushort virtualKey, IReadOnlyList<ushort> modifierKeyCodes,
        nint targetWindow)
    {
        return
        [
            SendKeyCommand,
            .. KeyHelperProtocol.BuildSendKeyPayload(virtualKey, modifierKeyCodes, targetWindow)
        ];
    }

    /// <summary>
    /// 解析助手的回复行。空行 / EOF / 认不出的行都算 <see cref="ElevatorKeySendReply.None"/> ——
    /// 调用方据此区分「助手拒发（有原因）」与「助手压根没回话（版本过旧 / 已退出）」。
    /// </summary>
    public static ElevatorKeySendReply ParseReply(string? line, out string? failureReason)
    {
        failureReason = null;

        if (!KeyHelperProtocol.TryParseReply(line, out var succeeded, out var reason))
            return ElevatorKeySendReply.None;

        if (succeeded)
            return ElevatorKeySendReply.Ok;

        failureReason = reason;
        return ElevatorKeySendReply.Failure;
    }

    /// <summary>
    /// 把助手回复里的原因 token 转成给用户看的一句话。
    /// 认不出的 token 原样带出（不吞掉）：助手将来加了原因，用户至少还能看到原文，
    /// 而不是一句没有信息量的「发送失败」。
    /// </summary>
    public static string DescribeFailure(string? reasonToken)
    {
        return reasonToken switch
        {
            KeyHelperProtocol.ReasonBadPayload =>
                "提权助手收到的送键请求不合法（内部错误）。",
            KeyHelperProtocol.ReasonBlockedChord =>
                "出于安全考虑没有发送这个组合键（Alt+F4 这类会直接把游戏关掉）。",
            KeyHelperProtocol.ReasonNotForeground =>
                "提权助手没能把游戏切到前台，按键会打进别的窗口，所以没有发送。请先点一下游戏画面再试。",
            KeyHelperProtocol.ReasonTargetMismatch =>
                "当前前台窗口不是当初那个游戏，提权助手拒发。请先点一下游戏画面再试。",
            KeyHelperProtocol.ReasonInjectionFailed =>
                "提权助手没能把按键插进系统输入队列（可能被反作弊拦截）。",
            KeyHelperProtocol.ReasonUnauthorized =>
                "提权助手拒绝了这个请求（不是拉起它的那个 JASM 进程）。",
            null or "" => "提权助手没有说明原因。",
            _ => $"提权助手拒绝发送：{reasonToken}"
        };
    }
}