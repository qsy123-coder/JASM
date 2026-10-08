using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.Input;
using Serilog;

namespace GIMI_ModManager.WinUI.Services;

/// <summary>
/// 启动时若本进程是「高完整性」（管理员身份），就**换一份中完整性的自己**再跑下去。
///
/// <para>
/// <b>为什么必须这么做</b>：Windows 只允许把拖拽投递给「完整性级别不高于拖拽来源」的窗口
/// （UIPI，[官方说明](https://learn.microsoft.com/nl-be/archive/blogs/patricka/q-why-doesnt-drag-and-drop-work-when-my-application-is-running-elevated-a-mandatory-integrity-control-and-uipi)）。
/// 于是「以管理员身份运行」会把拖拽安装整个掐死：拖 Mod 进主窗口或浮窗只剩禁止光标、松手没反应、
/// 连 DragOver 都不会来（所以「拖拽时才提示」的做法永远等不到机会）。而真实用户里有一大批机器
/// （UAC 关闭 / 内置 Administrator 账户）**双击 exe 就是管理员**，他们在设置里找不到任何
/// 「权限」开关，只看到拖拽不能用。
/// </para>
///
/// <para>
/// <b>为什么是「重启一份」而不是「就地把自己令牌改低」</b>：后者实测<b>不管用</b> ——
/// 进程对象是在创建那一刻按当时令牌的完整性级别定下来的，事后改令牌，这个进程照样收不到拖拽
/// （同一台机器上对照过：提权出生 + 就地降级 → 拖浮窗毫无反应、日志里连事件都没有；
/// 出生即中完整性 → 拖拽到达并安装成功）。所以只能在启动最前面换一份新的。
/// </para>
///
/// <para>
/// 新的一份用的是**从自己令牌降级得来的**中完整性令牌（不是借 explorer 的）——
/// 这样在 UAC 关闭的机器上也成立，而且管理员组仍留在令牌里，写受保护目录的 ACL 那条路照走
/// （实测：降级后仍能写 <c>Program Files</c>）。交接靠 <see cref="UnelevatedRelaunchMarker"/>
/// 那张落盘凭条：新的一份据此等前任退出，并避开单实例检查。
/// </para>
///
/// <para>
/// ⚠️ 调用时机是硬性的：必须在任何 <see cref="AppElevation"/> 读取之前（那两处是 <c>Lazy&lt;&gt;</c> 缓存，
/// 先读到 High 就会一直缓存 High，症状是「其实已经是中完整性，主窗口那条提权提示条却照样弹」）。
/// </para>
/// </summary>
internal static class IntegrityDowngrade
{
    /// <summary>「高」完整性级别（提权进程）。</summary>
    private const uint HighIntegrityRid = 0x3000;

    /// <summary>本次启动的处理结果。</summary>
    internal static IntegrityOutcome Outcome { get; private set; } = IntegrityOutcome.NotElevated;

    /// <summary>失败原因（<see cref="IntegrityOutcome.Failed"/> 时有值）。</summary>
    internal static string? FailureReason { get; private set; }

    /// <summary>
    /// 高完整性时换一份中完整性的自己。非高完整性（绝大多数用户）什么都不做。
    /// <b>绝不抛异常</b>：这是启动路径上的第一步，失败只是「拖拽可能不可用」，不该拦住启动。
    /// </summary>
    /// <returns><see cref="IntegrityOutcome.Relaunching"/> = 新的一份已经起来，调用方应当把本进程退掉。</returns>
    internal static IntegrityOutcome RelaunchAtMediumIfElevated(ILogger logger)
    {
        try
        {
            // 凭条在 = 本进程就是上一份重启出来的：绝不能再降级重启一次（一份接一份没完）
            if (UnelevatedRelaunchMarker.IsPendingHandoff(logger))
                return Outcome;

            // 刻意用 TryReadIntegrityLevelRid 而不是 IsOwnProcessElevated()：后者读的是 Lazy 缓存，
            // 在这里读一次就会把「降级前」的 High 永久缓存下来
            if (WindowProcessQuery.TryReadIntegrityLevelRid((uint)Environment.ProcessId) is not { } rid
                || rid < HighIntegrityRid)
                return Outcome = IntegrityOutcome.NotElevated;

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return Fail("拿不到自己的映像路径，无法换一份跑");

            // 凭条必须**先写**：新的一份若在凭条出现之前就跑完单实例检查，它会把自己当成
            // 「已经有一个 JASM 在跑」然后静默退出 —— 用户看到的是「双击了，什么都没发生」。
            UnelevatedRelaunchMarker.Write(logger, Environment.ProcessId);

            var result = UnelevatedLauncher.LaunchLowered(exePath, BuildArguments(), logger);
            if (!result.Success)
                return Fail(result.Detail);

            logger.Information("[降权] 启动时为高完整性（管理员身份），已换一份中完整性的自己：{Detail}",
                result.Detail);
            return Outcome = IntegrityOutcome.Relaunching;
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    /// <summary>给启动日志用的一行说明（收用户日志时唯一能看出「他到底怎么启动的」的地方）。</summary>
    internal static string Describe() => Outcome switch
    {
        IntegrityOutcome.Relaunching => "已换一份中完整性的自己启动（本进程即将退出）",
        IntegrityOutcome.Failed =>
            $"启动时为高完整性（管理员身份），换一份中完整性的自己失败：{FailureReason}"
            + " —— 拖拽安装在这份进程里不可用",
        _ => "中完整性，非管理员启动（拖拽安装可用）"
    };

    /// <summary>把原命令行参数原样转交给新的一份（带参数启动的场景不该在这一次重启里丢掉）。</summary>
    private static string? BuildArguments()
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        return arguments.Length == 0 ? null : string.Join(" ", arguments.Select(argument => $"\"{argument}\""));
    }

    private static IntegrityOutcome Fail(string reason)
    {
        FailureReason = reason;
        return Outcome = IntegrityOutcome.Failed;
    }
}

/// <summary><see cref="IntegrityDowngrade"/> 的启动结果。</summary>
internal enum IntegrityOutcome
{
    /// <summary>本来就是中完整性（绝大多数用户），或者本进程已经是被换出来的那一份：没做任何事。</summary>
    NotElevated,

    /// <summary>是高完整性，已经起了中完整性的一份 —— 本进程应当立刻退出，把位置让给它。</summary>
    Relaunching,

    /// <summary>是高完整性但换不出来 —— 这份进程里拖拽不可用，主窗口那条提示条仍是唯一出路。</summary>
    Failed
}
