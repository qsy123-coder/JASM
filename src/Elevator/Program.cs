using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using GIMI_ModManager.Core.Helpers;
using WindowsInput;


// Exit codes:
// 0: Success
// 1: Unhandled exception
// 2: Bad arguments

// Commands:
// -2: Alive check
// -1: Exit
// 0: RefreshActiveGenshinMods (legacy: the target is hardcoded and there is no reply)
// 1: CopyDirectory (<src> and <dst> follow on their own lines; replies "OK" or "FAIL:<msg>")
// 2: TargetedRefresh (<hwnd> follows on its own line; replies "OK" or "FAIL:<reason>")
// 3: SendKey (<vk>, <mods> and <hwnd> follow on their own lines; replies "OK" or "FAIL:<reason>")
// 4: ForegroundHandback (<hwnd> follows on its own line; replies "OK" or "FAIL:<reason>")
// 5: LaunchAndInject (<exe>, <d3d11.dll>, <mapper dll or empty>, <cwd>, <args base64> follow; replies "OK <pid>" or "FAIL:<reason>")

internal class Program
{
    public static void Main(string[] args)
    {
        var userName = "";
        try
        {
            userName = args.First();
        }
        catch
        {
            Console.Error.WriteLine("Please provide a username");
            Environment.Exit(2);
        }

        try
        {
            StartPipeServer(userName);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            Environment.Exit(1);
        }
    }


    static void StartPipeServer(string userName)
    {
        var specificUserAccount = new NTAccount(userName);
        var specificUserSid = (SecurityIdentifier)specificUserAccount.Translate(typeof(SecurityIdentifier));

        var ps = new PipeSecurity();

        var userAccessRule = new PipeAccessRule(specificUserSid,
            PipeAccessRights.FullControl, AccessControlType.Allow);
        ps.AddAccessRule(userAccessRule);

        while (true)
        {
            // 每条连接都要能失败而不带走这个提权进程：客户端超时放弃时会直接断开管道，
            // 此时读命令 / 写回执都会抛（IOException 一类），而异常冒到 Main 就是 Environment.Exit(1) ——
            // 助手一死，用户之后所有需要提权的刷新/复制都静默失效。
            try
            {
                // InOut so we can reply to commands that need a result (e.g. CopyDirectory).
                using var pipeServer = NamedPipeServerStreamConstructors.New("MyPipess", PipeDirection.InOut, 1,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous, pipeSecurity: ps);
                Console.WriteLine("Waiting for connection...");

                pipeServer.WaitForConnection();
                Console.WriteLine("Connected!");
                Console.WriteLine("----------------------");


                using var reader = new StreamReader(pipeServer);
                var command = reader.ReadLine();
                Console.WriteLine("Received command: " + command);
                Console.WriteLine("From user: " + pipeServer.GetImpersonationUserName());

                switch (command)
                {
                    case "-2":
                        break;
                    case "-1":
                        Console.WriteLine("Exiting");
                        Environment.Exit(0);
                        return;
                    case "0":
                        Console.WriteLine("Refreshing Genshin Mods");
                        RefreshGenshinMods();
                        break;
                    case "1":
                        Console.WriteLine("Copying directory");
                        HandleCopyCommand(pipeServer, reader);
                        break;
                    case "2":
                        Console.WriteLine("Refreshing the targeted game");
                        HandleTargetedRefreshCommand(pipeServer, reader);
                        break;
                    case "3":
                        Console.WriteLine("Sending a key chord");
                        HandleSendKeyCommand(pipeServer, reader);
                        break;
                    case "4":
                        Console.WriteLine("Handing the foreground back");
                        HandleForegroundHandbackCommand(pipeServer, reader);
                        break;
                    case "5":
                        Console.WriteLine("Launching the game and injecting DLLs");
                        HandleLaunchInjectCommand(pipeServer, reader);
                        break;

                    default:
                        // 旧版主程序以外的未知命令照旧只记日志：新加命令时别在这里报错给客户端，
                        // 客户端那条「无回复 = 助手版本过旧」的判断依赖「不回话」这个行为
                        Console.Error.WriteLine($"Unknown command: {command}");
                        break;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Connection failed, waiting for the next client");
                Console.Error.WriteLine(e);
            }
        }
    }

    /// <summary>
    /// Reads &lt;src&gt; and &lt;dst&gt; lines, copies the directory tree (overwriting) and replies
    /// "OK" or "FAIL:&lt;message&gt;" so the caller knows the elevated copy succeeded.
    /// </summary>
    static void HandleCopyCommand(PipeStream pipe, StreamReader reader)
    {
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        var src = reader.ReadLine();
        var dst = reader.ReadLine();

        if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
        {
            writer.WriteLine("FAIL:Missing source or destination path");
            return;
        }

        try
        {
            DirectoryCopy(src, dst, overwrite: true);
            Console.WriteLine($"Copied {src} -> {dst}");
            writer.WriteLine("OK");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            writer.WriteLine("FAIL:" + ex.Message);
        }
    }

    static void DirectoryCopy(string sourceDir, string destDir, bool overwrite)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            DirectoryCopy(subDir, Path.Combine(destDir, Path.GetFileName(subDir)), overwrite);
        }
    }

    // ── Command 5: launch suspended + inject ────────────────────────────

    const uint CREATE_SUSPENDED = 0x00000004;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const uint MEM_COMMIT = 0x00001000;
    const uint MEM_RESERVE = 0x00002000;
    const uint MEM_RELEASE = 0x00008000;
    const uint PAGE_READWRITE = 0x04;
    const uint WAIT_TIMEOUT = 0x00000102;

    /// <summary>
    /// 挂起创建游戏进程 → 依次 LoadLibraryW 注入 d3d11.dll 与可选的 Jasm.UhdMapper.dll → 恢复主线程。
    /// 任何一步失败都 TerminateProcess，绝不留下“挂起的孤儿游戏进程”。
    /// </summary>
    static void HandleLaunchInjectCommand(PipeStream pipe, StreamReader reader)
    {
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        var lines = new string?[5];
        for (var i = 0; i < lines.Length; i++)
            lines[i] = reader.ReadLine();

        if (!ElevatorLaunchInjectProtocol.TryParsePayload(lines, out var request, out var failureReason)
            || request is null)
        {
            writer.WriteLine(ElevatorLaunchInjectProtocol.BuildFailureReply(
                failureReason ?? ElevatorLaunchInjectProtocol.ReasonBadPayload));
            return;
        }

        if (!File.Exists(request.GameExecutablePath))
        {
            writer.WriteLine(ElevatorLaunchInjectProtocol.BuildFailureReply(
                ElevatorLaunchInjectProtocol.ReasonExeMissing));
            return;
        }

        if (!File.Exists(request.DllPath))
        {
            writer.WriteLine(ElevatorLaunchInjectProtocol.BuildFailureReply(
                ElevatorLaunchInjectProtocol.ReasonDllMissing));
            return;
        }

        var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        var commandLine = new StringBuilder(request.GameExecutablePath.Length + request.Arguments.Length + 8);
        commandLine.Append('"').Append(request.GameExecutablePath).Append('"');
        if (!string.IsNullOrWhiteSpace(request.Arguments))
            commandLine.Append(' ').Append(request.Arguments);

        if (!CreateProcessW(request.GameExecutablePath, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero,
                string.IsNullOrWhiteSpace(request.WorkingDirectory) ? null : request.WorkingDirectory,
                ref startupInfo, out var processInfo))
        {
            var error = Marshal.GetLastWin32Error();
            LogLine($"CreateProcessW 失败 err={error} exe={request.GameExecutablePath}");
            Console.Error.WriteLine($"CreateProcessW failed: {error}");
            writer.WriteLine(ElevatorLaunchInjectProtocol.BuildFailureReply(
                ElevatorLaunchInjectProtocol.ReasonCreateProcess));
            return;
        }

        LogLine($"CreateProcessW ok pid={processInfo.dwProcessId} args={request.Arguments} dll={request.DllPath} mapper={request.MapperDllPath ?? "(无)"}");

        try
        {
            if (!InjectDll(processInfo.hProcess, request.DllPath, out var injectReason))
            {
                TerminateProcess(processInfo.hProcess, 1);
                writer.WriteLine(ElevatorLaunchInjectProtocol.BuildFailureReply(injectReason));
                return;
            }

            if (!string.IsNullOrEmpty(request.MapperDllPath))
            {
                if (!File.Exists(request.MapperDllPath))
                {
                    TerminateProcess(processInfo.hProcess, 1);
                    writer.WriteLine(ElevatorLaunchInjectProtocol.BuildFailureReply(
                        ElevatorLaunchInjectProtocol.ReasonDllMissing));
                    return;
                }

                if (!InjectDll(processInfo.hProcess, request.MapperDllPath, out injectReason))
                {
                    TerminateProcess(processInfo.hProcess, 1);
                    writer.WriteLine(ElevatorLaunchInjectProtocol.BuildFailureReply(injectReason));
                    return;
                }

                // 映射 DLL 的工作线程装好 D3D11 钩子后会 SetEvent；等它再恢复主线程，
                // 避免游戏在钩子就绪前创建 D3D11 设备。没有事件的旧 DLL 直接放行。
                WaitForMapperReady((int)processInfo.dwProcessId);
            }

            if (ResumeThread(processInfo.hThread) == uint.MaxValue)
            {
                LogLine($"ResumeThread 失败 err={Marshal.GetLastWin32Error()}");
                TerminateProcess(processInfo.hProcess, 1);
                writer.WriteLine(ElevatorLaunchInjectProtocol.BuildFailureReply(
                    ElevatorLaunchInjectProtocol.ReasonResumeFailed));
                return;
            }

            LogLine($"ResumeThread ok pid={processInfo.dwProcessId}");
            Console.WriteLine($"Injected and resumed pid={processInfo.dwProcessId}");
            writer.WriteLine(ElevatorLaunchInjectProtocol.BuildOkReply((int)processInfo.dwProcessId));
        }
        finally
        {
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
        }
    }

    /// <summary>在目标进程中远程调用 kernel32!LoadLibraryW。成功判据是远程线程退出码非 0（HMODULE）。</summary>
    static bool InjectDll(IntPtr process, string dllPath, out string failureReason)
    {
        failureReason = ElevatorLaunchInjectProtocol.ReasonInjectFailed;

        var kernel32 = GetModuleHandleW("kernel32.dll");
        if (kernel32 == IntPtr.Zero)
        {
            LogLine($"InjectDll: GetModuleHandle(kernel32) 失败 err={Marshal.GetLastWin32Error()}");
            return false;
        }

        // 让目标进程的依赖搜索也包含 DLL 自己所在目录（WWMI 的 d3d11.dll 旁边有
        // d3dcompiler_47.dll 等配套文件，游戏目录里没有）。失败不致命，继续注入。
        var dllDirectory = Path.GetDirectoryName(dllPath);
        if (!string.IsNullOrEmpty(dllDirectory) && !SetRemoteDllDirectory(process, dllDirectory))
            LogLine($"InjectDll: 远程 SetDllDirectory('{dllDirectory}') 失败，继续");

        var loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
        if (loadLibrary == IntPtr.Zero)
        {
            LogLine($"InjectDll: GetProcAddress(LoadLibraryW) 失败 err={Marshal.GetLastWin32Error()}");
            return false;
        }

        var pathBytes = Encoding.Unicode.GetBytes(dllPath + '\0');
        var remotePath = VirtualAllocEx(process, IntPtr.Zero, (nuint)pathBytes.Length,
            MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remotePath == IntPtr.Zero)
        {
            LogLine($"InjectDll: VirtualAllocEx 失败 err={Marshal.GetLastWin32Error()}");
            return false;
        }

        try
        {
            if (!WriteProcessMemory(process, remotePath, pathBytes, (nuint)pathBytes.Length, out var written)
                || written != (nuint)pathBytes.Length)
            {
                LogLine($"InjectDll: WriteProcessMemory 失败 err={Marshal.GetLastWin32Error()} written={written}");
                return false;
            }

            var thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remotePath, 0, out _);
            if (thread == IntPtr.Zero)
            {
                LogLine($"InjectDll: CreateRemoteThread 失败 err={Marshal.GetLastWin32Error()}");
                return false;
            }

            try
            {
                var wait = WaitForSingleObject(thread, 10_000);
                if (wait == WAIT_TIMEOUT)
                {
                    failureReason = ElevatorLaunchInjectProtocol.ReasonInjectTimeout;
                    LogLine($"InjectDll: 等待远程线程超时 path={dllPath}");
                    return false;
                }

                if (wait != 0)
                {
                    LogLine($"InjectDll: WaitForSingleObject 返回 {wait}");
                    return false;
                }

                if (!GetExitCodeThread(thread, out var exitCode))
                {
                    LogLine($"InjectDll: GetExitCodeThread 失败 err={Marshal.GetLastWin32Error()}");
                    return false;
                }

                if (exitCode == 0)
                {
                    // 远程 LoadLibrary 返回 NULL：目标进程里加载失败（依赖缺失 / 被拦截 / 代理 DLL 拒绝）。
                    LogLine($"InjectDll: 远程 LoadLibraryW 返回 NULL path={dllPath}");
                    return false;
                }

                LogLine($"InjectDll: 已加载 {dllPath} hModule=0x{exitCode:X}");
                failureReason = string.Empty;
                return true;
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
        }
    }

    /// <summary>
    /// 等映射 DLL 的就绪事件。DLL 由它自己的工作线程创建，LoadLibrary 返回时事件可能还没建好，
    /// 所以先在 5 秒内重试 OpenEvent，再等 SetEvent。拿不到事件（旧版 DLL）也继续恢复线程。
    /// </summary>
    static void WaitForMapperReady(int processId)
    {
        const uint SYNCHRONIZE = 0x00100000;
        var name = ElevatorLaunchInjectProtocol.BuildReadyEventName(processId);
        var deadline = Environment.TickCount64 + 5000;
        var ready = IntPtr.Zero;

        while (Environment.TickCount64 < deadline)
        {
            ready = OpenEventW(SYNCHRONIZE, false, name);
            if (ready != IntPtr.Zero)
                break;
            Thread.Sleep(50);
        }

        if (ready == IntPtr.Zero)
        {
            LogLine("映射 DLL 就绪事件未出现，直接恢复主线程");
            return;
        }

        try
        {
            var remaining = (uint)Math.Max(0, deadline - Environment.TickCount64);
            var wait = WaitForSingleObject(ready, remaining);
            LogLine(wait == 0 ? "映射 DLL 已就绪" : "映射 DLL 就绪超时，继续恢复主线程");
        }
        finally
        {
            CloseHandle(ready);
        }
    }

    /// <summary>远程调用 kernel32!SetDllDirectoryW，把目录加进目标进程的依赖搜索路径。</summary>
    static bool SetRemoteDllDirectory(IntPtr process, string directory)
    {
        var kernel32 = GetModuleHandleW("kernel32.dll");
        if (kernel32 == IntPtr.Zero)
            return false;

        var setDllDirectory = GetProcAddress(kernel32, "SetDllDirectoryW");
        if (setDllDirectory == IntPtr.Zero)
            return false;

        var bytes = Encoding.Unicode.GetBytes(directory + '\0');
        var remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)bytes.Length,
            MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (remote == IntPtr.Zero)
            return false;

        try
        {
            if (!WriteProcessMemory(process, remote, bytes, (nuint)bytes.Length, out var written)
                || written != (nuint)bytes.Length)
                return false;

            var thread = CreateRemoteThread(process, IntPtr.Zero, 0, setDllDirectory, remote, 0, out _);
            if (thread == IntPtr.Zero)
                return false;

            try
            {
                if (WaitForSingleObject(thread, 10_000) != 0)
                    return false;

                return GetExitCodeThread(thread, out var exitCode) && exitCode != 0;
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        }
    }

    /// <summary>
    /// 注入相关的诊断日志。只写文件：助手由主程序以 runas 拉起，stdout 通常不可见。
    /// 任何写失败都吞掉 —— 诊断不能变成新的故障源。
    /// </summary>
    static void LogLine(string message)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JASM");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "Elevator.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint flAllocationType,
        uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, nuint nSize,
        out nuint lpNumberOfBytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, nuint dwStackSize,
        IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, out uint lpThreadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetExitCodeThread(IntPtr hThread, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    // 必须显式 CharSet.Unicode：默认 ANSI 编组会把宽字符 API 收到的模块名读错，
    // GetModuleHandleW 返回 NULL + ERROR_MOD_NOT_FOUND(126)，注入直接失败。
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenEventW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("User32.dll")]
    static extern int SetForegroundWindow(IntPtr point);

    [DllImport("User32.dll")]
    static extern IntPtr GetForegroundWindow();

    /// <summary>零位移鼠标微动用（见 <see cref="NudgeForegroundLock"/>）。</summary>
    [DllImport("user32.dll")]
    static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    const uint MOUSEEVENTF_MOVE = 0x0001;

    [DllImport("User32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("User32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    [DllImport("User32.dll")]
    static extern bool IsIconic(IntPtr hWnd);

    [DllImport("User32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>ShowWindow 的参数：还原最小化的窗口（不激活它）。</summary>
    const int SW_RESTORE = 9;

    /// <summary>前台校验的轮询：10 次 × 50ms，够游戏把窗口切上来。</summary>
    const int ForegroundCheckAttempts = 10;
    const int ForegroundCheckIntervalMs = 50;

    /// <summary>送键时按住 / 抬起之间的保持时长，与主程序 <c>GameKeySender.HoldMilliseconds</c> 取同一个值。</summary>
    const int KeyHoldMilliseconds = 80;

    /// <summary>
    /// 抢到前台后再等这一小会儿才送键，与主程序 <c>GameKeySender.ForegroundSettleMilliseconds</c> 同值 ——
    /// 前台刚切过去时游戏还在处理激活消息，立刻送键会被丢掉，现象就是「点了没反应」。
    /// 刷新路径不需要这一步（F10 是重载，早一点晚一点都吃得下），送键必须等。
    /// </summary>
    const int ForegroundSettleMilliseconds = 300;


    /// <summary>
    /// 历史命令 "0" 的实现：目标写死为原神，窗口也照旧用 <see cref="Process.MainWindowHandle"/> 找。
    /// 保留原语义只为兼容旧版主程序/旧命令，新路径请走 <see cref="HandleTargetedRefreshCommand"/>。
    /// </summary>
    static void RefreshGenshinMods()
    {
        var ptr = GetGenshinProcess();

        if (ptr == null) return;


        _ = SetForegroundWindow(ptr.Value);

        SendF10();
    }

    /// <summary>
    /// 带目标的刷新：载荷是目标窗口句柄（十进制一行）。
    ///
    /// 「哪个游戏、d3dx.ini 在哪」由主程序解析（只有它读得到 JASM 设置），窗口句柄也必须由它现找 ——
    /// <see cref="Process.MainWindowHandle"/> 首次访问即缓存，游戏进出全屏 / 换分辨率重建窗口后就陈旧了。
    /// 助手只做提权才能做的那一段：还原窗口 → 切前台 → 回读校验 → 发 F10，并把结果回执给主程序。
    /// 前台没抢到就**不发 F10**（那时按键会打进别的窗口，比如 JASM 自己），回执让主程序去报错。
    /// </summary>
    static void HandleTargetedRefreshCommand(PipeStream pipe, StreamReader reader)
    {
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        var payload = reader.ReadLine();

        if (!long.TryParse(payload?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawHandle)
            || !IsWindow(new IntPtr(rawHandle)))
        {
            Console.Error.WriteLine($"Bad target window handle: {payload}");
            writer.WriteLine("FAIL:bad-payload");
            return;
        }

        var targetWindow = new IntPtr(rawHandle);

        if (IsIconic(targetWindow))
        {
            // 最小化的窗口也能被 SetForegroundWindow 选中，但仍然是「最小化」状态，F10 打不进窗口
            Console.WriteLine("Restoring the minimized target window");
            ShowWindow(targetWindow, SW_RESTORE);
        }

        // 返回值不可信（前台锁会让它返回 0，而窗口其实已经切过去了），所以下面以回读为准。
        // 与 0 比较而不是取反：DllImport 声明的是 int（BOOL 原样传回），不动历史路径的声明
        if (SetForegroundWindow(targetWindow) == 0)
        {
            Console.WriteLine("SetForegroundWindow returned false; verifying by reading the window back");
        }

        if (!WaitForForeground(targetWindow))
        {
            Console.Error.WriteLine("The target window never became the foreground window, not sending F10");
            writer.WriteLine("FAIL:not-foreground");
            return;
        }

        Console.WriteLine("The target window is in the foreground, sending F10");
        SendF10();

        // 回执放在按键之后：主程序读到 "OK" 就等于 F10 真的发出去了
        writer.WriteLine("OK");
    }

    /// <summary>
    /// 送键：载荷三行（vk / mods / hwnd）由 <see cref="KeyHelperProtocol"/> 负责解析与护栏。
    ///
    /// **为什么这件事非助手不可**：游戏是提权运行的，UIPI 会把「中 → 高」的 <c>SendInput</c> 静默丢弃 ——
    /// 主程序发出去看起来成功、游戏却收不到。助手是提权进程，它发的才进得去。
    /// 助手只做提权才能做的那一段：还原窗口 → 切前台 → 回读校验 → 送键；
    /// 「哪个游戏、窗口句柄是多少」由主程序现找（只有它读得到 d3dx.ini，也只有它会用 EnumWindows）。
    /// </summary>
    static void HandleSendKeyCommand(PipeStream pipe, StreamReader reader)
    {
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        // 三行一次性读出来再交给协议解析：这样失败出口只有下面那几个，不会中途漏掉回执
        var virtualKeyLine = reader.ReadLine();
        var modifiersLine = reader.ReadLine();
        var windowLine = reader.ReadLine();

        if (!KeyHelperProtocol.TryParseSendKeyRequest(virtualKeyLine, modifiersLine, windowLine,
                out var request, out var failureReason))
        {
            // 载荷不合法、危险组合键都从这里出来。护栏在这里是**重复**过了一遍的：
            // 主程序发之前已经拦过，规则同一份（KeyChordGuard），等于白送一道防线。
            Console.Error.WriteLine($"Rejected the key chord: {failureReason}");
            writer.WriteLine(KeyHelperProtocol.BuildFailureReply(
                failureReason ?? KeyHelperProtocol.ReasonBadPayload));
            return;
        }

        IntPtr targetWindow = request.TargetWindow;

        // 句柄是主程序刚现找的，但从它写进管道到助手读到，中间隔着一次进程切换，
        // 游戏可能已经重建了窗口（进出全屏 / 改分辨率）。陈旧句柄必须在这里挡掉 ——
        // 放下去的话 SetForegroundWindow 会失败得毫无线索。
        if (!IsWindow(targetWindow))
        {
            Console.Error.WriteLine($"Bad target window handle: {request.TargetWindow}");
            writer.WriteLine(KeyHelperProtocol.BuildFailureReply(KeyHelperProtocol.ReasonBadPayload));
            return;
        }

        if (IsIconic(targetWindow))
        {
            Console.WriteLine("Restoring the minimized target window");
            ShowWindow(targetWindow, SW_RESTORE);
        }

        // 返回值不可信（前台锁会让它返回 0，而窗口其实已经切过去了），所以下面以回读为准
        if (SetForegroundWindow(targetWindow) == 0)
        {
            Console.WriteLine("SetForegroundWindow returned false; verifying by reading the window back");
        }

        if (!WaitForForeground(targetWindow))
        {
            // 与刷新同一条理由：前台没抢到，按键会打进别的窗口（比如 JASM 自己），拒发
            Console.Error.WriteLine("The target window never became the foreground window, not sending the key");
            writer.WriteLine(KeyHelperProtocol.BuildFailureReply(KeyHelperProtocol.ReasonNotForeground));
            return;
        }

        Thread.Sleep(ForegroundSettleMilliseconds);

        Console.WriteLine($"The target window is in the foreground, sending vk=0x{request.VirtualKey:X2}");
        SendChord(request.VirtualKey, request.ModifierKeyCodes);

        // 回执放在按键之后：主程序读到 "OK" 就等于按键真的发出去了
        writer.WriteLine(KeyHelperProtocol.BuildOkReply());
    }

    /// <summary>
    /// 归还前台：载荷一行（hwnd 十六进制）——把前台交给这个窗口，**不发任何键**。
    ///
    /// **为什么这件事只有助手做得到**：前台锁只认「自己就是前台进程 / 最近收到输入的那个进程」，
    /// 而**注入的输入算在注入者头上**。送键（<see cref="HandleSendKeyCommand"/>）之后前台留在游戏手里，
    /// 而那个游戏往往以管理员身份运行：主程序（「中」完整性）既抢不回前台，也没法用「先注入一次输入
    /// 把身份拿回来」那一招 —— 那个注入会被 UIPI **静默丢弃**。助手刚刚注入过按键，
    /// 此刻正是「最近收到输入的那个进程」，所以它这一句 SetForegroundWindow 立刻生效
    /// （与主程序注释里记的「助手刚被 UAC 拉起那一次抢得动」是同一条规则）。
    ///
    /// 与前两条命令一样**以回读为准**：返回值不可信（前台锁会让它返回 0，而窗口其实已经切过去了）。
    /// 交还失败不是错误、也不影响已经送出去的按键 —— 主程序那边只把它记进日志。
    /// </summary>
    static void HandleForegroundHandbackCommand(PipeStream pipe, StreamReader reader)
    {
        using var writer = new StreamWriter(pipe) { AutoFlush = true };

        var windowLine = reader.ReadLine();

        // 句柄那一行与送键命令共用同一个解析（本工程把 KeyHelperProtocol.cs 直接编了进来），
        // 格式只有一份，两边不会各写各的
        if (!KeyHelperProtocol.TryParseWindowLine(windowLine, out var targetWindow)
            || !IsWindow(targetWindow))
        {
            Console.Error.WriteLine($"Bad foreground handback window handle: {windowLine}");
            writer.WriteLine(KeyHelperProtocol.BuildFailureReply(KeyHelperProtocol.ReasonBadPayload));
            return;
        }

        Console.WriteLine($"Handing the foreground back to 0x{targetWindow:X}");

        if (SetForegroundWindow(targetWindow) == 0)
        {
            Console.WriteLine("SetForegroundWindow returned false; verifying by reading the window back");
        }

        if (!WaitForForeground(targetWindow))
        {
            Console.Error.WriteLine("The window never became the foreground window");
            writer.WriteLine(KeyHelperProtocol.BuildFailureReply(KeyHelperProtocol.ReasonForegroundNotTaken));
            return;
        }

        writer.WriteLine(KeyHelperProtocol.BuildOkReply());
    }

    /// <summary>
    /// 轮询等目标窗口所属的进程拿到前台（最多 ~500ms）。
    /// 按**进程 id** 比而不是按窗口句柄比：等待期间游戏可能重建窗口，句柄会变，进程不会。
    /// </summary>
    static bool WaitForForeground(IntPtr targetWindow)
    {
        GetWindowThreadProcessId(targetWindow, out var targetProcessId);
        if (targetProcessId == 0)
        {
            // 窗口在发命令与这次读取之间被销毁了
            return false;
        }

        for (var attempt = 0; attempt < ForegroundCheckAttempts; attempt++)
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow != IntPtr.Zero)
            {
                GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
                if (foregroundProcessId == targetProcessId)
                {
                    return true;
                }
            }

            // 前台锁只认「最近收到输入的那个进程」。**光是等是等不到的** —— 那一轮轮询只会白等：
            // 注入一次零位移的鼠标微动，本进程就成了「最近收到输入」的那个，前台切换权随之放行，
            // 下面这句 SetForegroundWindow 才可能真的生效。
            // （症状就是「时不时刷新没反应」：助手抢不到前台 → 按护栏拒发，游戏里毫无变化。）
            NudgeForegroundLock();
            _ = SetForegroundWindow(targetWindow);

            Thread.Sleep(ForegroundCheckIntervalMs);
        }

        return false;
    }

    /// <summary>
    /// 零位移的鼠标微动：不为移动光标，只为把「最近收到输入」这个身份拿到自己手里
    /// （Windows 前台锁只对那个进程放行）。位移恒为 0，游戏不会看到光标移动；也**不碰 Alt** ——
    /// 那一下会切出游戏菜单。与主程序 <c>ForegroundWindowActivator</c> 的解锁手法同源。
    /// </summary>
    static void NudgeForegroundLock()
    {
        mouse_event(MOUSEEVENTF_MOVE, 0, 0, 0, UIntPtr.Zero);
    }

    /// <summary>按下 F10（3DMigoto 的重载键）。新老两条路径共用这一份，发键机制本身没变。</summary>
    static void SendF10()
    {
        new InputSimulator().Keyboard
            .KeyDown(VirtualKeyCode.F10)
            .Sleep(100)
            .KeyUp(VirtualKeyCode.F10)
            .Sleep(100);
    }

    /// <summary>
    /// 送一个「修饰键 + 主键」的和弦。
    ///
    /// 顺序与主程序 <c>GameKeySender.SendChord</c> 逐字一致：按下时**先修饰键后主键**，
    /// 抬起时**反过来** —— 否则游戏可能读到「主键还按着、修饰键已经松开」，
    /// 把 Alt+↑ 认成单个 ↑。这里没有复用那份实现，是因为它在 WinUI 工程里、
    /// 且依赖 CsWin32 生成的 INPUT 结构；助手这边沿用自己的 WindowsInput（SendF10 用的同一套）。
    /// </summary>
    static void SendChord(ushort virtualKey, IReadOnlyList<ushort> modifierKeyCodes)
    {
        var keyboard = new InputSimulator().Keyboard;

        foreach (var modifier in modifierKeyCodes)
        {
            keyboard.KeyDown((VirtualKeyCode)modifier);
        }

        keyboard.KeyDown((VirtualKeyCode)virtualKey);
        Thread.Sleep(KeyHoldMilliseconds);
        keyboard.KeyUp((VirtualKeyCode)virtualKey);

        // 抬起顺序反过来，理由见上面的注释
        for (var i = modifierKeyCodes.Count - 1; i >= 0; i--)
        {
            keyboard.KeyUp((VirtualKeyCode)modifierKeyCodes[i]);
        }
    }


    static IntPtr? GetGenshinProcess()
    {
        var processes = Process.GetProcessesByName("GenshinImpact");

        foreach (var process in processes)
        {
            Console.WriteLine("Title: " + process.MainWindowTitle);
        }

        if (processes.Length > 1)
        {
            Console.Error.WriteLine("Multiple GenshinImpact.exe processes found");
            return null;
        }

        var ptr = processes.FirstOrDefault()?.MainWindowHandle;
        if (ptr == IntPtr.Zero)
        {
            Console.Error.WriteLine("GenshinImpact.exe process not found");
            return null;
        }

        return ptr;
    }
}

/*[DllImport("user32.dll")]
static extern bool PostMessage(IntPtr hWnd, UInt32 Msg, int wParam, int lParam);

const UInt32 WM_KEYDOWN = 0x0100;
const int VK_F10 = 0x79;

async Task RefreshGenshinMods()
{
    var ptr = GetGenshinProcess().MainWindowHandle;


    SetForegroundWindow(ptr);
    await Task.Delay(100);

    var success = PostMessage(ptr, WM_KEYDOWN, VK_F10, 0);

    Console.WriteLine(!success ? "Failed to send message" : "Sent message");
}*/

/*async Task RefreshGenshinModsWinInput()
{
    var ptr = GetGenshinProcess().MainWindowHandle;

    SetForegroundWindow(ptr);
    await Task.Delay(1000);

    await WindowsInput.Simulate.Events()
        .Click(KeyCode.F10)
        .Invoke();
}*/