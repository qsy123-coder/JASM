namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 「这一份进程与 shell 的完整性级别谁高」的三档关系。拖拽能不能用**只**取决于它，
/// 与「是不是管理员」无关（UAC 关掉的机器上 explorer 自己就是高，拿管理员身份去判会弹假警报）。
/// </summary>
public enum IntegrityRelation
{
    /// <summary>读不到 shell 的完整性级别（explorer 正重启、跨会话、权限不够）。</summary>
    Unknown,

    /// <summary>两边同级 —— 拖拽在权限这一档上没有问题。</summary>
    Same,

    /// <summary>本进程更高（用户勾了「以管理员身份运行」）。实测：中→高被 UIPI 拒。</summary>
    OwnHigher,

    /// <summary>本进程更低（整机提权/UAC 关闭的机器上 explorer 是 High，而本进程被普通权限拉起）。</summary>
    OwnLower
}

/// <summary>
/// 拖拽自检的结论档位。
///
/// <para>
/// 五档里前三档在用户屏幕上是**同一个样子**（禁止光标 + 松手没反应），所以判定必须同时用到
/// 「完整性级别」与「事件到底有没有进进程」两半证据 —— 缺任何一半都分不开它们。
/// </para>
/// </summary>
public enum DragDropSelfCheckVerdict
{
    /// <summary>框里收到了拖拽事件 ⇒ 这台机器的拖拽链路是通的（问题若还在，就在某个落点自己的判据上）。</summary>
    EventsArrived,

    /// <summary>没收到事件，且本进程比 shell 高 ⇒ 管理员启动，被 UIPI 挡在中→高这一档。</summary>
    BlockedByOwnHigherIntegrity,

    /// <summary>没收到事件，且本进程比 shell 低 ⇒ 被挡在「来源比目标高」这一档（实测同样禁止）。</summary>
    BlockedByOwnLowerIntegrity,

    /// <summary>没收到事件，但两边同级 ⇒ 整机拖放投递被第三方接管（网维 / 无盘 / 安全软件，网吧机最常见）。</summary>
    BlockedByMachineChannel,

    /// <summary>没收到事件，且连 shell 的级别都读不到 ⇒ 只能说「事件没到」，原因定不了。</summary>
    Undetermined
}

/// <summary>
/// 「拖拽为什么用不了」的判定 —— **纯逻辑**，不碰 win32。
/// 读级别那一半在 WinUI 的 <c>WindowProcessQuery</c> / <c>AppElevation</c>，这里只负责下结论。
///
/// <para>
/// <b>为什么值得单独成类</b>：2026-10 的报障里「拖不进 JASM」有**三条**完全不同的来路，
/// 而用户在屏幕上看到的**一模一样**（禁止光标 + 松手没反应、不报任何错）：
/// </para>
/// <list type="number">
/// <item><b>本进程比 shell 高</b>（用户勾了「以管理员身份运行」）—— 实测中→高被 UIPI 拒；</item>
/// <item><b>本进程比 shell 低</b>（整机提权 / UAC 关闭的机器上 explorer 自己就是 High，而本进程是被
/// 普通权限的程序拉起来的）—— 实测高→中**同样**被拒。这一条与 MSDN 博文写的「来源 ≥ 目标」相反，
/// 是本项目实测纠正过的结论，也是最容易漏掉的一档：它与第 1 条方向相反，却给出同一个症状；</item>
/// <item><b>两边同级却收不到事件</b> —— 整机拖放通道被网维 / 无盘客户端接管（网吧机上实测，
/// 那种环境下「拖不进任何程序」正是那些软件的正常工作状态）。</item>
/// </list>
///
/// <para>
/// 第 1/2 条与第 3 条在屏幕上分不开，所以自检必须真的让用户拖一次（<see cref="WindowSeconds"/> 秒内），
/// 拿「事件有没有进到进程」这半条证据去分。
/// </para>
/// </summary>
public static class DragDropSelfCheck
{
    /// <summary>
    /// 等拖拽事件的窗口长度（秒）。10 秒是「够用户把文件从资源管理器拖过来、又不至于让他对着框发呆」的折中：
    /// 拖拽是手边动作，超过十秒还没动就说明他没在做这件事，继续等着只会让人以为按钮坏了。
    /// </summary>
    public const int WindowSeconds = 10;

    /// <summary>
    /// 完整性级别的档位名。**读到的值与回填的文案只在这一处换算** ——
    /// 自检报告、日志、提示条都从这里取，免得同一台机器在两处被说成不同的档位。
    /// </summary>
    public static string DescribeIntegrityLevel(uint? integrityRid) => integrityRid switch
    {
        null => "未知",
        0x0000 => "不受信任(0x0000)",
        0x1000 => "低(0x1000)",
        0x2000 => "中(0x2000)",
        0x3000 => "高(0x3000)",
        0x4000 => "系统(0x4000)",
        var other => $"0x{other:X}"
    };

    /// <summary>三档关系的一句话说法。</summary>
    public static string DescribeRelation(IntegrityRelation relation) => relation switch
    {
        IntegrityRelation.Same => "同级",
        IntegrityRelation.OwnHigher => "本进程更高",
        IntegrityRelation.OwnLower => "本进程更低",
        _ => "未知"
    };

    /// <summary>
    /// 比较两个完整性级别。<paramref name="shellIntegrityRid"/> 为 <c>null</c>（读不到）时返回
    /// <see cref="IntegrityRelation.Unknown"/> —— 拿不到 shell 就下不了结论，这时**不能**退化成
    /// 「照管理员身份判」：UAC 关掉的机器上那样会给出正好相反的结论。
    /// </summary>
    public static IntegrityRelation Compare(uint ownIntegrityRid, uint? shellIntegrityRid)
    {
        if (shellIntegrityRid is not { } shell)
            return IntegrityRelation.Unknown;

        if (ownIntegrityRid == shell)
            return IntegrityRelation.Same;

        return ownIntegrityRid > shell ? IntegrityRelation.OwnHigher : IntegrityRelation.OwnLower;
    }

    /// <summary>
    /// 出结论。<paramref name="dragEventReceived"/> 是**实测**那一半：用户往自检框里拖一次，
    /// 框的 DragOver 有没有被触发。
    /// </summary>
    public static DragDropSelfCheckVerdict Classify(bool dragEventReceived, IntegrityRelation relation)
    {
        // 事件到了就是通了 —— 哪怕级别不等也说明这条通道没被掐。
        // （这种情况在真实机器上很少见，但「实测优先于推断」是这里的纪律：拿不到实测就说推断，
        // 拿得到实测就别说推断。）
        if (dragEventReceived)
            return DragDropSelfCheckVerdict.EventsArrived;

        return relation switch
        {
            IntegrityRelation.OwnHigher => DragDropSelfCheckVerdict.BlockedByOwnHigherIntegrity,
            IntegrityRelation.OwnLower => DragDropSelfCheckVerdict.BlockedByOwnLowerIntegrity,
            IntegrityRelation.Same => DragDropSelfCheckVerdict.BlockedByMachineChannel,
            _ => DragDropSelfCheckVerdict.Undetermined
        };
    }

    /// <summary>
    /// 已知会接管拖放投递的进程名（网吧管理 / 无盘客户端的实测名单，2026-10-07 那台机器上抓到的）。
    ///
    /// <para>
    /// **只列实测见过的**：这张名单是用来在自检报告里点名「你机器上跑着这些东西」的，
    /// 猜一个名字加进去的代价是冤枉一台正常机器，所以宁可少列。
    /// 三个 <c>TODO: &lt;公司名&gt;</c> 的守护进程当时只拿到了进程名，一并列着当线索。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> KnownDragBlockerProcessNames { get; } =
    [
        "NBMSClient",       // 网吧管理系统
        "ylhost",           // 顺网云海 / 网维那套
        "CloudAppCenter",   // 无盘云端应用分发
        "yule_agent",       // 厂商名是 TODO 的守护进程（实测同机出现）
        "Host_Monitor",
        "log_client"
    ];

    /// <summary>
    /// 从一批进程名里挑出已知会接管拖放的那些，**按名单本身的顺序**返回 ——
    /// 输出顺序必须跟输入无关：进程枚举出来的先后是不确定的，报告里点名的那几行若跟着它抖，
    /// 同一台机器两次自检读起来就不一样，反而像出了问题。
    ///
    /// 带不带 <c>.exe</c>、大小写不同都认；空名字与空白直接跳过；同一个进程列出两次也只算一次。
    /// </summary>
    public static IReadOnlyList<string> FindKnownDragBlockers(IEnumerable<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in processNames)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            present.Add(Path.GetFileNameWithoutExtension(raw.Trim()));
        }

        return KnownDragBlockerProcessNames.Where(present.Contains).ToList();
    }
}