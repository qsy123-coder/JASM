using System.Globalization;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 「以普通权限重启自己（去提权）」这条路的两块纯逻辑：**交接标记**的读写与时效判断。
/// 不含任何 win32 / 文件 IO，所以能被单测。
///
/// <para>
/// 这条路存在的理由只有一个：提权之后跨完整性级别的拖拽会被 UIPI 整个掐掉（浮窗与主窗口同时
/// 只剩禁止光标、松手没反应、且不报任何错），而用户往往不知道自己是怎么被提权的 ——
/// exe 属性 → 兼容性里勾一下「以管理员身份运行此程序」，之后双击就是提权运行。
/// </para>
///
/// <para>
/// <b>为什么用文件而不是命令行参数来交接</b>：去提权只能靠 shell（explorer）代起进程
/// （它是中完整性，它启动的子进程就是中完整性），而 <c>explorer.exe "路径"</c> **传不了参数**。
/// 于是新的一份与正在退出的那一份映像路径完全相同、命令行也相同，单实例检查会把新进程当成
/// 「已经有一个 JASM 在跑」而把它挡回去 —— 用户看到的是「点了重启，什么都没发生」。
/// 落一个带**前任 pid**的标记文件就同时解决了两件事：新进程知道自己该等谁、也认得出自己是接班的那一份。
/// </para>
/// </summary>
public static class UnelevatedRelaunchProtocol
{
    /// <summary>交接标记的文件名（放在 JASM 的数据目录里，与 <c>Elevator.version</c> 同级）。</summary>
    public const string MarkerFileName = "unelevated-relaunch.txt";

    /// <summary>
    /// 标记多久算新鲜。它不是缓存而是**一次性的交接凭条**：
    /// 真正该用它的是紧接着启动的那一份，隔了几分钟的标记只可能是上次启动失败留下的垃圾，
    /// 拿它去等一个早就不存在的 pid 只会让启动白等一轮。
    /// </summary>
    public static readonly TimeSpan MarkerValidity = TimeSpan.FromSeconds(60);

    /// <summary>把「前任的进程 id」写成标记文件的内容。</summary>
    public static string FormatMarker(int predecessorProcessId) =>
        predecessorProcessId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 读标记文件的内容。格式不对（空、非数字、负数）返回 false —— 宁可不认，
    /// 也不要拿一个瞎猜的 pid 去等。
    /// </summary>
    public static bool TryParseMarker(string? content, out int predecessorProcessId)
    {
        predecessorProcessId = 0;

        if (!int.TryParse(content?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            return false;

        if (parsed <= 0)
            return false;

        predecessorProcessId = parsed;
        return true;
    }

    /// <summary>
    /// 这份标记还能不能用来判定交接：内容可解析 + 写下来还没超过 <see cref="MarkerValidity"/>。
    /// </summary>
    /// <param name="content">标记文件的内容；文件不存在给 null。</param>
    /// <param name="writtenUtc">标记文件的最后写入时间（UTC）。</param>
    /// <param name="nowUtc">当前时间（UTC）。</param>
    /// <param name="predecessorProcessId">解析出来的前任进程 id。</param>
    public static bool IsHandoffPending(string? content, DateTime writtenUtc, DateTime nowUtc,
        out int predecessorProcessId)
    {
        if (!TryParseMarker(content, out predecessorProcessId))
            return false;

        var age = nowUtc - writtenUtc;

        // 时钟被往回调过（age 为负）时也认：宁可多等一轮，也别因为改过系统时间就把交接丢掉
        return age <= MarkerValidity;
    }
}