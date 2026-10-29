using System.Runtime.InteropServices;

namespace JASM.InjectorProbe;

/// <summary>
/// "建窗子进程"：被 <see cref="CbtHookInjector"/> 拉起，用来触发 CBT 事件。
///
/// 为什么不用 notepad：Win11 的记事本/画图是打包应用，`notepad.exe` 只是启动器，
/// 我们拿到的 PID 未必是真正建窗的那个进程，CBT 事件就不会落在我们跟踪的 PID 上。
/// 用自己拉起的子进程最可控，也让探针保持自包含。
/// </summary>
internal static class WindowChild
{
    private const string ClassName = "JasmInjectorProbeWindow";

    /// <summary>子命令入口：建一个可见窗口并泵消息，到时间自己退出。</summary>
    public static int Run(int seconds)
    {
        var instance = NativeMethods.GetModuleHandleW(null);

        var windowClass = new NativeMethods.WndClass
        {
            Style = 0,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate<NativeMethods.WndProc>(DefWindowProc),
            Instance = instance,
            ClassName = ClassName
        };

        var atom = NativeMethods.RegisterClassW(ref windowClass);
        if (atom == 0)
        {
            return Marshal.GetLastWin32Error();
        }

        var window = NativeMethods.CreateWindowExW(0, ClassName, "JASM Injector Probe",
            0x00CF0000 /* WS_OVERLAPPEDWINDOW */, 0, 0, 320, 200,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);

        if (window == IntPtr.Zero)
        {
            return Marshal.GetLastWin32Error();
        }

        // 显示出来（CBT 的 HCBT_CREATEWND/HCBT_ACTIVATE 才会发生）
        NativeMethods.ShowWindow(window, 1 /* SW_SHOWNORMAL */);

        var deadline = Environment.TickCount64 + seconds * 1000L;
        while (Environment.TickCount64 < deadline)
        {
            while (NativeMethods.PeekMessageW(out var message, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
            {
                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessageW(ref message);
            }

            Thread.Sleep(10);
        }

        NativeMethods.DestroyWindow(window);
        return 0;

        static IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam) =>
            NativeMethods.DefWindowProcW(hWnd, message, wParam, lParam);
    }
}