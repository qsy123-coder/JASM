using GIMI_ModManager.WinUI.Services.AppManagement;
using Serilog;

namespace GIMI_ModManager.WinUI.Services;

/// <summary>
/// 启动时若**本进程比 shell（explorer）的完整性级别高**，就换一份与 shell 同级的中完整性自己再跑下去。
///
/// <para>
/// <b>拖拽要求两边完整性级别「相同」</b>（实测，不是文档说的「来源 ≥ 目标」）：
/// </para>
/// <list type="bullet">
/// <item>中来源 → 高目标：禁止光标、事件根本不到（提权 JASM 收不到资源管理器拖来的文件就是这么来的）；</item>
/// <item><b>高来源 → 中目标：同样禁止</b>（用提权进程当来源实测过）；</item>
/// <item>中来源 → 中目标：正常，这一档才是能用的那一档。</item>
/// </list>
///
/// <para>
/// 于是「让本进程与 shell 同级」就是唯一能保拖拽的办法，而 shell 的级别由机器决定：
/// </para>
/// <list type="bullet">
/// <item><b>shell 是中</b>（普通机器，用户在快捷方式上勾了「以管理员身份运行」）⇒ 我们高 ⇒
/// 换一份中完整性的自己 ✓；</item>
/// <item><b>shell 也是高</b>（UAC 关闭 / 内置 Administrator 那种「双击 exe 就是管理员」的机器，
/// 网吧机常见）⇒ 本来就同级，<b>绝不能换</b> —— 换成中反而把原本能拖的弄成不能拖。</item>
/// </list>
///
/// <para>
/// <b>为什么是「换一份」而不是「就地把自己令牌改低」</b>：后者实测不管用 —— 进程对象在创建那一刻
/// 就定了，事后改令牌这个进程照样收不到拖拽（同机对照：提权出生 + 就地降级 → 拖浮窗毫无反应、
/// 日志里连事件都没有；出生即中完整性 → 拖拽到达并安装成功）。所以只能在启动最前面换一份新的。
/// </para>
///
/// <para>
/// 新的一份用的是**从自己令牌降级得来的**中完整性令牌（不是借 explorer 的）—— 这样在 UAC 关闭的
/// 机器上也成立，而且管理员组仍留在令牌里，写受保护目录的 ACL 那条路照走（实测：降级后仍能写
/// <c>Program Files</c>）。交接靠 <see cref="UnelevatedRelaunchMarker"/> 那张落盘凭条：
/// 新的一份据此等前任退出，并避开单实例检查。
/// </para>
///
/// <para>
/// ⚠️ 还有一个判据覆盖不到的角落：**我们比 shell 低**（例如 UAC 关闭的机器上 JASM 被一个中完整性
/// 的进程拉起来）。那种情况下拖拽同样不可用，但方向反了、没有任何 API 能把完整性级别升上去，
/// 只能靠用户以管理员身份重开。这种情况很罕见，暂时不做提示。
/// </para>
///
/// <para>
/// ⚠️ 调用时机是硬性的：必须在任何 <see cref="AppElevation"/> 读取之前（那两处是 <c>Lazy&lt;&gt;</c> 缓存，
/// 先读到 High 就会一直缓存 High，症状是「其实已经是中完整性，主窗口那条提权提示条却照样弹」）。
/// </para>
/// </summary>
internal static class IntegrityDowngrade
{
    /// <summary>本次启动的处理结果。</summary>
    internal static IntegrityOutcome Outcome { get; private set; } = IntegrityOutcome.NotElevated;

    /// <summary>失败原因（<see cref="IntegrityOutcome.Failed"/> 时有值）。</summary>
    internal static string? FailureReason { get; private set; }

    /// <summary>
    /// 本进程比 shell 高时，换一份与 shell 同级（中完整性）的自己。其余情况什么都不做。
    /// <b>绝不抛异常</b>：这是启动路径上的第一步，失败只是「拖拽可能不可用」，不该拦住启动。
    /// </summary>
    /// <returns><see cref="IntegrityOutcome.Relaunching"/> = 新的一份已经起来，调用方应当把本进程退掉。</returns>
    internal static IntegrityOutcome RelaunchAtMediumIfElevated(ILogger logger)
    {
        try
        {
            // 凭条在 = 本进程就是上一份换出来的：绝不能再换一次（一份接一份没完）
            if (UnelevatedRelaunchMarker.IsPendingHandoff(logger))
                return Outcome;

            // 判据用 AppElevation.IsDragDropBlocked()（= 我们比 shell 高）而不是 IsOwnProcessElevated()：
            // 后者会把「shell 也是高」那种机器（UAC 关闭 / 整机提权）也一起换掉，而那正是不能换的情况。
            // 顺带也避开 Lazy 缓存：换份之后新进程会重新算一遍。
            if (!AppElevation.IsDragDropBlocked())
                return Outcome;

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return Fail("拿不到自己的映像路径，无法换一份跑");

            // 凭条必须**先写**：新的一份若在凭条出现之前就跑完单实例检查，它会把自己当成
            // 「已经有一个 JASM 在跑」然后静默退出 —— 用户看到的是「双击了，什么都没发生」。
            UnelevatedRelaunchMarker.Write(logger, Environment.ProcessId);

            var result = UnelevatedLauncher.LaunchLowered(exePath, BuildArguments(), logger);
            if (!result.Success)
                return Fail(result.Detail);

            logger.Information("[降权] 本进程比 shell 高（{Detail}），已换一份中完整性的自己",
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
            "本进程比 shell 高，换一份中完整性的自己失败："
            + FailureReason
            + " —— 拖拽安装在这份进程里不可用",
        _ => "与 shell 同级（拖拽安装可用）"
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
    /// <summary>本来就与 shell 同级（绝大多数用户），或者本进程已经是被换出来的那一份：没做任何事。</summary>
    NotElevated,

    /// <summary>比 shell 高，已经换了一份中完整性的 —— 本进程应当立刻退出，把位置让给它。</summary>
    Relaunching,

    /// <summary>比 shell 高但换不出来 —— 这份进程里拖拽不可用，主窗口那条提示条仍是唯一出路。</summary>
    Failed
}
