using System.Diagnostics;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.Input;
using GIMI_ModManager.WinUI.Services.Overlay;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Diagnostics;

/// <summary>拖拽自检一次的结论。</summary>
/// <param name="Verdict">档位（<see cref="DragDropSelfCheckVerdict"/>）。</param>
/// <param name="Headline">一句话结论 —— 显示在自检框旁边那一行。</param>
/// <param name="Report">完整报告（多行）—— 用户复制出去发给排查者的就是它。</param>
internal sealed record DragDropSelfCheckResult(DragDropSelfCheckVerdict Verdict, string Headline, string Report);

/// <summary>
/// 「这台机器为什么拖不进去」的自检：把判据采齐、说成一句人话。
///
/// <para>
/// <b>它要终结的是什么</b>：用户报「拖不进去」时，他的屏幕上只有禁止光标 —— 与提权、与整机拖放
/// 被接管、与落点自己的判据（收不收虚拟文件）全都长得一样。此前只能让用户交日志、我们猜、再要日志，
/// 而日志在用户机器上未必找得到（单文件版的日志在 <c>%TEMP%\.net</c> 的自解压目录里）。
/// 有了它，用户自己点一下、把报告复制给我们就完事了。
/// </para>
///
/// <para>
/// 它**不做**的事：不去测「拖拽本身能不能用」。那件事只能由用户真的拖一次来回答
/// （<paramref name="dragEventReceived"/> 就是那次实测的结果），本类只负责把实测与权限测量合起来出结论。
/// </para>
/// </summary>
/// <remarks>
/// <b>为什么是 internal、而且不注入 <c>SettingsViewModel</c> 的构造函数</b>：它依赖
/// <see cref="OverlayWindowService"/>，那一个是**刻意 internal** 的（浮窗整套都只在本程序集里用，见
/// <c>OverlayViewModel</c> 的类注释）；而 <c>SettingsViewModel</c> 是 public（XAML 的 <c>x:Bind</c> 要它），
/// public 构造函数里放不下 internal 的参数类型（CS0051）。把浮窗那套为了一条诊断信息改成 public 是本末倒置，
/// 所以这里按页面那套写法由调用方 <c>App.GetService&lt;&gt;()</c> 取（见 <c>SettingsViewModel.CompleteDragSelfCheck</c>）。
/// </remarks>
internal sealed class DragDropSelfCheckService
{
    private readonly ILanguageLocalizer _localizer;
    private readonly OverlayWindowService _overlayWindowService;
    private readonly ILogger _logger;

    internal DragDropSelfCheckService(ILanguageLocalizer localizer,
        OverlayWindowService overlayWindowService, ILogger logger)
    {
        _localizer = localizer;
        _overlayWindowService = overlayWindowService;
        _logger = logger.ForContext<DragDropSelfCheckService>();
    }

    /// <summary>
    /// 出结论。
    /// </summary>
    /// <param name="dragEventReceived">
    /// 实测那一半：自检框在等待窗口（<see cref="DragDropSelfCheck.WindowSeconds"/> 秒）里
    /// 有没有收到过一次拖拽事件。
    /// </param>
    internal DragDropSelfCheckResult Run(bool dragEventReceived)
    {
        var own = WindowProcessQuery.OwnIntegrityLevelRid;
        var shell = TryReadShellIntegrityLevel();

        // 关系**只从 AppElevation 取**，不在这里再比一次大小：两份判据迟早会分家，
        // 那就会出现「提示条不亮、自检却说被权限挡了」这种自相矛盾的现场。
        var relation = AppElevation.CompareWithShell();

        var verdict = DragDropSelfCheck.Classify(dragEventReceived, relation);
        var headline = Headline(verdict);
        var report = BuildReport(dragEventReceived, own, shell, relation, headline, verdict);

        // 整份报告进日志：用户把报告截图/复制过来时，我们这一侧也留了同一份，
        // 免得「他复制的和我们看到的不是同一次自检」。
        _logger.Information("[拖拽自检] 结论={Verdict}；本进程={Own}；shell={Shell}；关系={Relation}；收到拖拽事件={Received}",
            verdict, DragDropSelfCheck.DescribeIntegrityLevel(own),
            DragDropSelfCheck.DescribeIntegrityLevel(shell), relation, dragEventReceived);

        return new DragDropSelfCheckResult(verdict, headline, report);
    }

    /// <summary>shell（explorer）的完整性级别；读不到返回 <c>null</c>（报告里会写成「未知」）。</summary>
    private static uint? TryReadShellIntegrityLevel()
        => WindowProcessQuery.TryGetShellProcessId() is { } shellProcessId
            ? WindowProcessQuery.TryReadIntegrityLevelRid(shellProcessId)
            : null;

    private string BuildReport(bool dragEventReceived, uint own, uint? shell, IntegrityRelation relation,
        string headline, DragDropSelfCheckVerdict verdict)
    {
        var lines = new List<string>
        {
            Localize("DragSelfCheck_Report_Integrity", "完整性级别：本进程 {0}；资源管理器 {1} → {2}",
                DragDropSelfCheck.DescribeIntegrityLevel(own),
                DragDropSelfCheck.DescribeIntegrityLevel(shell),
                DragDropSelfCheck.DescribeRelation(relation)),

            _overlayWindowService.IsOverlayHotkeyRegistered
                ? Localize("DragSelfCheck_Report_HotkeyRegistered", "浮窗唤出键：{0}（已注册）",
                    _overlayWindowService.OverlayHotkeyDescription ?? "?")
                : Localize("DragSelfCheck_Report_HotkeyMissing",
                    "浮窗唤出键：没有注册上 —— 浮窗唤不出来（候选键都被别的程序占了）"),

            dragEventReceived
                ? Localize("DragSelfCheck_Report_EventReceived", "拖拽事件：收到了（这台机器能把文件拖进 JASM）")
                : Localize("DragSelfCheck_Report_EventMissing", "拖拽事件：{0} 秒内一次都没收到",
                    DragDropSelfCheck.WindowSeconds)
        };

        // 已知会接管拖放的软件只在这台机器上真有才列：没有就不提，免得让用户去查一个他没装的东西。
        var blockers = DragDropSelfCheck.FindKnownDragBlockers(EnumerateProcessNames());
        if (blockers.Count > 0)
            lines.Add(Localize("DragSelfCheck_Report_KnownBlockers", "检测到已知会接管拖放的软件：{0}",
                string.Join("、", blockers)));

        lines.Add(Localize("DragSelfCheck_Report_Conclusion", "结论：{0}", headline));
        lines.Add(Localize("DragSelfCheck_Report_Advice", "出路：{0}", Advice(verdict)));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>一句话结论（也进报告）。五档各一句，用词与出路一致。</summary>
    private string Headline(DragDropSelfCheckVerdict verdict) => verdict switch
    {
        DragDropSelfCheckVerdict.EventsArrived => Localize("DragSelfCheck_Headline_EventsArrived",
            "拖拽链路正常"),
        DragDropSelfCheckVerdict.BlockedByOwnHigherIntegrity => Localize("DragSelfCheck_Headline_Higher",
            "被权限挡住：JASM 正以管理员身份运行"),
        DragDropSelfCheckVerdict.BlockedByOwnLowerIntegrity => Localize("DragSelfCheck_Headline_Lower",
            "被权限挡住：JASM 的权限比资源管理器低"),
        DragDropSelfCheckVerdict.BlockedByMachineChannel => Localize("DragSelfCheck_Headline_MachineBlocked",
            "权限这一档没问题，但拖拽投递被这台机器上的别的软件掐了"),
        _ => Localize("DragSelfCheck_Headline_Undetermined", "拖拽事件没有到达 JASM，原因没判出来")
    };

    /// <summary>
    /// 每一档的出路。**必须给具体动作**：用户点自检就是想解决它，只告诉他「哪里不对」等于把问题
    /// 原样还回去。最后一档（判不出来）给的是不依赖拖放的那条装 Mod 的路。
    /// </summary>
    private string Advice(DragDropSelfCheckVerdict verdict) => verdict switch
    {
        DragDropSelfCheckVerdict.EventsArrived => Localize("DragSelfCheck_Advice_EventsArrived",
            "拖拽本身没问题。若某个页面仍显示禁止光标，那是那一页自己的判据：浮窗只收真文件"
            + "（网盘客户端 / 压缩软件给的虚拟文件不算），角色详情页一次只收一个包。"),

        DragDropSelfCheckVerdict.BlockedByOwnHigherIntegrity => Localize("DragDropSelfCheck_Advice_Higher",
            "Windows 不允许把文件从中等权限的程序拖进高权限的程序。用普通权限重开 JASM："
            + "右键 JASM 的快捷方式 → 属性 → 兼容性 → 取消勾选「以管理员身份运行此程序」。"
            + "（较新版本的 JASM 启动时会自己换一份普通权限的进程，若这份日志里写着「与 shell 同级」却仍然拖不进，"
            + "请把报告发给作者。）"),

        DragDropSelfCheckVerdict.BlockedByOwnLowerIntegrity => Localize("DragDropSelfCheck_Advice_Lower",
            "这台机器的资源管理器本身是高权限（UAC 关闭 / 整机提权 / 网吧机常见），而 JASM 是被普通权限"
            + "拉起来的 —— 两个方向的权限差别都会被 Windows 挡住。请以管理员身份重开 JASM。"),

        DragDropSelfCheckVerdict.BlockedByMachineChannel => Localize("DragDropSelfCheck_Advice_MachineBlocked",
            "两边权限相同却收不到拖拽事件，说明拖放投递被这台机器上的第三方软件接管了"
            + "（网维 / 无盘 / 安全软件，网吧机最常见）—— 那种环境下拖不进任何程序，改 JASM 没用。"
            + "请改用「添加 Mod」按钮装 Mod，它不依赖 Windows 的拖放。"),

        _ => Localize("DragDropSelfCheck_Advice_Undetermined",
            "没能判出原因。请把这份报告发给作者；同时可以改用「添加 Mod」按钮装 Mod —— "
            + "它走文件选择器，不依赖 Windows 的拖放。")
    };

    /// <summary>
    /// 本机所有进程的名字。读不到的（受保护进程）跳过；整个枚举失败就返回空表 ——
    /// 这一项只是报告里的一条线索，不该因为它把整个自检带崩。
    /// </summary>
    private static IReadOnlyList<string> EnumerateProcessNames()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception)
        {
            return [];
        }

        var names = new List<string>(processes.Length);

        foreach (var process in processes)
        {
            try
            {
                names.Add(process.ProcessName);
            }
            catch (Exception)
            {
                // 受保护进程读不到名字：少一条线索，不影响结论
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }

    /// <summary>
    /// 取词条，取不到用内联兜底，**绝不抛**。
    ///
    /// 兜底不是可选项：这里是用户点出来的诊断路径，词条缺失（比如资源没进 PRI）只是配置问题，
    /// 不该升级成「点一下按钮就崩」，也不该让用户看到一条空的结论 —— 那与「自检没跑」在肉眼上分不开。
    /// </summary>
    private string Localize(string key, string fallback, params object?[] args)
    {
        var template = _localizer.GetLocalizedStringOrDefault(key, defaultValue: fallback) ?? fallback;

        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            // 词条里的占位符被翻译弄坏了：宁可露出没填的模板，也不要抛
            return template;
        }
    }
}