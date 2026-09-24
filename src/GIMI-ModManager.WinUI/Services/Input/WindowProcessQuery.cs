using System.Diagnostics;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace GIMI_ModManager.WinUI.Services.Input;

/// <summary>
/// 窗口 / 进程的只读 win32 查询 —— 按键合成（<c>GameKeySender</c>）与提权刷新（<c>ElevatorService</c>）
/// 共用「目标游戏在不在跑 / 窗口在哪」这套定位逻辑。
/// 这里全是**读**操作：读不到就返回 null / 保守值，不抛异常、不改任何状态。
/// </summary>
internal static unsafe class WindowProcessQuery
{
    /// <summary>Mandatory Integrity Control 的「中」级别 —— JASM 的 manifest 是 asInvoker，不提权就是这个值。</summary>
    private const uint MediumIntegrityRid = 0x2000;

    /// <summary>「高」级别 —— 提权后的进程（本机游戏就是这一档）。</summary>
    private const uint HighIntegrityRid = 0x3000;

    /// <summary>映像路径缓冲。Windows 路径上限 260 上下，给 1024 个字符足够且只是栈上一点开销。</summary>
    private const int ImagePathBufferLength = 1024;

    /// <summary>窗口类名缓冲。Windows 的类名上限就是 256 个字符。</summary>
    private const int WindowClassNameBufferLength = 256;

    /// <summary>本进程的完整性级别（低 0x1000 / 中 0x2000 / 高 0x3000）。进程生命周期内不变，读一次缓存。</summary>
    private static readonly Lazy<uint> OwnIntegrityLevelRidLazy =
        new(() => TryReadIntegrityLevelRid((uint)Environment.ProcessId) ?? MediumIntegrityRid);

    internal static uint OwnIntegrityLevelRid => OwnIntegrityLevelRidLazy.Value;

    /// <summary>本进程是否已提权（High 及以上）。提权助手自己开工前要确认这一点。</summary>
    internal static bool IsOwnProcessElevated() => OwnIntegrityLevelRid >= HighIntegrityRid;

    internal static uint GetWindowProcessId(HWND window)
    {
        uint processId;
        PInvoke.GetWindowThreadProcessId(window, &processId);
        return processId;
    }

    /// <summary>HWND 的值在 CsWin32 里是裸指针，日志里要转成文本（指针不能进 Serilog 的参数数组）。</summary>
    internal static string FormatWindow(HWND window) => $"0x{(nint)window.Value:X}";

    /// <summary>
    /// 按进程名取所有进程 id（进程名**不含** <c>.exe</c>）。
    /// 进程不存在 / 查询被拒都返回空数组 —— 调用方按「没在跑」处理，不必区分。
    /// </summary>
    internal static uint[] GetProcessIds(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }

        try
        {
            return processes.Select(process => (uint)process.Id).ToArray();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    /// <summary>
    /// 找游戏窗口：枚举目标进程的**全部**顶层窗口，取「可见 + 有面积」里**面积最大**的那个
    /// （未最小化的优先）。
    ///
    /// 判据为什么不是「当前前台那个」也不是「枚举到的第一个」：游戏进世界 / 切显示模式时
    /// 常常多出别的顶层窗口，它一旦成了前台，按「前台优先」就会选中一个**不是渲染主窗口**的
    /// 窗口 —— 而渲染窗口才是 3DMigoto 挂消息钩子、游戏读键盘输入的那个。键打进别的窗口
    /// 没人接，用户只看到「刷新了但没变化」；提权助手只比进程 id，同进程的错窗口还会被判成
    /// 发送成功。（现场签名：加载界面能成、进游戏不成。）真全屏 / 无边框全屏的渲染窗口必然
    /// 面积最大，所以面积是最稳的判据。
    ///
    /// 候选全都被最小化时（游戏最小化着）退化成「面积最大的那个」：助手会先 SW_RESTORE 再送键。
    /// 不用 <c>Process.MainWindowHandle</c>：它首次访问即缓存，游戏重建窗口后就陈旧了。
    /// </summary>
    internal static HWND FindGameWindow(uint[] processIds, ILogger? logger = null)
    {
        var foreground = PInvoke.GetForegroundWindow();
        var candidates = new List<WindowCandidate>();

        PInvoke.EnumWindows((window, _) =>
        {
            // 只看目标进程的可见顶层窗口；零面积的（IME / 工具窗口）不可能是渲染窗口
            if (PInvoke.IsWindowVisible(window) == 0
                || !processIds.Contains(GetWindowProcessId(window)))
                return new BOOL(1);

            if (TryGetWindowSize(window, out var width, out var height) && width > 0 && height > 0)
                candidates.Add(new WindowCandidate(window, width, height,
                    PInvoke.IsIconic(window) != 0, window.Value == foreground.Value));

            // 返回 1 = 继续枚举：要把所有候选都收齐才能比面积
            return new BOOL(1);
        }, default);

        if (candidates.Count == 0)
        {
            logger?.Warning("[窗口] 目标进程 [{ProcessIds}] 没有可用的顶层窗口（可见 + 有面积）",
                string.Join(",", processIds));
            return HWND.Null;
        }

        var picked = candidates
            .OrderBy(candidate => candidate.IsIconic)       // 未最小化的优先：最小化窗口的尺寸不可信
            .ThenByDescending(candidate => candidate.Area)  // 面积最大的最可能是渲染主窗口
            .ThenByDescending(candidate => candidate.IsForeground)
            .First();

        if (logger is not null)
        {
            // 现场诊断：日志必须能看出「同进程有几个窗口、挑中的是哪个、枚举时前台是哪个」，
            // 否则「键打进错窗口」这件事在日志里完全不可见（助手只比进程 id，错窗口也报成功）。
            foreach (var candidate in candidates)
                logger.Information("[窗口] 候选 {Window}{Picked}", DescribeWindow(candidate.Window),
                    candidate.Window.Value == picked.Window.Value ? " ←选中" : string.Empty);

            if (candidates.Count > 1)
                logger.Warning("[窗口] 目标进程有 {Count} 个可用顶层窗口，按面积选了 {Window}；"
                               + "选中的若不是渲染主窗口，按键会被打进没人接的窗口（表现是「刷新了但没变化」）",
                    candidates.Count, DescribeWindow(picked.Window));
        }

        return picked.Window;
    }

    /// <summary>
    /// 把窗口写成一行诊断文本：hwnd + 进程 id + 尺寸 + 类名 + 前台 / 最小化标记。
    ///
    /// 只读类名（<c>GetClassName</c> 读的是内核里的类原子，不打扰目标窗口），**不读标题**：
    /// <c>GetWindowText</c> 会向目标窗口发 WM_GETTEXT，游戏正忙（加载界面正好在忙）时
    /// 可能把调用它的那个线程卡住 —— 这里是在 JASM 的同步段里调的，卡不起。
    /// </summary>
    internal static string DescribeWindow(HWND window)
    {
        if (window.IsNull)
            return "<无窗口>";

        var text = $"0x{(nint)window.Value:X} pid={GetWindowProcessId(window)}";
        if (TryGetWindowSize(window, out var width, out var height))
            text += $" {width}x{height}";

        text += $" class={ReadWindowClassName(window)}";

        if (window.Value == PInvoke.GetForegroundWindow().Value)
            text += " [前台]";

        if (PInvoke.IsIconic(window) != 0)
            text += " [最小化]";

        return text;
    }

    /// <summary>读窗口类名（游戏渲染窗口通常是 <c>UnrealWindow</c>）。读不到返回 <c>?</c>。</summary>
    private static string ReadWindowClassName(HWND window)
    {
        var buffer = stackalloc char[WindowClassNameBufferLength];
        var length = PInvoke.GetClassName(window, buffer, WindowClassNameBufferLength);

        return length <= 0 ? "?" : new string(buffer, 0, Math.Min(length, WindowClassNameBufferLength));
    }

    /// <summary>读窗口外框尺寸（物理像素）。窗口已销毁 / 查询被拒时返回 false。</summary>
    private static bool TryGetWindowSize(HWND window, out int width, out int height)
    {
        RECT rect;
        if (!PInvoke.GetWindowRect(window, &rect))
        {
            width = 0;
            height = 0;
            return false;
        }

        width = rect.right - rect.left;
        height = rect.bottom - rect.top;
        return true;
    }

    /// <summary>候选窗口（枚举阶段收集）：选主窗口只看尺寸和「是不是最小化了」。</summary>
    /// <param name="Window">窗口句柄。</param>
    /// <param name="Width">外框宽（物理像素）。</param>
    /// <param name="Height">外框高（物理像素）。</param>
    /// <param name="IsIconic">是否已最小化 —— 最小化时 <c>GetWindowRect</c> 给的是图标位置，尺寸不可信。</param>
    /// <param name="IsForeground">枚举那一刻它是不是前台窗口。</param>
    private readonly record struct WindowCandidate(HWND Window, int Width, int Height, bool IsIconic,
        bool IsForeground)
    {
        /// <summary>外框面积，用来挑主窗口。</summary>
        internal int Area => Width * Height;
    }

    /// <summary>
    /// 读某个进程的完整性级别（Mandatory Integrity Control 的最后一个 SubAuthority）。
    /// 读不到（进程已退出 / 受保护进程 / 权限不够）返回 null —— 调用方当作「不知道」。
    /// </summary>
    internal static uint? TryReadIntegrityLevelRid(uint processId)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsNull)
            return null;

        try
        {
            HANDLE token = default;
            if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, &token))
                return null;

            try
            {
                // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes } } + 变长 SID，
                // 最大也就上百字节，固定 256 字节栈缓冲足够（长度传大不会报错）。
                var buffer = stackalloc byte[256];
                uint returnLength = 0;
                if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buffer,
                        256, &returnLength))
                    return null;

                // SID 布局：Revision(1) SubAuthorityCount(1) IdentifierAuthority(6) SubAuthority[](4 * N)
                var sid = *(byte**)buffer;
                var subAuthorityCount = sid is null ? 0 : sid[1];
                if (subAuthorityCount == 0)
                    return null;

                return *(uint*)(sid + 8 + (subAuthorityCount - 1) * 4);
            }
            finally
            {
                PInvoke.CloseHandle(token);
            }
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    /// <summary>
    /// 读某个进程的映像全路径（Win32 形式，例如 <c>D:\...\Client-Win64-Shipping.exe</c>）。
    ///
    /// **两端必须用同一个 API 读**：客户端把它作为「允许注入的 exe」传给助手，
    /// 助手拿同一个 API 读前台窗口所属进程再逐字比较 —— 同源才不会因为长短路径 / 大小写 / 符号链接而误判。
    /// 读不到返回 null。
    /// </summary>
    internal static string? TryGetProcessImagePath(uint processId)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsNull)
            return null;

        try
        {
            var buffer = stackalloc char[ImagePathBufferLength];
            uint length = ImagePathBufferLength;

            // PROCESS_NAME_WIN32：要的就是 "D:\..." 这种形式（PROCESS_NAME_NATIVE 会给 \Device\HarddiskVolumeX\...）
            if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, &length))
                return null;

            return length == 0 ? null : new string(buffer, 0, (int)length);
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}