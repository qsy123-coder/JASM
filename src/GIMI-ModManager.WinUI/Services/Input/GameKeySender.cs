using System.Diagnostics;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Options;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace GIMI_ModManager.WinUI.Services.Input;

/// <summary>
/// 把按键合成发送给正在运行的游戏（3dmigoto / XXMI 的 mod 绑定靠它响应）。
///
/// 三个关键约束，改动前先读：
/// <list type="number">
/// <item><b>切前台必须在同步段里做</b> —— Windows 只允许「当前前台进程」调 <c>SetForegroundWindow</c>。
/// 用户点徽章时 JASM 就是前台进程；一旦 await 让出，前台身份随时可能易主，窗口就切不过去了。</item>
/// <item><b>窗口句柄每次现找</b>（<c>EnumWindows</c>），不用 <c>Process.MainWindowHandle</c> ——
/// 后者首次访问即缓存，游戏进出全屏 / 换分辨率重建窗口后拿到的句柄就陈旧了。</item>
/// <item><b>只发键盘</b>：鼠标类绑定（<c>VK_LBUTTON</c> 等）在词表里就没有虚拟键码，UI 侧直接灰掉。</item>
/// <item><b>完整性级别不够就别发</b>：游戏提权运行（XXMI 的 <c>d3dx.ini</c> 里 <c>require_admin = true</c>，
/// 鸣潮就是）而 JASM 没提权时，UIPI 不允许把输入注入进去 —— 发送会「看起来成功」但游戏收不到，
/// 用户只看到窗口切过去了、什么都没发生。所以在切前台之前先比两个进程的完整性级别，
/// 级别不够直接返回 <see cref="GameKeySendStatus.NeedsElevation"/>。</item>
/// </list>
/// </summary>
public sealed class GameKeySender : IGameKeySender
{
    /// <summary>按下到抬起之间的保持时长。固定短按，不做「点一下按住、再点一下松开」。</summary>
    private const int HoldMilliseconds = 80;

    /// <summary>Mandatory Integrity Control 的「中」级别 —— 本 app 的 manifest 是 asInvoker，不提权就是这个值。</summary>
    private const uint MediumIntegrityRid = 0x2000;

    /// <summary>本进程的完整性级别（低 0x1000 / 中 0x2000 / 高 0x3000）。进程生命周期内不变，读一次缓存。</summary>
    private static readonly Lazy<uint> OwnIntegrityLevelRid =
        new(() => TryReadIntegrityLevelRid((uint)Environment.ProcessId) ?? MediumIntegrityRid);

    /// <summary>切完前台后等焦点稳定，再发按键。</summary>
    private const int ForegroundSettleMilliseconds = 300;

    private const string D3dxIniFileName = "d3dx.ini";

    private readonly ILocalSettingsService _localSettingsService;
    private readonly ILogger _logger;

    /// <summary>连点串行化：两次注入交错会按出「修饰键还没抬起就再次按下」的怪状态。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GameKeySender(ILocalSettingsService localSettingsService, ILogger logger)
    {
        _localSettingsService = localSettingsService;
        _logger = logger.ForContext<GameKeySender>();
    }

    public async Task<GameKeySendStatus> SendKeyAsync(ushort virtualKey, IReadOnlyList<ushort> modifierKeyCodes,
        CancellationToken ct = default)
    {
        if (KeyChordGuard.IsBlockedChord(virtualKey, modifierKeyCodes))
        {
            _logger.Warning("[GameKeySender] 拒绝发送危险组合键 vk=0x{VirtualKey:X2} mods={Modifiers}",
                virtualKey, string.Join(",", modifierKeyCodes));
            return GameKeySendStatus.BlockedChord;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var options = await _localSettingsService
                .ReadOrCreateSettingAsync<ModManagerOptions>(ModManagerOptions.Section).ConfigureAwait(false);

            // ══ 同步段开始：以下到 Task.Delay 之前**不得**出现 await，否则窗口可能切不过去 ══
            var iniPath = ResolveD3dxIniPath(options);
            if (iniPath is null)
            {
                _logger.Warning("[GameKeySender] 找不到 {FileName}（GimiRootFolderPath={Root}, ModsFolderPath={Mods}）",
                    D3dxIniFileName, options.GimiRootFolderPath, options.ModsFolderPath);
                return GameKeySendStatus.TargetNotConfigured;
            }

            var processName = ReadTargetProcessName(iniPath);
            if (processName is null)
            {
                _logger.Warning("[GameKeySender] {IniPath} 里没有可用的 target", iniPath);
                return GameKeySendStatus.TargetNotConfigured;
            }

            var processIds = GetProcessIds(processName);
            if (processIds.Length == 0)
            {
                _logger.Warning("[GameKeySender] 目标进程 {ProcessName} 没有运行", processName);
                return GameKeySendStatus.GameProcessNotRunning;
            }

            var gameWindow = FindGameWindow(processIds);
            if (gameWindow.IsNull)
            {
                _logger.Warning("[GameKeySender] 目标进程 {ProcessName} 没有可见的顶层窗口", processName);
                return GameKeySendStatus.GameWindowNotFound;
            }

            var gameProcessId = GetWindowProcessId(gameWindow);
            var ownIntegrity = OwnIntegrityLevelRid.Value;
            var gameIntegrity = TryReadIntegrityLevelRid(gameProcessId);

            // UIPI 前置检查：目标进程级别更高就发不进去（而且发出去也「看起来成功」）。
            // 放在切前台**之前** —— 既然注定送不到，就不要把用户的焦点从 JASM 抢走。
            // 读不到级别（受保护进程等）时不拦，照常尝试发送。
            if (gameIntegrity is { } targetIntegrity && targetIntegrity > ownIntegrity)
            {
                _logger.Warning(
                    "[GameKeySender] 拒绝发送：目标进程 {ProcessName} 完整性级别 0x{TargetIntegrity:X4} "
                    + "高于本进程 0x{OwnIntegrity:X4}，UIPI 会把注入的输入丢掉；需要以管理员身份运行 JASM",
                    processName, targetIntegrity, ownIntegrity);
                return GameKeySendStatus.NeedsElevation;
            }

            if (PInvoke.IsIconic(gameWindow) != 0)
                PInvoke.ShowWindow(gameWindow, SHOW_WINDOW_CMD.SW_RESTORE);

            if (PInvoke.SetForegroundWindow(gameWindow) == 0)
            {
                // 切不过去不中断：按键仍然会进当时真正的前台窗口，用户可能只是没把游戏调出来
                _logger.Warning("[GameKeySender] SetForegroundWindow 被拒，仍按当前前台窗口继续发送");
            }
            // ══ 同步段结束 ══

            await Task.Delay(ForegroundSettleMilliseconds, ct).ConfigureAwait(false);

            // 真正送键那一刻的前台窗口：切前台没生效（或被别的窗口抢走）时，这行是唯一的线索
            var foregroundWindow = PInvoke.GetForegroundWindow();

            var keyCount = modifierKeyCodes.Count + 1;
            var inserted = SendChord(virtualKey, modifierKeyCodes, keyUp: false);
            try
            {
                // 抬起这一下**绝不能用 ct**：中途取消会让修饰键永远按着，游戏会一直以为 Alt 没松
                await Task.Delay(HoldMilliseconds, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (SendChord(virtualKey, modifierKeyCodes, keyUp: true) == 0)
                    _logger.Warning("[GameKeySender] 抬起按键失败 vk=0x{VirtualKey:X2}", virtualKey);
            }

            _logger.Information(
                "[GameKeySender] vk=0x{VirtualKey:X2} mods=[{Modifiers}] SendInput 插入 {Inserted}/{KeyCount} 个事件；"
                + "目标窗口=0x{TargetWindow:X} 发送时前台窗口=0x{ForegroundWindow:X}"
                + " 本进程完整性=0x{OwnIntegrity:X4} 目标完整性={GameIntegrity}",
                virtualKey, string.Join(",", modifierKeyCodes), inserted, keyCount,
                FormatWindow(gameWindow), FormatWindow(foregroundWindow), ownIntegrity,
                gameIntegrity is { } il ? $"0x{il:X4}" : "未知");

            if (inserted == keyCount)
                return GameKeySendStatus.Sent;

            _logger.Warning(
                "[GameKeySender] SendInput 只插入 {Inserted}/{KeyCount} 个事件 vk=0x{VirtualKey:X2} mods={Modifiers}；"
                + "常见原因是反作弊拦截合成输入",
                inserted, keyCount, virtualKey, string.Join(",", modifierKeyCodes));
            return GameKeySendStatus.SendInputFailed;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── 目标定位 ────────────────────────────────────────────────

    /// <summary>d3dx.ini 就在用户已配好的 GIMI 根目录正下方（与 Mods 同级）；取第一个真实存在的。</summary>
    private string? ResolveD3dxIniPath(ModManagerOptions options)
    {
        foreach (var candidate in GetD3dxIniCandidates(options))
        {
            if (File.Exists(candidate))
                return candidate;

            _logger.Debug("[GameKeySender] 候选 {FileName} 不存在: {Path}", D3dxIniFileName, candidate);
        }

        return null;
    }

    /// <summary>
    /// 候选路径：GIMI 根目录正下方，兜底 Mods 的父目录。
    /// 不用 <see cref="ModManagerOptions.XxmiRootFolderPath"/>（多数机器上是 null，本机就是）。
    /// </summary>
    private static IEnumerable<string> GetD3dxIniCandidates(ModManagerOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.GimiRootFolderPath))
            yield return Path.Combine(options.GimiRootFolderPath, D3dxIniFileName);

        var modsFolderPath = options.ModsFolderPath?.TrimEnd('\\', '/');
        var modsRootFolder = string.IsNullOrWhiteSpace(modsFolderPath)
            ? null
            : Path.GetDirectoryName(modsFolderPath);

        if (!string.IsNullOrWhiteSpace(modsRootFolder))
            yield return Path.Combine(modsRootFolder, D3dxIniFileName);
    }

    private string? ReadTargetProcessName(string iniPath)
    {
        try
        {
            return D3dxIniTargetParser.ParseTargetProcessName(File.ReadAllText(iniPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 游戏运行中 d3dx.ini 也可能被独占打开 —— 当作没配好处理
            _logger.Warning(e, "[GameKeySender] 读取 {IniPath} 失败", iniPath);
            return null;
        }
    }

    private static uint[] GetProcessIds(string processName)
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
    /// </summary>
    private static HWND FindGameWindow(uint[] processIds)
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

    private static unsafe uint GetWindowProcessId(HWND window)
    {
        uint processId;
        PInvoke.GetWindowThreadProcessId(window, &processId);
        return processId;
    }

    // ── 合成输入 ────────────────────────────────────────────────

    /// <summary>
    /// 把整个和弦一次性提交，返回 <c>SendInput</c> 真正插入的事件数（= 请求数才算成功）。
    /// 按下顺序是「先修饰键后主键」，抬起顺序**反过来** ——
    /// 否则游戏可能读到「主键还按着但修饰键已经松开」，把 Alt+↑ 认成单个 ↑。
    /// </summary>
    private static unsafe uint SendChord(ushort virtualKey, IReadOnlyList<ushort> modifierKeyCodes, bool keyUp)
    {
        var count = modifierKeyCodes.Count + 1;
        var inputs = stackalloc INPUT[count];
        var index = 0;

        if (keyUp)
        {
            inputs[index++] = CreateKeyboardInput(virtualKey, true);
            foreach (var modifier in modifierKeyCodes)
                inputs[index++] = CreateKeyboardInput(modifier, true);
        }
        else
        {
            foreach (var modifier in modifierKeyCodes)
                inputs[index++] = CreateKeyboardInput(modifier, false);

            inputs[index] = CreateKeyboardInput(virtualKey, false);
        }

        return PInvoke.SendInput((uint)count, inputs, sizeof(INPUT));
    }

    /// <summary>HWND 的值在 CsWin32 里是裸指针，日志里要转成文本（指针不能进 Serilog 的参数数组）。</summary>
    private static unsafe string FormatWindow(HWND window) => $"0x{(nint)window.Value:X}";

    // ── 完整性级别（UIPI 判断）──────────────────────────────────

    /// <summary>
    /// 读某个进程的完整性级别（Mandatory Integrity Control 的最后一个 SubAuthority）。
    /// 读不到（进程已退出 / 受保护进程 / 权限不够）返回 null —— 调用方当作「不知道」，照常尝试发送。
    /// </summary>
    private static unsafe uint? TryReadIntegrityLevelRid(uint processId)
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

    private static INPUT CreateKeyboardInput(ushort virtualKey, bool keyUp) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = new INPUT._Anonymous_e__Union
        {
            ki = new KEYBDINPUT
            {
                wVk = (VIRTUAL_KEY)virtualKey,
                wScan = 0,
                dwFlags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = 0
            }
        }
    };
}