using System.Diagnostics;
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
    /// 找游戏窗口：先看「已经是前台的窗口」（避免误选 overlay / 启动器残留窗口），
    /// 否则枚举顶层窗口，取第一个「可见 + 属于目标进程」的。
    /// 不用 <c>Process.MainWindowHandle</c>：它首次访问即缓存，游戏重建窗口后就陈旧了。
    /// </summary>
    internal static HWND FindGameWindow(uint[] processIds)
    {
        var foreground = PInvoke.GetForegroundWindow();
        if (!foreground.IsNull && processIds.Contains(GetWindowProcessId(foreground)))
            return foreground;

        HWND found = HWND.Null;
        PInvoke.EnumWindows((window, _) =>
        {
            if (found.IsNull && PInvoke.IsWindowVisible(window) != 0
                             && processIds.Contains(GetWindowProcessId(window)))
                found = window;

            // 返回 0 = 停止枚举：已经找到就没必要继续
            return new BOOL(found.IsNull ? 1 : 0);
        }, default);

        return found;
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