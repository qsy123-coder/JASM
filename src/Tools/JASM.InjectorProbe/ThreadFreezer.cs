using System.Runtime.InteropServices;

namespace JASM.InjectorProbe;

/// <summary>
/// 冻结/解冻目标进程的全部线程。
///
/// 为什么需要它：<c>CreateProcess(CREATE_SUSPENDED)</c> 的进程还停在 ntdll 加载器初始化之前，
/// 此时 kernel32 尚未就绪，远程 LoadLibrary 注定失败（实测：模块表为空 → 判为"目标不存在"）。
/// 稳妥做法与第三方管理器一致：**先让进程跑起来，再冻结全部线程 → 注入 → 解冻**，
/// 这样既保证 kernel32 可用，又能避免注入期间目标主线程继续往前跑。
/// </summary>
internal sealed class ThreadFreezer : IDisposable
{
    private readonly List<IntPtr> _handles = new();

    /// <summary>挂起目标进程当前的所有线程。返回成功挂起的数量。</summary>
    public int Freeze(int processId)
    {
        Thaw();

        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32csSnapThread, 0);
        if (snapshot == new IntPtr(-1))
        {
            return 0;
        }

        try
        {
            var entry = new NativeMethods.ThreadEntry32
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.ThreadEntry32>()
            };

            if (!NativeMethods.Thread32First(snapshot, ref entry))
            {
                return 0;
            }

            do
            {
                if (entry.OwnerProcessId != processId)
                {
                    entry.Size = (uint)Marshal.SizeOf<NativeMethods.ThreadEntry32>();
                    continue;
                }

                // 逐个挂起：自己开句柄，避免依赖快照句柄生命周期
                var handle = NativeMethods.OpenThread(NativeMethods.ThreadSuspendResume, false, entry.ThreadId);
                if (handle != IntPtr.Zero && NativeMethods.SuspendThread(handle) != uint.MaxValue)
                {
                    _handles.Add(handle);
                }
                else if (handle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(handle);
                }

                entry.Size = (uint)Marshal.SizeOf<NativeMethods.ThreadEntry32>();
            }
            while (NativeMethods.Thread32Next(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return _handles.Count;
    }

    /// <summary>解冻并释放所有句柄。可重复调用。</summary>
    public void Thaw()
    {
        foreach (var handle in _handles)
        {
            NativeMethods.ResumeThread(handle);
            NativeMethods.CloseHandle(handle);
        }

        _handles.Clear();
    }

    public void Dispose() => Thaw();
}