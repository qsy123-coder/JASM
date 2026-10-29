namespace JASM.InjectorProbe;

/// <summary>
/// 注入结果分类。
///
/// 刻意做成枚举而不是 bool：PRD 的可诊断性 KPI 要求"任何失败都落到可枚举原因之一"，
/// 隐藏失败（返回 false 但不说为什么）在实机上等于不可排障。
/// </summary>
public enum InjectionOutcome
{
    Success,

    /// <summary>目标进程不存在（PID 失效或已退出）。</summary>
    TargetNotFound,

    /// <summary>OpenProcess 被拒：通常是目标权限更高（管理员游戏）而本进程不是。</summary>
    AccessDenied,

    /// <summary>DLL 路径不存在或不是文件。</summary>
    DllPathMissing,

    /// <summary>远程 LoadLibraryW 返回 0：DLL 本身加载失败（依赖缺失 / 位数不符）。</summary>
    DllLoadFailed,

    /// <summary>CreateRemoteThread 失败。</summary>
    RemoteThreadFailed,

    /// <summary>远程线程 15 秒内没有结束（死锁 / 目标卡住）。</summary>
    Timeout,

    /// <summary>线程成功返回，但在目标模块表里找不到目标模块。</summary>
    ModuleNotLoaded,

    /// <summary>拒绝：目标是本探针自己。</summary>
    RefusedSelf,

    /// <summary>拒绝：目标看起来是受保护的游戏进程（探针只做自建靶子）。</summary>
    RefusedProtectedTarget,

    /// <summary>位数不匹配（32 位目标无法被 64 位注入器加载 64 位 DLL）。</summary>
    ArchitectureMismatch,

    /// <summary>Hook 通道专用：DLL 没有导出 `CBTProc`，无法用于全局钩子。</summary>
    HookDllMissingCbtProc,

    /// <summary>Hook 通道专用：SetWindowsHookExW 失败。</summary>
    HookInstallFailed,

    /// <summary>Hook 通道专用：钩子装上了，但目标始终没触发 CBT 事件。</summary>
    HookTimeout
}

public static class InjectionOutcomeText
{
    /// <summary>给人看的一句话。UI/日志都用它，避免各处自己拼字符串。</summary>
    public static string Describe(this InjectionOutcome outcome, int win32Error = 0)
    {
        var suffix = win32Error != 0 ? $"（Win32 {win32Error}）" : string.Empty;
        return outcome switch
        {
            InjectionOutcome.Success => "成功",
            InjectionOutcome.TargetNotFound => "目标进程不存在" + suffix,
            InjectionOutcome.AccessDenied => "拒绝访问：需要与目标同权限级（管理员游戏需提权运行）" + suffix,
            InjectionOutcome.DllPathMissing => "DLL 路径不存在",
            InjectionOutcome.DllLoadFailed => "DLL 在目标进程中加载失败：依赖缺失或位数不符",
            InjectionOutcome.RemoteThreadFailed => "CreateRemoteThread 失败" + suffix,
            InjectionOutcome.Timeout => "远程线程超时未返回",
            InjectionOutcome.ModuleNotLoaded => "注入返回成功但模块表里没有该模块",
            InjectionOutcome.RefusedSelf => "拒绝：不能注入自己",
            InjectionOutcome.RefusedProtectedTarget => "拒绝：目标是受保护的游戏进程，探针只对自建靶子工作",
            InjectionOutcome.ArchitectureMismatch => "位数不匹配",
            InjectionOutcome.HookDllMissingCbtProc => "DLL 没有导出 CBTProc，不能用于 Hook 通道",
            InjectionOutcome.HookInstallFailed => "SetWindowsHookExW 失败" + suffix,
            InjectionOutcome.HookTimeout => "钩子已安装但目标始终未触发 CBT 事件",
            _ => outcome.ToString() + suffix
        };
    }
}