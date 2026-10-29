namespace JASM.InjectorProbe;

/// <summary>
/// 自建靶进程：一个跑着 <c>ping</c> 的 <c>cmd.exe</c>。
///
/// 为什么不用真实游戏：探针只回答"这条通道在本机稳不稳"，
/// 拿游戏当靶子既不需要，也会带来封号与误伤风险（PRD 决策 3）。
/// </summary>
internal sealed class TargetProcess : IDisposable
{
    private readonly IntPtr _processHandle;
    private IntPtr _threadHandle;

    public int ProcessId { get; }

    private TargetProcess(IntPtr processHandle, IntPtr threadHandle, int processId)
    {
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        ProcessId = processId;
    }

    /// <summary>
    /// 启动靶进程。
    /// <paramref name="suspended"/> = true 时用 CREATE_SUSPENDED 创建，
    /// 模拟第三方管理器"先挂起主线程 → 注入 → 放行"的流程。
    /// </summary>
    public static TargetProcess Spawn(bool suspended, TimeSpan lifetime)
    {
        // ping 的 -n 次数决定存活时长：每多 1 次约 1 秒
        var seconds = Math.Max(2, (int)lifetime.TotalSeconds);
        var commandLine = $"cmd.exe /c ping -n {seconds} 127.0.0.1 >nul";

        var startup = new NativeMethods.StartupInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.StartupInfo>()
        };

        var flags = NativeMethods.CreateNoWindow |
                    (suspended ? NativeMethods.CreateSuspended : 0u);

        if (!NativeMethods.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                flags, IntPtr.Zero, null, ref startup, out var info))
        {
            throw new InvalidOperationException(
                $"CreateProcessW 失败，Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        return new TargetProcess(info.Process, info.Thread, (int)info.ProcessId);
    }

    /// <summary>
    /// 拉起一个"会建窗口"的子进程（就是本探针自己的 <c>--window-child</c> 模式）。
    ///
    /// Hook 通道需要目标自己触发 CBT 事件，所以要有一个真正建窗的进程；
    /// 用自己控制得住的子进程，比赌 notepad 是原生程序可靠得多。
    /// </summary>
    public static TargetProcess SpawnWindowed(TimeSpan lifetime)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("取不到当前可执行文件路径");

        var startup = new NativeMethods.StartupInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.StartupInfo>()
        };

        var commandLine = $"\"{executable}\" --window-child {(int)lifetime.TotalSeconds}";

        if (!NativeMethods.CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                NativeMethods.CreateNoWindow, IntPtr.Zero, null, ref startup, out var info))
        {
            throw new InvalidOperationException(
                $"CreateProcessW 失败，Win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }

        NativeMethods.CloseHandle(info.Thread);
        return new TargetProcess(info.Process, IntPtr.Zero, (int)info.ProcessId);
    }

    /// <summary>进程名（PID 找不到时返回空串）。</summary>
    public string ProcessName
    {
        get
        {
            try
            {
                return System.Diagnostics.Process.GetProcessById(ProcessId).ProcessName;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>放行被挂起的主线程。已放行或未挂起时是空操作。</summary>
    public void Resume()
    {
        if (_threadHandle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.ResumeThread(_threadHandle);
        NativeMethods.CloseHandle(_threadHandle);
        _threadHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_threadHandle != IntPtr.Zero)
        {
            NativeMethods.ResumeThread(_threadHandle);
            NativeMethods.CloseHandle(_threadHandle);
            _threadHandle = IntPtr.Zero;
        }

        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.TerminateProcess(_processHandle, 0);
            NativeMethods.CloseHandle(_processHandle);
        }
    }
}