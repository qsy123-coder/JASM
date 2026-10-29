using System.Runtime.InteropServices;

namespace JASM.InjectorProbe;

/// <summary>
/// 探针用到的 Win32 入口。
///
/// 只声明本探针真正调用到的函数；新加函数时同步补这里，
/// 与主程序 NativeMethods.txt 的做法不同（本工具本来就是手写 P/Invoke）。
/// </summary>
internal static class NativeMethods
{
    // ── OpenProcess 访问位 ────────────────────────────────────────────────
    public const uint ProcessCreateThread = 0x0002;
    public const uint ProcessVmOperation = 0x0008;
    public const uint ProcessVmWrite = 0x0020;
    public const uint ProcessVmRead = 0x0010;
    public const uint ProcessQueryInformation = 0x0400;
    public const uint Synchronize = 0x00100000;

    /// <summary>注入所需的最小集合：建线程 + 读写目标内存 + 查信息 + 等待线程。</summary>
    public const uint InjectionAccess =
        ProcessCreateThread | ProcessVmOperation | ProcessVmWrite | ProcessVmRead |
        ProcessQueryInformation | Synchronize;

    // ── VirtualAllocEx ────────────────────────────────────────────────────
    public const uint MemCommit = 0x1000;
    public const uint MemReserve = 0x2000;
    public const uint PageReadWrite = 0x04;
    public const uint MemRelease = 0x8000;

    // ── CreateProcess 标志 ────────────────────────────────────────────────
    public const uint CreateSuspended = 0x00000004;
    public const uint CreateNoWindow = 0x08000000;

    // ── 等待 ──────────────────────────────────────────────────────────────
    public const uint WaitTimeout = 0x00000102;
    public const uint StillActive = 0x00000103;

    // ── Toolhelp32 ────────────────────────────────────────────────────────
    public const uint Th32csSnapModule = 0x00000008;
    public const uint Th32csSnapModule32 = 0x00000010;
    public const int MaxModuleName32 = 256;

    // ── GetModuleHandleEx 标志（取本进程 kernel32 基址） ───────────────────
    public const uint GetModuleHandleExUnchangedRefCount = 0x00000004;
    public const uint GetModuleHandleExPin = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nint size,
        uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nint size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer,
        nint size, out nint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nint stackSize,
        IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

    // CharSet 必须显式写 Unicode：函数名带 W 但默认封送是 ANSI，
    // 否则传进去的字符串是乱码、句柄恒为 NULL（本探针踩过这个坑）。
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetModuleHandleExW(uint flags, IntPtr moduleName,
        out IntPtr module);

    // GetProcAddress 只有 ANSI 版本（LPCSTR），且没有 W/A 后缀。
    // 写成 CharSet.Unicode 会把字符串按 UTF-16 传进去，函数返回 NULL —— 本探针踩过这个坑。
    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Module32FirstW(IntPtr snapshot, ref ModuleEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Module32NextW(IntPtr snapshot, ref ModuleEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessW(string? applicationName, string? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SuspendThread(IntPtr thread);

    // ── Hook 通道（Phase A2） ─────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string fileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr module);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int hookId, IntPtr hookProc, IntPtr module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hook);

    // ── 建窗子进程（触发 CBT 事件用） ─────────────────────────────────────
    public delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WndClass
    {
        public uint Style;
        public IntPtr WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public IntPtr Window;
        public uint Value;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassW(ref WndClass windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu,
        IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PeekMessageW(out Message message, IntPtr window, uint min, uint max,
        uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr DispatchMessageW(ref Message message);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProcW(IntPtr window, uint message, IntPtr wParam,
        IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry32 entry);

    /// <summary>THREAD_SUSPEND_RESUME：冻结/解冻线程所需的最小权限。</summary>
    public const uint ThreadSuspendResume = 0x0002;

    public const uint Th32csSnapThread = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    public struct ThreadEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ThreadId;
        public uint OwnerProcessId;
        public int BasePriority;
        public int DeltaPriority;
        public uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetModuleFileNameW(IntPtr module, [Out] char[] fileName, uint size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ModuleEntry32
    {
        public uint Size;
        public uint ModuleId;
        public uint ProcessId;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr BaseAddress;
        public uint BaseSize;
        public IntPtr ModuleHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MaxModuleName32)]
        public string ModuleName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutablePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }
}