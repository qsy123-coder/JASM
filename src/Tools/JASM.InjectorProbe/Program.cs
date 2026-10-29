using System.Diagnostics;
using System.Text;

namespace JASM.InjectorProbe;

/// <summary>
/// 注入通道可行性探针（Phase A1）。
///
/// 子命令：
///   matrix     [--count N] [--dll 名称或路径]   普通启动靶进程 → 注入 → 核对
///   suspended  [--count N] [--dll ...]          挂起启动 → 注入 → 放行 → 核对（Rocket 的流程）
///   once       --pid N [--dll ...]              注入一个已存在的进程（带保护性拒绝）
///   negative                                    失败矩阵：自注入 / 不存在的 PID / 不存在的 DLL
///
/// 默认注入目标 <c>winmm.dll</c>：系统自带、无副作用，且注入前一定不在目标模块表里，
/// 因此能真正验证"模块出现了"，而不是被已经加载的 DLL 蒙混过关。
/// </summary>
internal static class Program
{
    /// <summary>拒绝注入的游戏进程名（探针只对自建靶子工作）。</summary>
    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Client-Win64-Shipping", "Client-Win64-Shipping.exe",
        "YuanShen", "GenshinImpact", "StarRail", "ZenlessZoneZero", "Endfield", "HTGame"
    };

    private const string DefaultDll = "winmm.dll";

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        // 被自己拉起的"建窗子进程"：只建窗口、泵消息、到点退出（Hook 通道用）
        if (args[0] == "--window-child")
        {
            var seconds = args.Length > 1 && int.TryParse(args[1], out var parsed) ? parsed : 20;
            return WindowChild.Run(seconds);
        }

        var command = args[0].ToLowerInvariant();
        var options = ParseOptions(args.Skip(1).ToArray());

        return command switch
        {
            "matrix" => RunMatrix(count: options.Count, dll: options.Dll, suspended: false),
            "suspended" => RunMatrix(count: options.Count, dll: options.Dll, suspended: true),
            "once" => RunOnce(options.ProcessId, options.Dll),
            "negative" => RunNegative(),
            "plan" => RunPlan(options.BuildJob(dryRun: true)),
            "apply" => RunPlan(options.BuildJob(dryRun: false)),
            "hook" => RunHook(options.Dll, options.TimeoutSeconds),
            _ => Unknown(command)
        };
    }

    /// <summary>
    /// Hook 通道（Phase A2）：需要一个导出 CBTProc 的原生 DLL（CI 产出的 cbtprobe.dll）。
    /// </summary>
    private static int RunHook(string dll, int timeoutSeconds)
    {
        var resolved = ResolveDll(dll);
        if (resolved is null)
        {
            Console.WriteLine($"[失败] 找不到 DLL：{dll}");
            Console.WriteLine("       这个 DLL 必须导出 CBTProc；本机没有 C++ 编译器，");
            Console.WriteLine("       请从 CI 产物 cbtprobe-x64 下载（见 native/README）或自行用 VS Build Tools 编译。");
            return 1;
        }

        Console.WriteLine($"Hook 通道（WH_CBT）  DLL：{resolved}  超时：{timeoutSeconds}s");
        Console.WriteLine("正在安装全局钩子并拉起建窗子进程……");

        var (outcome, detail) = CbtHookInjector.Run(resolved, TimeSpan.FromSeconds(timeoutSeconds));
        Console.WriteLine();
        Console.WriteLine($"结果：{outcome.Describe()}");
        Console.WriteLine($"      {detail}");
        return outcome == InjectionOutcome.Success ? 0 : 2;
    }

    // ── 作业模型：plan / apply ────────────────────────────────────────────

    private static int RunPlan(InjectionJob job)
    {
        Console.WriteLine($"作业：{job.Describe()}");

        var validation = job.Validate();
        if (validation.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("作业不合法：");
            foreach (var error in validation)
            {
                Console.WriteLine($"  ✗ {error}");
            }

            return 2;
        }

        if (job.DryRun)
        {
            Console.WriteLine();
            Console.WriteLine("计划（dry-run，不会真的注入）：");

            var target = InjectionRunner.DescribeTarget(job);
            Console.WriteLine($"  {(target.Ok ? "✓" : "✗")} 目标：{target.Text}");

            foreach (var dll in job.Dlls)
            {
                var resolved = Path.IsPathRooted(dll)
                    ? (File.Exists(dll) ? dll : "（不存在）")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), dll);

                var exists = Path.IsPathRooted(dll) ? File.Exists(dll) : File.Exists(resolved);
                Console.WriteLine($"  {(exists ? "✓" : "✗")} {dll}  →  {resolved}");
            }

            return 0;
        }

        var run = InjectionRunner.Run(job);
        Console.WriteLine($"目标：{run.ProcessName}（{run.ProcessId}）");
        Console.WriteLine();

        foreach (var dll in run.Dlls)
        {
            var mark = dll.Outcome == InjectionOutcome.Success ? "✓" : "✗";
            Console.WriteLine($"  {mark} {Path.GetFileName(dll.Dll),-16} " +
                              $"{dll.Outcome.Describe(dll.Win32Error)}  {dll.ElapsedMs:F1} ms");
        }

        foreach (var error in run.Errors)
        {
            Console.WriteLine($"  ✗ {error}");
        }

        Console.WriteLine();
        Console.WriteLine(run.AllSucceeded ? "结果：全部成功" : "结果：失败");
        return run.AllSucceeded ? 0 : 2;
    }

    // ── 子命令实现 ────────────────────────────────────────────────────────

    private static int RunMatrix(int count, string dll, bool suspended)
    {
        var resolved = ResolveDll(dll);
        if (resolved is null)
        {
            Console.WriteLine($"[失败] 找不到 DLL：{dll}");
            return 1;
        }

        Console.WriteLine($"模式：{(suspended ? "挂起→注入→放行" : "普通启动→注入")}    目标 DLL：{resolved}");
        Console.WriteLine($"次数：{count}    进程位数：{(Environment.Is64BitProcess ? "x64" : "x86")}");
        Console.WriteLine();
        Console.WriteLine($"{"#",-3} {"PID",-7} {"结果",-10} {"耗时",-8} 说明");
        Console.WriteLine(new string('-', 78));

        var outcomes = new List<(InjectionOutcome Outcome, double Elapsed)>();

        for (var i = 1; i <= count; i++)
        {
            // 两种模式都普通启动：CREATE_SUSPENDED 的进程加载器还没跑，kernel32 不可用，
            // 远程 LoadLibrary 必然失败（实测 0/10）。"冻结"由 ThreadFreezer 在加载器就绪后完成。
            using var target = TargetProcess.Spawn(suspended: false, TimeSpan.FromSeconds(30));

            // 冻结模式：等加载器就绪 → 冻结全部线程 → 注入 → 解冻 → 核对
            if (suspended)
            {
                RemoteLoadLibrary.Result result;
                var outcome = InjectionOutcome.Timeout;

                // 轮询等 kernel32 出现（加载器初始化完成），最多 2 秒
                var deadline = Environment.TickCount64 + 2000;
                while (Environment.TickCount64 < deadline &&
                       ModuleProbe.GetBaseAddress(target.ProcessId, "kernel32.dll") == IntPtr.Zero)
                {
                    Thread.Sleep(20);
                }

                using (var freezer = new ThreadFreezer())
                {
                    freezer.Freeze(target.ProcessId);
                    result = RemoteLoadLibrary.Inject(target.ProcessId, resolved);
                    freezer.Thaw();

                    var verified = result.Outcome == InjectionOutcome.Success &&
                                   ModuleProbe.Contains(target.ProcessId, Path.GetFileName(resolved));
                    outcome = verified ? InjectionOutcome.Success : result.Outcome;
                }

                outcomes.Add((outcome, result.ElapsedMs));
                PrintRow(i, target.ProcessId, outcome, result.ElapsedMs);
            }
            else
            {
                // 普通模式：等 cmd 进入空闲再注入
                Thread.Sleep(200);
                var result = RemoteLoadLibrary.Inject(target.ProcessId, resolved);
                outcomes.Add((result.Outcome, result.ElapsedMs));
                PrintRow(i, target.ProcessId, result.Outcome, result.ElapsedMs);
            }
        }

        PrintSummary(outcomes);
        return outcomes.All(o => o.Outcome == InjectionOutcome.Success) ? 0 : 2;
    }

    private static int RunOnce(int processId, string dll)
    {
        if (processId <= 0)
        {
            Console.WriteLine("[失败] 需要 --pid <进程号>");
            return 1;
        }

        if (processId == Environment.ProcessId)
        {
            Console.WriteLine("[拒绝] 不能注入自己");
            return 2;
        }

        string name;
        try
        {
            name = Process.GetProcessById(processId).ProcessName;
        }
        catch (ArgumentException)
        {
            Console.WriteLine($"[失败] 目标进程 {processId} 不存在");
            return 1;
        }

        if (ProtectedProcessNames.Contains(name) || ProtectedProcessNames.Contains(name + ".exe"))
        {
            Console.WriteLine($"[拒绝] {name} 看起来是受保护的游戏进程；探针只对自建靶子工作");
            return 2;
        }

        var resolved = ResolveDll(dll);
        if (resolved is null)
        {
            Console.WriteLine($"[失败] 找不到 DLL：{dll}");
            return 1;
        }

        var result = RemoteLoadLibrary.Inject(processId, resolved);
        Console.WriteLine($"目标：{name}（{processId}）  DLL：{resolved}");
        Console.WriteLine($"结果：{result.Outcome.Describe(result.Win32Error)}    耗时：{result.ElapsedMs:F1} ms");
        return result.Outcome == InjectionOutcome.Success ? 0 : 2;
    }

    private static int RunNegative()
    {
        Console.WriteLine("失败矩阵（每项都应被正确分类，而不是静默失败）");
        Console.WriteLine();

        var dll = ResolveDll(DefaultDll);
        if (dll is null)
        {
            Console.WriteLine($"[失败] 系统里找不到 {DefaultDll}");
            return 1;
        }

        // 1) 注入自己
        var self = RemoteLoadLibrary.Inject(Environment.ProcessId, dll);
        Report("注入自己", self, InjectionOutcome.RefusedSelf);

        // 2) 不存在的 PID
        var ghost = RemoteLoadLibrary.Inject(0x7FFF_FFF0, dll);
        Report("不存在的 PID", ghost, InjectionOutcome.TargetNotFound);

        // 3) 不存在的 DLL（注入自己会被先拦下，所以这里对真实靶进程做）
        using (var target = TargetProcess.Spawn(suspended: false, TimeSpan.FromSeconds(10)))
        {
            Thread.Sleep(200);

            var missing = RemoteLoadLibrary.Inject(target.ProcessId, @"C:\definitely\not\here\nope.dll");
            Report("不存在的 DLL", missing, InjectionOutcome.DllPathMissing);

            // 4) 存在的 DLL，注入到真实靶进程
            var good = RemoteLoadLibrary.Inject(target.ProcessId, dll);
            Report("正常注入", good, InjectionOutcome.Success);
        }

        return 0;

        static void Report(string label, RemoteLoadLibrary.Result result, InjectionOutcome expected)
        {
            var mark = result.Outcome == expected ? "✓" : "✗";
            Console.WriteLine($"  {mark} {label,-16} → {result.Outcome.Describe(result.Win32Error)}" +
                              (result.Outcome == expected ? string.Empty : $"（期望 {expected}）"));
        }
    }

    // ── 输出 ──────────────────────────────────────────────────────────────

    private static void PrintRow(int index, int pid, InjectionOutcome outcome, double elapsedMs)
    {
        var text = outcome.Describe();
        Console.WriteLine($"{index,-3} {pid,-7} {outcome,-10} {$"{elapsedMs:F1} ms",-9} {text}");
    }

    private static void PrintSummary(List<(InjectionOutcome Outcome, double Elapsed)> outcomes)
    {
        var success = outcomes.Count(o => o.Outcome == InjectionOutcome.Success);
        var total = outcomes.Count;
        var elapsed = outcomes.Select(o => o.Elapsed).OrderBy(v => v).ToList();
        double Median() => elapsed[elapsed.Count / 2];
        double P95() => elapsed[(int)Math.Clamp(elapsed.Count * 0.95, 0, elapsed.Count - 1)];

        Console.WriteLine();
        Console.WriteLine($"成功 {success}/{total}" +
                          (total > 0 ? $"（{(success * 100.0 / total):F1}%）" : string.Empty) +
                          $"    耗时 中位 {Median():F1} ms / P95 {P95():F1} ms");

        var failures = outcomes.Where(o => o.Outcome != InjectionOutcome.Success)
            .GroupBy(o => o.Outcome)
            .Select(g => $"{g.Key}×{g.Count()}");
        var failureText = string.Join("  ", failures);
        if (failureText.Length > 0)
        {
            Console.WriteLine($"失败分布：{failureText}");
        }
    }

    // ── 参数 ──────────────────────────────────────────────────────────────

    private sealed record Options(
        int Count,
        string Dll,
        int ProcessId,
        string? ProcessName,
        IReadOnlyList<string> Dlls,
        int DelayMs,
        InjectionChannelKind Channel,
        bool Freeze,
        int TimeoutSeconds)
    {
        /// <summary>把命令行选项折叠成一次注入作业。</summary>
        public InjectionJob BuildJob(bool dryRun) => new()
        {
            ProcessId = ProcessId > 0 ? ProcessId : null,
            ProcessName = ProcessName,
            Dlls = Dlls,
            Channel = Channel,
            DelayMs = DelayMs,
            FreezeThreads = Freeze,
            DryRun = dryRun
        };
    }

    private static Options ParseOptions(string[] args)
    {
        var count = 10;
        var dll = DefaultDll;
        var pid = 0;
        string? processName = null;
        var dlls = new List<string>();
        var delayMs = 0;
        var channel = InjectionChannelKind.Direct;
        var freeze = true;
        var timeoutSeconds = 10;
        var verbose = args.Contains("--verbose");
        RemoteLoadLibrary.Trace = verbose ? Console.WriteLine : null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--count":
                    int.TryParse(args[i + 1], out count);
                    break;
                case "--dll":
                    dll = args[i + 1];
                    dlls.Add(args[i + 1]);
                    break;
                case "--pid":
                    int.TryParse(args[i + 1], out pid);
                    break;
                case "--process":
                    processName = args[i + 1];
                    break;
                case "--delay":
                    int.TryParse(args[i + 1], out delayMs);
                    break;
                case "--channel":
                    channel = string.Equals(args[i + 1], "hook", StringComparison.OrdinalIgnoreCase)
                        ? InjectionChannelKind.Hook
                        : InjectionChannelKind.Direct;
                    break;
                case "--no-freeze":
                    freeze = false;
                    break;
                case "--timeout":
                    int.TryParse(args[i + 1], out timeoutSeconds);
                    break;
            }
        }

        // plan/apply 没写 --dll 时给一个安全的默认值，避免空作业
        if (dlls.Count == 0)
        {
            dlls.Add(dll);
        }

        return new Options(Math.Clamp(count, 1, 200), dll, pid, processName, dlls, delayMs, channel, freeze,
            Math.Clamp(timeoutSeconds, 1, 300));
    }

    /// <summary>
    /// --dll 允许写裸名字（winmm.dll）或完整路径。
    /// 裸名字一律解析到 system32，避免意外注入同目录下的同名文件。
    /// </summary>
    private static string? ResolveDll(string dll)
    {
        if (Path.IsPathRooted(dll))
        {
            return File.Exists(dll) ? dll : null;
        }

        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), dll);
        return File.Exists(system32) ? system32 : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("JASM Injector Probe —— 注入通道可行性探针（Phase A1）");
        Console.WriteLine();
        Console.WriteLine("  matrix    [--count N] [--dll 名字]   普通靶进程 → 注入 → 核对（通道验证）");
        Console.WriteLine("  suspended [--count N] [--dll 名字]   冻结线程 → 注入 → 解冻 → 核对");
        Console.WriteLine("  once      --pid N [--dll 名字]       注入一个已存在的进程");
        Console.WriteLine("  negative                             失败矩阵自检");
        Console.WriteLine("  plan      (--pid N | --process 名字) [--dll 名字]…  dry-run：只打印计划");
        Console.WriteLine("  apply     (--pid N | --process 名字) [--dll 名字]…  按作业模型执行注入");
        Console.WriteLine("  hook      --dll <导出 CBTProc 的 DLL>             Hook 通道（Phase A2，需 CI 产物）");
        Console.WriteLine();
        Console.WriteLine("  通用选项：--delay 毫秒  --no-freeze  --channel direct|hook  --verbose");
        Console.WriteLine();
        Console.WriteLine("默认 DLL：winmm.dll（系统自带、无副作用、注入前不在目标模块表里）");
        Console.WriteLine("绝不注入游戏进程；--pid 指向已知游戏会被拒绝。");
    }

    private static int Unknown(string command)
    {
        Console.WriteLine($"未知子命令：{command}");
        PrintUsage();
        return 1;
    }
}