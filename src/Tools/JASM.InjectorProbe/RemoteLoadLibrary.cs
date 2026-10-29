using System.Runtime.InteropServices;

namespace JASM.InjectorProbe;

/// <summary>
/// 直接注入通道：OpenProcess → VirtualAllocEx → WriteProcessMemory →
/// CreateRemoteThread(LoadLibraryW) → 等线程 → 核对模块 → 释放。
///
/// 关键点（照抄第三方管理器 Rocket 的稳健做法）：
/// **不要**把本进程的 LoadLibraryW 绝对地址直接投到目标进程。正确做法是：
///     远程起点 = 目标进程 kernel32 基址 + (本进程 LoadLibraryW - 本进程 kernel32 基址)
///
/// 本机实测：同一次开机内 kernel32 在注入器与目标里是**同一个基址**（共享镜像，
/// ASLR 按开机而非按进程），所以直接用绝对地址也能work —— 但那只是当前环境的巧合。
/// 用相对偏移不依赖这个巧合，是更稳的写法。
/// </summary>
internal static class RemoteLoadLibrary
{
    public sealed record Result(InjectionOutcome Outcome, int Win32Error, double ElapsedMs);

    /// <summary>等待远程线程结束的上限。超时说明目标卡住，继续等没有意义。</summary>
    private static readonly TimeSpan ThreadTimeout = TimeSpan.FromSeconds(15);

    /// <summary>可选的逐步诊断输出（--verbose 时接到 Console.WriteLine）。</summary>
    public static Action<string>? Trace { get; set; }

    private static void Step(string message) => Trace?.Invoke("    · " + message);

    public static Result Inject(int processId, string dllPath)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        if (processId == Environment.ProcessId)
        {
            return new Result(InjectionOutcome.RefusedSelf, 0, watch.Elapsed.TotalMilliseconds);
        }

        if (!File.Exists(dllPath))
        {
            return new Result(InjectionOutcome.DllPathMissing, 0, watch.Elapsed.TotalMilliseconds);
        }

        var modulesBefore = ModuleProbe.Snapshot(processId);
        if (modulesBefore.Count == 0)
        {
            return new Result(InjectionOutcome.TargetNotFound, 0, watch.Elapsed.TotalMilliseconds);
        }

        // 目标里的 kernel32 基址：拿不到就没法算远程起点
        var targetKernel32 = modulesBefore
            .FirstOrDefault(m => string.Equals(m.Name, "kernel32.dll", StringComparison.OrdinalIgnoreCase))
            ?.BaseAddress ?? IntPtr.Zero;

        if (targetKernel32 == IntPtr.Zero)
        {
            return new Result(InjectionOutcome.ArchitectureMismatch, 0, watch.Elapsed.TotalMilliseconds);
        }

        var localKernel32 = NativeMethods.GetModuleHandleW("kernel32.dll");
        var localError = Marshal.GetLastWin32Error();
        var localLoadLibrary = NativeMethods.GetProcAddress(localKernel32, "LoadLibraryW");
        Step($"kernel32(本进程)=0x{localKernel32:X} err={localError} " +
             $"LoadLibraryW=0x{localLoadLibrary:X} kernel32(目标)=0x{targetKernel32:X}");
        if (localKernel32 == IntPtr.Zero || localLoadLibrary == IntPtr.Zero)
        {
            return new Result(InjectionOutcome.RemoteThreadFailed, localError,
                watch.Elapsed.TotalMilliseconds);
        }

        var remoteStart = targetKernel32 +
                          (long)localLoadLibrary - (long)localKernel32;
        Step($"远程起点=0x{remoteStart:X}（偏移 0x{(long)localLoadLibrary - (long)localKernel32:X}）");

        var process = NativeMethods.OpenProcess(NativeMethods.InjectionAccess, false, processId);
        if (process == IntPtr.Zero)
        {
            var openError = Marshal.GetLastWin32Error();
            Step($"OpenProcess 失败 err={openError}");
            return new Result(MapOpenProcessError(openError), openError,
                watch.Elapsed.TotalMilliseconds);
        }

        Step($"OpenProcess ok handle=0x{process:X}");

        try
        {
            // 路径按 UTF-16 写入，末尾补 '\0'
            var pathBytes = System.Text.Encoding.Unicode.GetBytes(dllPath + "\0");
            var remoteMemory = NativeMethods.VirtualAllocEx(process, IntPtr.Zero, pathBytes.Length,
                NativeMethods.MemCommit | NativeMethods.MemReserve, NativeMethods.PageReadWrite);

            if (remoteMemory == IntPtr.Zero)
            {
                return new Result(InjectionOutcome.RemoteThreadFailed, Marshal.GetLastWin32Error(),
                    watch.Elapsed.TotalMilliseconds);
            }

            try
            {
                if (!NativeMethods.WriteProcessMemory(process, remoteMemory, pathBytes, pathBytes.Length, out var written)
                    || written != pathBytes.Length)
                {
                    return new Result(InjectionOutcome.RemoteThreadFailed, Marshal.GetLastWin32Error(),
                        watch.Elapsed.TotalMilliseconds);
                }

                var thread = NativeMethods.CreateRemoteThread(process, IntPtr.Zero, 0,
                    (IntPtr)remoteStart, remoteMemory, 0, out _);

                if (thread == IntPtr.Zero)
                {
                    var error = Marshal.GetLastWin32Error();
                    return new Result(error == 5 ? InjectionOutcome.AccessDenied : InjectionOutcome.RemoteThreadFailed,
                        error, watch.Elapsed.TotalMilliseconds);
                }

                try
                {
                    var wait = NativeMethods.WaitForSingleObject(thread,
                        (uint)ThreadTimeout.TotalMilliseconds);

                    if (wait == NativeMethods.WaitTimeout)
                    {
                        return new Result(InjectionOutcome.Timeout, 0, watch.Elapsed.TotalMilliseconds);
                    }

                    if (!NativeMethods.GetExitCodeThread(thread, out var exitCode))
                    {
                        return new Result(InjectionOutcome.RemoteThreadFailed, Marshal.GetLastWin32Error(),
                            watch.Elapsed.TotalMilliseconds);
                    }

                    // LoadLibraryW 的返回值就是模块句柄：0 表示加载失败
                    if (exitCode == 0 || exitCode == NativeMethods.StillActive)
                    {
                        return new Result(InjectionOutcome.DllLoadFailed, 0, watch.Elapsed.TotalMilliseconds);
                    }

                    // 结果核对：模块真的出现在目标里才算数
                    var moduleName = Path.GetFileName(dllPath);
                    if (!WaitForModule(processId, moduleName, TimeSpan.FromSeconds(2)))
                    {
                        // DLL 已加载过时（LoadLibrary 返回非零但模块本来就在）属于正常；
                        // 这里只拦"返回非零但模块也不在"的异常情况。
                        return ModuleProbe.Contains(processId, moduleName)
                            ? new Result(InjectionOutcome.Success, 0, watch.Elapsed.TotalMilliseconds)
                            : new Result(InjectionOutcome.ModuleNotLoaded, 0, watch.Elapsed.TotalMilliseconds);
                    }

                    return new Result(InjectionOutcome.Success, 0, watch.Elapsed.TotalMilliseconds);
                }
                finally
                {
                    NativeMethods.CloseHandle(thread);
                }
            }
            finally
            {
                NativeMethods.VirtualFreeEx(process, remoteMemory, 0, NativeMethods.MemRelease);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>轮询等模块出现（目标加载 DLL 的 DllMain 可能有几百毫秒延迟）。</summary>
    private static bool WaitForModule(int processId, string moduleName, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (ModuleProbe.Contains(processId, moduleName))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    /// <summary>OpenProcess 的错误码归因：2/87 是"没有这个进程"，5 是权限不足。</summary>
    private static InjectionOutcome MapOpenProcessError(int error) => error switch
    {
        2 or 87 => InjectionOutcome.TargetNotFound,
        5 => InjectionOutcome.AccessDenied,
        _ => InjectionOutcome.RemoteThreadFailed
    };
}