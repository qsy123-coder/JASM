using System.Globalization;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>提权助手（<c>KeyHelperHost</c>）支持的命令。</summary>
public enum KeyHelperCommand
{
    /// <summary>不认识 / 空行。</summary>
    Unknown,

    /// <summary>合成本次按键（后面跟 3 行载荷：vk / mods / hwnd）。</summary>
    SendKey,

    /// <summary>回 OK 后助手退出。</summary>
    Exit,

    /// <summary>存活探测。</summary>
    Ping
}

/// <summary>一条合法的按键发送请求。字段全部是数字，见 <see cref="KeyHelperProtocol"/> 的说明。</summary>
public readonly record struct KeyHelperSendRequest(ushort VirtualKey, ushort[] ModifierKeyCodes, nint TargetWindow);

/// <summary>
/// 主程序 ↔ 提权助手之间的行协议（纯字符串逻辑，不碰文件系统也不碰 win32，所以放在 Core 里可单测）。
///
/// 线路格式（UTF-8，每行 <c>\n</c> 结尾）：
/// <code>
/// 客户端 → 助手                          助手 → 客户端
/// PING                                   OK
/// EXIT                                   OK      （回完就退出）
/// SENDKEY                                OK
/// &lt;vk 十进制&gt;                            FAIL:&lt;reason&gt;
/// &lt;mods 逗号分隔十进制，可为空&gt;
/// &lt;hwnd 十六进制&gt;
/// </code>
///
/// **安全要点**：载荷里每个字段都是**数字**（游戏 exe 路径走命令行 argv，不进管道），
/// 所以结构上就没有「往字段里塞换行、伪造出多一条命令」的空间 —— 多出来的换行只会把后续字段挤掉，
/// 于是数字解析失败 → 整条请求按 <see cref="ReasonBadPayload"/> 拒掉。
/// 助手在解析时还会用 <see cref="KeyChordGuard"/> 再过一遍危险组合键（防御性重复，规则只有一份实现）。
/// </summary>
public static class KeyHelperProtocol
{
    /// <summary>SENDKEY 后面跟的载荷行数。</summary>
    public const int SendKeyPayloadLineCount = 3;

    public const string SendKeyCommand = "SENDKEY";
    public const string ExitCommand = "EXIT";
    public const string PingCommand = "PING";

    /// <summary>成功回复。</summary>
    public const string OkReply = "OK";

    /// <summary>失败回复前缀。</summary>
    public const string FailurePrefix = "FAIL:";

    // ── 失败原因 token（助手产出，客户端映射成状态与文案）────────────
    /// <summary>载荷不合法（行数不对 / 数字解析失败 / 修饰键越界）。</summary>
    public const string ReasonBadPayload = "bad-payload";

    /// <summary>危险组合键（<see cref="KeyChordGuard.IsBlockedChord"/> 命中）。</summary>
    public const string ReasonBlockedChord = "blocked-chord";

    /// <summary>目标窗口不是当前前台窗口 —— 按键会打进别的窗口，拒发。</summary>
    public const string ReasonNotForeground = "not-foreground";

    /// <summary>前台窗口的进程不是启动时冻结的那个游戏 exe，拒发。</summary>
    public const string ReasonTargetMismatch = "target-mismatch";

    /// <summary>发命令的客户端不是拉起助手的那个 JASM 进程，拒答。</summary>
    public const string ReasonUnauthorized = "unauthorized";

    /// <summary>SendInput 插入的事件数少于请求数。</summary>
    public const string ReasonInjectionFailed = "injection-failed";

    /// <summary>把一条发送请求编成 <see cref="SendKeyPayloadLineCount"/> 行载荷（不含命令行的 <c>SENDKEY</c>）。</summary>
    public static string[] BuildSendKeyPayload(ushort virtualKey, IReadOnlyList<ushort> modifierKeyCodes,
        nint targetWindow)
    {
        var modifiers = modifierKeyCodes.Count == 0
            ? string.Empty
            : string.Join(",", modifierKeyCodes);

        return
        [
            virtualKey.ToString(CultureInfo.InvariantCulture),
            modifiers,
            ((long)targetWindow).ToString("X", CultureInfo.InvariantCulture)
        ];
    }

    /// <summary>识别命令行（大小写不敏感，容忍行尾的 <c>\r</c> —— 万一对面用了 CRLF）。</summary>
    public static KeyHelperCommand ParseCommand(string? line)
    {
        var trimmed = line?.TrimEnd('\r').Trim();
        if (string.IsNullOrEmpty(trimmed))
            return KeyHelperCommand.Unknown;

        if (trimmed.Equals(SendKeyCommand, StringComparison.OrdinalIgnoreCase))
            return KeyHelperCommand.SendKey;

        if (trimmed.Equals(ExitCommand, StringComparison.OrdinalIgnoreCase))
            return KeyHelperCommand.Exit;

        if (trimmed.Equals(PingCommand, StringComparison.OrdinalIgnoreCase))
            return KeyHelperCommand.Ping;

        return KeyHelperCommand.Unknown;
    }

    /// <summary>
    /// 解析 SENDKEY 的三行载荷。任何不合法的地方都返回 false 并给出原因 token（调用方原样回复 FAIL:&lt;原因&gt;）。
    /// </summary>
    public static bool TryParseSendKeyRequest(string? virtualKeyLine, string? modifiersLine, string? windowLine,
        out KeyHelperSendRequest request, out string? failureReason)
    {
        request = default;
        failureReason = null;

        // ① 主键：十进制、不许带符号 / 空白。VK 码就是一个字节，所以 0x00 与超出 0xFF 的一律不是合法 VK
        //（0xFF 也拒：它不是任何键，Windows 里当占位符用）。只靠 ushort 会把 256 这种放进来。
        if (!ushort.TryParse(virtualKeyLine, NumberStyles.None, CultureInfo.InvariantCulture, out var virtualKey)
            || virtualKey is 0x00 or >= 0xFF)
        {
            failureReason = ReasonBadPayload;
            return false;
        }

        // ② 修饰键：空 = 没有；否则逗号分隔的十进制，逐项过白名单（顺带挡住重复）
        ushort[] modifiers;
        var modifiersText = modifiersLine?.TrimEnd('\r') ?? string.Empty;
        if (modifiersText.Length == 0)
        {
            modifiers = [];
        }
        else
        {
            var parts = modifiersText.Split(',');
            modifiers = new ushort[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!ushort.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out modifiers[i]))
                {
                    failureReason = ReasonBadPayload;
                    return false;
                }
            }

            if (!KeyChordGuard.IsAllowedModifiers(modifiers))
            {
                failureReason = ReasonBadPayload;
                return false;
            }
        }

        // ③ 窗口句柄：十六进制（最高位为 1 的地址在允许范围之外，故此按有符号 long 解析后再判正负）
        var windowText = windowLine?.TrimEnd('\r') ?? string.Empty;
        if (!long.TryParse(windowText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var window)
            || window <= 0)
        {
            failureReason = ReasonBadPayload;
            return false;
        }

        // ④ 危险组合键：主程序发之前就拦过，这里再拦一次 —— 规则同一份，等于白送一道防线
        if (KeyChordGuard.IsBlockedChord(virtualKey, modifiers))
        {
            failureReason = ReasonBlockedChord;
            return false;
        }

        request = new KeyHelperSendRequest(virtualKey, modifiers, (nint)window);
        return true;
    }

    /// <summary>成功回复。</summary>
    public static string BuildOkReply() => OkReply;

    /// <summary>
    /// 失败回复。原因是**固定 token**（不塞异常文本，异常进助手自己的日志）——
    /// 顺手把换行压成空格，避免任何未来的调用方把多行内容灌进单行回复。
    /// </summary>
    public static string BuildFailureReply(string reason)
    {
        var safeReason = string.IsNullOrWhiteSpace(reason) ? ReasonBadPayload : reason;
        safeReason = safeReason.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return FailurePrefix + safeReason;
    }

    /// <summary>解析助手的回复行。</summary>
    public static bool TryParseReply(string? line, out bool succeeded, out string? failureReason)
    {
        succeeded = false;
        failureReason = null;

        var trimmed = line?.TrimEnd('\r').Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;

        if (trimmed.Equals(OkReply, StringComparison.OrdinalIgnoreCase))
        {
            succeeded = true;
            return true;
        }

        if (!trimmed.StartsWith(FailurePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        failureReason = trimmed[FailurePrefix.Length..].Trim();
        return failureReason.Length > 0;
    }
}