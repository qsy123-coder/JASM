using System.Runtime.InteropServices;

namespace JASM.InjectorProbe;

/// <summary>
/// Hook 注入通道（Phase A2）：全局 <c>WH_CBT</c> 钩子。
///
/// 原理：把钩子过程放进一个 DLL，用 <c>SetWindowsHookExW(WH_CBT, proc, hMod, 0)</c> 装成全局钩子。
/// 之后任何进程触发 CBT 事件（建窗、激活…）时，Windows 会把该 DLL 映射进那个进程 —— 不写内存、
/// 不改线程，是最"温和"的一条注入通道。
///
/// 与直接注入的差别：
/// - **不能指定"现在就投"**：要等目标自己触发 CBT 事件（所以探针会自己拉起一个建窗的靶进程）；
/// - DLL **必须导出 `CBTProc`**，且必须是真正的镜像（不能用 LOAD_LIBRARY_AS_DATAFILE 加载）。
///
/// 本机没有 C/C++ 编译器，`cbtprobe.dll` 由 CI 构建（见 .github/workflows/injector-probe-native.yml）。
/// </summary>
internal static class CbtHookInjector
{
    private const int WhCbt = 5;

    /// <summary>装钩子并把 DLL 灌进"接下来会建窗口"的进程，等模块出现后卸钩子。</summary>
    public static (InjectionOutcome Outcome, string Detail) Run(string dllPath, TimeSpan timeout)
    {
        if (!File.Exists(dllPath))
        {
            return (InjectionOutcome.DllPathMissing, $"找不到 DLL：{dllPath}");
        }

        // 1) 完整加载到本进程：钩子过程必须来自一个已映射的镜像
        var module = NativeMethods.LoadLibraryW(dllPath);
        if (module == IntPtr.Zero)
        {
            return (InjectionOutcome.DllLoadFailed,
                $"LoadLibraryW 失败（Win32 {Marshal.GetLastWin32Error()}）");
        }

        var hook = IntPtr.Zero;
        try
        {
            // 2) 取 CBTProc
            var proc = NativeMethods.GetProcAddress(module, "CBTProc");
            if (proc == IntPtr.Zero)
            {
                return (InjectionOutcome.HookDllMissingCbtProc,
                    "DLL 没有导出 CBTProc —— 不能用于 Hook 通道");
            }

            // 3) 全局钩子（线程 0 = 全系统）
            hook = NativeMethods.SetWindowsHookExW(WhCbt, proc, module, 0);
            if (hook == IntPtr.Zero)
            {
                return (InjectionOutcome.HookInstallFailed,
                    $"SetWindowsHookExW 失败（Win32 {Marshal.GetLastWin32Error()}）");
            }

            // 4) 拉起一个会建窗口的靶进程：CBT 事件只在进程真正建窗时触发
            using var target = TargetProcess.SpawnWindowed(TimeSpan.FromSeconds(20));
            var moduleName = Path.GetFileName(dllPath);

            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (ModuleProbe.Contains(target.ProcessId, moduleName))
                {
                    return (InjectionOutcome.Success,
                        $"已进入 {target.ProcessName}（{target.ProcessId}）");
                }

                Thread.Sleep(100);
            }

            return (InjectionOutcome.HookTimeout,
                $"超时：{timeout.TotalSeconds:F0}s 内 {moduleName} 没有出现在目标模块表里");
        }
        finally
        {
            if (hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(hook);
            }

            // 钩子卸掉后才能安全卸载；失败也不致命
            NativeMethods.FreeLibrary(module);
        }
    }
}