using System.Runtime.InteropServices;
using GIMI_ModManager.WinUI.Services.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

namespace GIMI_ModManager.WinUI.Services;

/// <summary>
/// 启动时把本进程从「高完整性」降到「中完整性」。
///
/// <para>
/// <b>为什么必须这么做</b>：Windows 只允许把拖拽投递给「完整性级别不高于拖拽来源」的窗口
/// （UIPI，[官方说明](https://learn.microsoft.com/nl-be/archive/blogs/patricka/q-why-doesnt-drag-and-drop-work-when-my-application-is-running-elevated-a-mandatory-integrity-control-and-uipi)）。
/// 于是「以管理员身份运行」会把拖拽安装整个掐死：拖 Mod 进主窗口或浮窗只剩禁止光标、松手没反应、
/// 连 DragOver 都不会来（所以「拖拽时才提示」的做法永远等不到机会）。用户能做的只有自己发现
/// 「是不是管理员」这件事 —— 而真实用户里有一大批机器（UAC 关闭 / 内置 Administrator 账户）
/// <b>双击 exe 就是管理员</b>，他们在设置里找不到任何「权限」开关，只看到拖拽不能用。
/// </para>
///
/// <para>
/// <b>为什么是「降自己」而不是别的办法</b>：降级是内核允许的方向（升回高完整性需要
/// <c>SeTcbPrivilege</c>，管理员没有这个特权），所以提权进程可以就地把自己降到中完整性，
/// 不需要重启、不需要过 UAC。降级后：
/// </para>
/// <list type="bullet">
/// <item>任何 ≥ 中完整性的来源都能往我们窗口里投递 —— 桌面、资源管理器、浏览器、网盘客户端、QQ；</item>
/// <item><b>管理员写权限一个都没丢</b>：令牌里的管理员组仍是启用的，写受保护目录走的是 ACL
/// 那条路（实机验证过：降级后仍能写 <c>Program Files</c>、仍能覆盖/删除提权时创建的文件）。</item>
/// </list>
///
/// <para>
/// <b>唯一的代价</b>：本进程再也不能给<b>高完整性窗口</b>发键（UIPI 的另一面）—— 那条路本来就
/// 由提权助手 <c>Elevator.exe</c> 代劳，本进程发键是它失败后的回退，不是主路径。
/// </para>
///
/// <para>
/// ⚠️ <b>调用时机是硬性的</b>：必须早于任何 <see cref="AppElevation"/> / <see cref="WindowProcessQuery.OwnIntegrityLevelRid"/>
/// 的读取 —— 那两处是 <c>Lazy&lt;&gt;</c> 缓存，先读到 High 就会把 High 一直缓存下去，
/// 症状是「降级其实成功了，但主窗口顶部那条提权提示条照样弹」。
/// </para>
/// </summary>
internal static unsafe class IntegrityDowngrade
{
    /// <summary>「高」完整性级别（提权进程）。</summary>
    private const uint HighIntegrityRid = 0x3000;

    /// <summary>「中」完整性级别（非提权的普通进程）。</summary>
    private const uint MediumIntegrityRid = 0x2000;

    /// <summary><c>SE_GROUP_INTEGRITY</c>：标记这个 SID 是强制性完整性标签，不是普通组。</summary>
    private const uint SeGroupIntegrity = 0x20;

    private const int SidLength = 12;

    /// <summary>
    /// 本进程的降级结果。启动日志与诊断都读它 —— 降级成功后 <see cref="AppElevation.IsElevated"/>
    /// 已经是 <c>false</c>，光看它无法回答「用户到底是不是以管理员身份启动的」。
    /// </summary>
    internal static IntegrityOutcome Outcome { get; private set; } = IntegrityOutcome.NotElevated;

    /// <summary>降级失败的原因（<see cref="IntegrityOutcome.Failed"/> 时有值），失败也要能说清为什么。</summary>
    internal static string? FailureReason { get; private set; }

    /// <summary>
    /// 高完整性时把自己降成中完整性。非高完整性（绝大多数用户）什么都不做。
    /// <b>绝不抛异常</b>：这是启动路径上的第一步，任何失败都只是「拖拽可能不可用」，不该拦住启动。
    /// </summary>
    internal static IntegrityOutcome LowerToMediumIfElevated()
    {
        try
        {
            // 刻意用 TryReadIntegrityLevelRid 而不是 IsOwnProcessElevated()：后者读的是 Lazy 缓存，
            // 在这里读一次就会把「降级前」的 High 永久缓存下来，正好毁掉这次降级的意义。
            var currentRid = WindowProcessQuery.TryReadIntegrityLevelRid((uint)Environment.ProcessId);
            if (currentRid is not { } rid || rid < HighIntegrityRid)
                return Outcome = IntegrityOutcome.NotElevated;

            // 令牌句柄必须在**还是高完整性**的时候开出来：降级之后再用 TOKEN_ALL_ACCESS 开自己的令牌
            // 会被拒（access denied）。句柄的访问权限在打开那一刻就固定了，所以之后的写入照常。
            //
            // 传 HANDLE* 而不是 out HANDLE：CsWin32 为这个函数生成的进程句柄参数是 SafeHandle 版，
            // 指针版才接得住 GetCurrentProcess() 的返回值（与 WindowProcessQuery 里的用法一致）。
            HANDLE token = default;
            if (!PInvoke.OpenProcessToken(PInvoke.GetCurrentProcess(), TOKEN_ACCESS_MASK.TOKEN_ALL_ACCESS,
                    &token))
                return Fail($"OpenProcessToken 失败（win32={Marshal.GetLastWin32Error()}）");

            try
            {
                if (!SetIntegrityLevel(token, MediumIntegrityRid))
                    return Fail($"SetTokenInformation 失败（win32={Marshal.GetLastWin32Error()}）");
            }
            finally
            {
                PInvoke.CloseHandle(token);
            }

            var afterRid = WindowProcessQuery.TryReadIntegrityLevelRid((uint)Environment.ProcessId);
            if (afterRid != MediumIntegrityRid)
                return Fail($"写入成功但读回是 {Describe(afterRid)}，未达到中完整性");

            return Outcome = IntegrityOutcome.Lowered;
        }
        catch (Exception e)
        {
            return Fail(e.Message);
        }
    }

    /// <summary>给启动日志用的一行说明（这一行是排查「用户到底怎么启动的」的唯一依据）。</summary>
    internal static string Describe()
    {
        var current = WindowProcessQuery.TryReadIntegrityLevelRid((uint)Environment.ProcessId);
        return Outcome switch
        {
            IntegrityOutcome.Lowered =>
                $"启动时为高完整性（管理员身份），已降级为中完整性 —— 拖拽安装可用；"
                + "需要向高完整性窗口发键的动作交给提权助手",
            IntegrityOutcome.Failed =>
                $"启动时为高完整性（管理员身份），降级失败：{FailureReason}"
                + $" —— 拖拽安装在这份进程里不可用（当前={Describe(current)}）",
            _ => $"中完整性，非管理员启动（拖拽安装可用）"
        };
    }

    private static IntegrityOutcome Fail(string reason)
    {
        FailureReason = reason;
        return Outcome = IntegrityOutcome.Failed;
    }

    private static string Describe(uint? rid) =>
        rid is { } value ? $"0x{value:X4}" : "读取失败";

    /// <summary>
    /// 往令牌写强制性完整性标签。
    ///
    /// <para>
    /// SID 是手工拼的 <c>S-1-16-8192</c>（中完整性）而不是走 <c>ConvertStringSidToSid</c>：
    /// 少一个 API 依赖，而且这几个字节是固定的 —— 布局为
    /// <c>{ Revision=1, SubAuthorityCount=1, IdentifierAuthority=SECURITY_MANDATORY_LABEL_AUTHORITY(16),
    /// SubAuthority[0]=0x2000 }</c>。
    /// </para>
    /// </summary>
    private static bool SetIntegrityLevel(HANDLE token, uint integrityRid)
    {
        var sid = stackalloc byte[SidLength];
        sid[0] = 1; // Revision
        sid[1] = 1; // SubAuthorityCount
        sid[7] = 16; // IdentifierAuthority：6 字节大端，值 16 只占最后一个字节
        *(uint*)(sid + 8) = integrityRid; // SubAuthority[0]：小端

        var label = new TokenMandatoryLabel
        {
            // CsWin32 没有为这几个结构生成类型（且 POINT 那种坑也踩过），自己声明一个正好够用的即可
            Sid = (nint)sid,
            Attributes = SeGroupIntegrity
        };

        return PInvoke.SetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, &label,
            (uint)sizeof(TokenMandatoryLabel));
    }

    /// <summary>
    /// <c>TOKEN_MANDATORY_LABEL</c>：一个 <c>SID_AND_ATTRIBUTES</c>，<see cref="Sid"/> 指向
    /// <see cref="SetIntegrityLevel"/> 里那段手工拼出来的 SID。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public nint Sid;
        public uint Attributes;
    }
}

/// <summary><see cref="IntegrityDowngrade"/> 的启动结果。</summary>
internal enum IntegrityOutcome
{
    /// <summary>本来就是中完整性（绝大多数用户）：没做任何事。</summary>
    NotElevated,

    /// <summary>启动时是高完整性（管理员身份），已就地降级为中完整性。</summary>
    Lowered,

    /// <summary>是高完整性但降级失败 —— 这份进程里拖拽安装不可用，主窗口那条提示条仍是唯一出路。</summary>
    Failed
}
