using System.Diagnostics;
using System.Runtime.InteropServices;
using GIMI_ModManager.WinUI.Services.Input;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.AppManagement;

/// <summary>
/// 「以普通权限启动一个进程」—— 也就是**去提权**。
///
/// <para>
/// 为什么要它：JASM 被提权运行之后，跨完整性级别的拖拽会被 UIPI 整个掐掉（Explorer 是中完整性、
/// 提权后的 JASM 是高），而用户往往不知道自己是怎么被提权的 —— exe 属性 → 兼容性里勾一下
/// 「以管理员身份运行此程序」，之后双击就是提权运行。有了它就能一键回到普通权限，
/// 不用让用户去翻那个勾。
/// </para>
///
/// <para>
/// 做法是**请 explorer.exe 代起这个进程**：explorer 自己是中完整性，它启动的子进程也是中完整性。
/// 这是 Windows 上「从提权的进程里起一个不提权的进程」的通用的土办法 —— 系统没有提供
/// 「去提权」的 API，`ShellExecute` 的 <c>runas</c> 只能往上走，往下走只有借 shell 的令牌这一条路；
/// 而借令牌那条路（<c>DuplicateTokenEx</c> + <c>CreateProcessWithTokenW</c>）在本项目的
/// CsWin32 里生成不出来（同批请求的其他函数都能生成，只有它不吐），所以这里走 explorer。
/// </para>
///
/// <para>
/// <b>代价是有意接受的</b>：<c>explorer.exe "路径"</c> **传不了参数**、也控制不了子进程的工作目录。
/// 参数这一件事由<b>交接标记文件</b>补上（见 <c>UnelevatedRelaunchProtocol</c>）；
/// 工作目录不需要管 —— 日志走的是绝对路径（<c>App.xaml.cs</c> 里 <c>AppContext.BaseDirectory</c> 拼的）。
/// </para>
/// </summary>
internal static class UnelevatedLauncher
{
    /// <summary>
    /// 启动结果。<paramref name="ProcessId"/> 恒为 0：经 explorer 起的那一份拿不到 pid
    /// （explorer 只负责转交，不等它、也不回报），所以调用方别指望用它做后续判定。
    /// </summary>
    internal readonly record struct LaunchResult(bool Success, string Detail);

    /// <summary>以普通权限启动 <paramref name="exePath"/>。</summary>
    internal static LaunchResult Launch(string exePath, ILogger logger)
    {
        try
        {
            // UseShellExecute = false：走 CreateProcess，不经过 shell 的动词解析 ——
            // 我们要的是**用当前（提权的）身份去起 explorer**，让 explorer 再往下起中完整性的子进程。
            // 打开 UseShellExecute 反而会让 Windows 按目标 exe 自己的清单决定提不提权，方向就反了。
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exePath}\"")
            {
                UseShellExecute = false
            });

            if (explorer is null)
            {
                logger.Error("去提权重启：explorer.exe 没能启动");
                return new LaunchResult(false, "explorer.exe 没能启动");
            }

            logger.Information("去提权重启：已经请 explorer.exe 代起 {ExePath}", exePath);
            return new LaunchResult(true, "经 explorer.exe 启动（新进程为普通权限）");
        }
        catch (Exception e)
        {
            logger.Error(e, "去提权重启：请 explorer.exe 代起失败");
            return new LaunchResult(false, e.Message);
        }
    }

    /// <summary>
    /// 用「把当前（提权）令牌的完整性级别降成中」的方式起一份新进程 —— **不借 shell**。
    ///
    /// <para>
    /// 为什么必须有这条路：<see cref="Launch"/> 那条借 explorer 令牌的土办法在 <b>UAC 关闭的机器</b>上
    /// 是无效的 —— 那种机器上 explorer 自己也是高完整性（用户账户就是内置 Administrator，
    /// 双击 exe 就是管理员），借它的令牌起出来还是高完整性。而实测证明：<b>只有「出生时就是中完整性」
    /// 的进程才收得到拖拽</b> —— 提权出生、事后把令牌就地改低是没用的（进程对象在创建那一刻就是高的，
    /// 连别的中完整性进程都杀不掉它），所以只能重新生一个。
    /// </para>
    ///
    /// <para>
    /// <c>CreateProcessWithTokenW</c> 与 <c>DuplicateTokenEx</c> 在本项目的 CsWin32 里生成不出来，
    /// 所以下面手写了 DllImport（<see cref="NativeToken"/>）。整条调用序列在真机上验证过：
    /// 提权进程 → 复制自己的令牌 → 把完整性级别设成 <c>S-1-16-8192</c>（中）→ 用它起进程，
    /// 子进程读回来确实是「中」，而且保留了管理员组（写受保护目录的 ACL 那条路照走）。
    /// </para>
    /// </summary>
    /// <param name="exePath">要启动的 exe（本进程自己的映像）。</param>
    /// <param name="arguments">命令行参数（原样转交；提权那份是被谁带参数起来的，新的那份也该带）。</param>
    internal static LaunchResult LaunchLowered(string exePath, string? arguments, ILogger logger)
    {
        try
        {
            if (!NativeToken.OpenProcessToken(NativeToken.GetCurrentProcess(), NativeToken.TokenAllAccess,
                    out var own))
                return Fail($"OpenProcessToken 失败（win32={Marshal.GetLastWin32Error()}）");

            try
            {
                if (!NativeToken.DuplicateTokenEx(own, NativeToken.TokenAllAccess, IntPtr.Zero,
                        NativeToken.SecurityImpersonation, NativeToken.TokenPrimary, out var primary))
                    return Fail($"DuplicateTokenEx 失败（win32={Marshal.GetLastWin32Error()}）");

                try
                {
                    if (!NativeToken.SetIntegrityToMedium(primary))
                        return Fail($"SetTokenInformation 失败（win32={Marshal.GetLastWin32Error()}）");

                    var workingDirectory = Path.GetDirectoryName(exePath);
                    var commandLine = string.IsNullOrWhiteSpace(arguments)
                        ? $"\"{exePath}\""
                        : $"\"{exePath}\" {arguments}";

                    var startupInfo = new NativeToken.StartupInfo { cb = Marshal.SizeOf<NativeToken.StartupInfo>() };
                    if (!NativeToken.CreateProcessWithTokenW(primary, 0, exePath, commandLine, 0, IntPtr.Zero,
                            workingDirectory, ref startupInfo, out var processInfo))
                        return Fail($"CreateProcessWithTokenW 失败（win32={Marshal.GetLastWin32Error()}）");

                    NativeToken.CloseHandle(processInfo.hThread);
                    NativeToken.CloseHandle(processInfo.hProcess);

                    logger.Information("去提权重启：已用降级令牌启动 {ExePath}（pid={ProcessId}）", exePath,
                        processInfo.dwProcessId);
                    return new LaunchResult(true, $"用降级令牌启动（pid={processInfo.dwProcessId}）");
                }
                finally
                {
                    NativeToken.CloseHandle(primary);
                }
            }
            finally
            {
                NativeToken.CloseHandle(own);
            }
        }
        catch (Exception e)
        {
            logger.Error(e, "去提权重启：用降级令牌启动失败");
            return new LaunchResult(false, e.Message);
        }

        LaunchResult Fail(string detail)
        {
            logger.Error("去提权重启：{Detail}", detail);
            return new LaunchResult(false, detail);
        }
    }

    /// <summary>
    /// 读某个进程现在的完整性级别，只用来**验**这件事（日志里说真话）：
    /// 去提权成功的话，新的一份应该是「中」而不是「高」。
    /// 读不到返回 <c>null</c>，调用方按「不知道」处理。
    /// </summary>
    internal static string DescribeIntegrity(uint processId) =>
        WindowProcessQuery.TryReadIntegrityLevelRid(processId) switch
        {
            null => "<读不到>",
            0x1000 => "低",
            0x2000 => "中（普通权限）",
            0x3000 => "高（管理员）",
            var rid => $"0x{rid:X}"
        };

    /// <summary>
    /// 手写的令牌 / 进程 P/Invoke。只为 <see cref="LaunchLowered"/> 服务，所以不进项目级的
    /// <c>NativeMethods.txt</c>（CsWin32 生成不出 <c>CreateProcessWithTokenW</c> 与
    /// <c>DuplicateTokenEx</c>，这正是当初只能借 explorer 令牌的原因）。
    /// </summary>
    private static class NativeToken
    {
        internal const uint TokenAllAccess = 0xF01FF;
        internal const int SecurityImpersonation = 2;
        internal const int TokenPrimary = 1;
        internal const int TokenIntegrityLevel = 25;

        /// <summary><c>SE_GROUP_INTEGRITY</c>：这个 SID 是强制性完整性标签，不是普通组。</summary>
        private const uint SeGroupIntegrity = 0x20;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SidAndAttributes
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenMandatoryLabel
        {
            public SidAndAttributes Label;
        }

        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
            int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(IntPtr token, int informationClass, IntPtr information,
            uint informationLength);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ConvertStringSidToSid(string stringSid, out IntPtr sid);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? applicationName,
            string? commandLine, uint creationFlags, IntPtr environment, string? currentDirectory,
            ref StartupInfo startupInfo, out ProcessInformation processInformation);

        /// <summary>
        /// 把令牌的完整性级别设成「中」（<c>S-1-16-8192</c>）。
        /// <b>降级不需要 SeTcbPrivilege</b>（升才需要，管理员没有），所以提权进程做得到这件事。
        /// </summary>
        internal static bool SetIntegrityToMedium(IntPtr token)
        {
            if (!ConvertStringSidToSid("S-1-16-8192", out var sid))
                return false;

            try
            {
                var label = new TokenMandatoryLabel
                {
                    Label = new SidAndAttributes { Sid = sid, Attributes = SeGroupIntegrity }
                };

                var size = Marshal.SizeOf<TokenMandatoryLabel>();
                var buffer = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(label, buffer, false);
                    return SetTokenInformation(token, TokenIntegrityLevel, buffer, (uint)size);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                LocalFree(sid);
            }
        }
    }
}