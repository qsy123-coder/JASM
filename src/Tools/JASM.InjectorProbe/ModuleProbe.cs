namespace JASM.InjectorProbe;

/// <summary>
/// 枚举目标进程的已加载模块（Toolhelp32）。
///
/// 两个用途：
/// 1) 注入结果核对 —— PRD 要求"目标进程模块表里出现该模块才算成功"；
/// 2) 取目标进程 kernel32 的实际基址 —— 远程 LoadLibraryW 的地址要靠它 + 本地偏移算出来。
/// </summary>
internal static class ModuleProbe
{
    public sealed record ModuleInfo(string Name, IntPtr BaseAddress);

    /// <summary>
    /// 列出目标进程的模块。失败返回空表（调用方按"查不到"处理，不抛）。
    /// </summary>
    public static IReadOnlyList<ModuleInfo> Snapshot(int processId)
    {
        var result = new List<ModuleInfo>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32csSnapModule | NativeMethods.Th32csSnapModule32, processId);
        if (snapshot == InvalidHandle)
        {
            return result;
        }

        try
        {
            var entry = new NativeMethods.ModuleEntry32
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ModuleEntry32>()
            };

            if (!NativeMethods.Module32FirstW(snapshot, ref entry))
            {
                return result;
            }

            do
            {
                result.Add(new ModuleInfo(entry.ModuleName, entry.BaseAddress));
                entry.Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.ModuleEntry32>();
            }
            while (NativeMethods.Module32NextW(snapshot, ref entry));
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }

        return result;
    }

    /// <summary>目标里是否已有指定模块（按文件名，不区分大小写）。</summary>
    public static bool Contains(int processId, string moduleName) =>
        Snapshot(processId).Any(m => string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase));

    /// <summary>取目标进程里指定模块的基址；找不到返回 <see cref="IntPtr.Zero"/>。</summary>
    public static IntPtr GetBaseAddress(int processId, string moduleName) =>
        Snapshot(processId)
            .FirstOrDefault(m => string.Equals(m.Name, moduleName, StringComparison.OrdinalIgnoreCase))
            ?.BaseAddress ?? IntPtr.Zero;

    private static readonly IntPtr InvalidHandle = new(-1);
}