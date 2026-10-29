namespace JASM.InjectorProbe;

/// <summary>注入通道。第一版只实现直接注入；Hook 通道要等有导出 CBTProc 的原生 DLL。</summary>
public enum InjectionChannelKind
{
    /// <summary>远程 LoadLibraryW（已实测可用）。</summary>
    Direct,

    /// <summary>全局 WH_CBT 钩子（Phase A2，当前不可用，见 README）。</summary>
    Hook
}

/// <summary>
/// 一次注入作业：投什么、投到哪、怎么投。
///
/// 刻意做成纯数据 + 纯校验，不碰任何句柄：这样它可以被单测、
/// 可以在真正动手前先 dry-run 打印出来给人确认。
/// </summary>
public sealed record InjectionJob
{
    /// <summary>目标进程名（不含 .exe 也可以）。与 <see cref="ProcessId"/> 二选一。</summary>
    public string? ProcessName { get; init; }

    /// <summary>目标 PID。给了 PID 就不再按名字找。</summary>
    public int? ProcessId { get; init; }

    /// <summary>要注入的 DLL 列表，按顺序注入。裸名字会解析到 system32。</summary>
    public IReadOnlyList<string> Dlls { get; init; } = Array.Empty<string>();

    /// <summary>通道。默认直接注入。</summary>
    public InjectionChannelKind Channel { get; init; } = InjectionChannelKind.Direct;

    /// <summary>注入前等待的毫秒数（对应"注入延迟"设置）。</summary>
    public int DelayMs { get; init; }

    /// <summary>注入时冻结目标全部线程（更接近第三方管理器的安全做法）。</summary>
    public bool FreezeThreads { get; init; } = true;

    /// <summary>只打印计划、不真的注入。</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// 校验作业本身是否成立（不查进程是否存在、不查文件是否存在——那些属于运行期）。
    /// 返回空表表示合法。
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (ProcessId is null && string.IsNullOrWhiteSpace(ProcessName))
        {
            errors.Add("必须指定目标：--pid 或 --process");
        }

        if (ProcessId is <= 0)
        {
            errors.Add($"--pid 必须是正整数（收到 {ProcessId}）");
        }

        if (Dlls.Count == 0)
        {
            errors.Add("至少要有一个 --dll");
        }

        if (Dlls.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("DLL 列表里有空项");
        }

        if (DelayMs is < 0 or > 120_000)
        {
            errors.Add($"--delay 超出范围 0..120000（收到 {DelayMs}）");
        }

        if (Channel == InjectionChannelKind.Hook)
        {
            errors.Add("Hook 通道尚未实现（需要一个导出 CBTProc 的原生 DLL，Phase A2）");
        }

        return errors;
    }

    /// <summary>给人看的一行摘要，用于 plan 输出与日志。</summary>
    public string Describe()
    {
        var target = ProcessId is not null ? $"pid={ProcessId}" : $"process={ProcessName}";
        var channel = Channel == InjectionChannelKind.Hook ? "Hook(WH_CBT)" : "直接注入";
        var freeze = FreezeThreads ? "冻结线程" : "不冻结";
        var delay = DelayMs > 0 ? $"延迟 {DelayMs}ms" : "无延迟";
        return $"{target}  {channel}  {freeze}  {delay}  DLL×{Dlls.Count}";
    }
}