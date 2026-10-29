using System.Diagnostics;

namespace JASM.InjectorProbe;

/// <summary>
/// 把 <see cref="InjectionJob"/> 落到实际动作上：
/// 解析目标 → （可选）冻结线程 → 逐个注入 → 解冻 → 汇总。
///
/// 通道目前只有直接注入；Hook 通道在 <see cref="InjectionJob.Validate"/> 里就被拦下了，
/// 这里不需要再判一次。
/// </summary>
internal static class InjectionRunner
{
    public sealed record DllResult(string Dll, InjectionOutcome Outcome, int Win32Error, double ElapsedMs);

    public sealed record RunResult(
        int ProcessId,
        string ProcessName,
        IReadOnlyList<DllResult> Dlls,
        IReadOnlyList<string> Errors)
    {
        public bool AllSucceeded => Errors.Count == 0 && Dlls.All(d => d.Outcome == InjectionOutcome.Success);
    }

    public static RunResult Run(InjectionJob job)
    {
        var errors = new List<string>();

        var validation = job.Validate();
        if (validation.Count > 0)
        {
            return new RunResult(0, string.Empty, Array.Empty<DllResult>(), validation);
        }

        // 1) 解析目标
        if (!TryResolveTarget(job, out var pid, out var processName, out var targetError))
        {
            return new RunResult(0, string.Empty, Array.Empty<DllResult>(), new[] { targetError! });
        }

        // 2) 解析 DLL 路径（裸名字 → system32）
        var resolved = new List<string>();
        foreach (var dll in job.Dlls)
        {
            var path = ResolveDllPath(dll);
            if (path is null)
            {
                errors.Add($"找不到 DLL：{dll}");
            }
            else
            {
                resolved.Add(path);
            }
        }

        if (errors.Count > 0)
        {
            return new RunResult(pid, processName, Array.Empty<DllResult>(), errors);
        }

        // 3) 延迟（对应注入延迟设置）
        if (job.DelayMs > 0)
        {
            Thread.Sleep(job.DelayMs);
        }

        // 4) 注入（可选：全程冻结目标线程）
        var results = new List<DllResult>();
        using var freezer = new ThreadFreezer();
        if (job.FreezeThreads)
        {
            freezer.Freeze(pid);
        }

        try
        {
            foreach (var path in resolved)
            {
                var result = RemoteLoadLibrary.Inject(pid, path);
                results.Add(new DllResult(path, result.Outcome, result.Win32Error, result.ElapsedMs));

                // 第一个失败就停：继续投下一个只会让现场更难判断
                if (result.Outcome != InjectionOutcome.Success)
                {
                    break;
                }
            }
        }
        finally
        {
            freezer.Thaw();
        }

        return new RunResult(pid, processName, results, errors);
    }

    /// <summary>
    /// dry-run 用：只解析目标并给出人话，不注入。
    /// </summary>
    public static (bool Ok, string Text) DescribeTarget(InjectionJob job) =>
        TryResolveTarget(job, out var pid, out var name, out var error)
            ? (true, $"{name}（{pid}）")
            : (false, error ?? "目标解析失败");

    /// <summary>
    /// 解析目标：给了 PID 就直接用；否则按进程名找唯一实例。
    /// 同名多开时**拒绝**而不是随便挑一个——挑错目标比失败更糟。
    /// </summary>
    private static bool TryResolveTarget(InjectionJob job, out int processId, out string processName,
        out string? error)
    {
        processId = 0;
        processName = string.Empty;
        error = null;

        if (job.ProcessId is not null)
        {
            processId = job.ProcessId.Value;
            try
            {
                processName = Process.GetProcessById(processId).ProcessName;
            }
            catch (ArgumentException)
            {
                error = $"目标进程 {processId} 不存在";
                return false;
            }

            return true;
        }

        var wanted = (job.ProcessName ?? string.Empty).Replace(".exe", string.Empty,
            StringComparison.OrdinalIgnoreCase);

        var matches = Process.GetProcesses()
            .Where(p => string.Equals(p.ProcessName, wanted, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            error = $"找不到进程：{job.ProcessName}";
            return false;
        }

        if (matches.Count > 1)
        {
            error = $"进程 {job.ProcessName} 有 {matches.Count} 个实例（PID：{string.Join("/", matches.Select(m => m.Id))}），" +
                    "请用 --pid 指定";
            return false;
        }

        processId = matches[0].Id;
        processName = matches[0].ProcessName;
        return true;
    }

    /// <summary>
    /// 解析 DLL 路径：绝对路径原样；带分隔符的相对路径按当前目录展开；
    /// 裸名字一律到 system32 找（避免命中同目录同名文件）。
    /// </summary>
    private static string? ResolveDllPath(string dll)
    {
        if (Path.IsPathRooted(dll))
        {
            return File.Exists(dll) ? dll : null;
        }

        if (dll.Contains('\\') || dll.Contains('/'))
        {
            var full = Path.GetFullPath(dll);
            return File.Exists(full) ? full : null;
        }

        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), dll);
        return File.Exists(system32) ? system32 : null;
    }
}