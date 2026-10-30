using System.Text;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>提权助手对「启动并注入」命令 <c>5</c> 的回复种类。</summary>
public enum ElevatorLaunchInjectReply
{
    /// <summary>没读到可识别的回复（连接断了 / 读不到行）。对面多半是不认识 <c>5</c> 的旧版助手。</summary>
    None,

    /// <summary>游戏已启动，<see cref="ElevatorLaunchInjectResult.ProcessId"/> 是新进程 PID。</summary>
    Ok,

    /// <summary>拒绝或失败，原因见 <see cref="ElevatorLaunchInjectResult.FailureReason"/>。</summary>
    Failure
}

/// <summary><see cref="ElevatorLaunchInjectProtocol.TryParseReply"/> 的解析结果。</summary>
public sealed record ElevatorLaunchInjectResult(
    ElevatorLaunchInjectReply Kind,
    int ProcessId,
    string? FailureReason)
{
    public static ElevatorLaunchInjectResult None { get; } = new(ElevatorLaunchInjectReply.None, 0, null);
}

/// <summary>一次「挂起启动 + 注入」请求。路径都要求是绝对本地路径。</summary>
public sealed record ElevatorLaunchInjectRequest(
    string GameExecutablePath,
    string DllPath,
    string WorkingDirectory,
    string Arguments,
    string? MapperDllPath = null)
{
    /// <summary>把参数编成 base64：命令行里可能有空格、引号、中文，逐行协议不该再自己解析一次。</summary>
    public string EncodedArguments => Convert.ToBase64String(Encoding.UTF8.GetBytes(Arguments ?? string.Empty));
}

/// <summary>
/// 主程序 ↔ <c>Elevator.exe</c> 的「启动并注入」命令 <c>5</c>。
///
/// 为什么必须由助手做：游戏要以高完整性运行（WWMI 注入需要），而 JASM 自己是中完整性，
/// <see cref="System.Diagnostics.Process"/> 既不能以 <c>CREATE_SUSPENDED</c> 创建进程，也拿不到
/// 可注入的高完整性进程句柄。助手是已提权的常驻进程，由它 <c>CreateProcessW(CREATE_SUSPENDED)</c>
/// → 远程 <c>LoadLibraryW</c> 注入 <c>d3d11.dll</c> → <c>ResumeThread</c>，再把 PID 回传给主程序。
///
/// 线路格式（与其它 Elevator 命令一样，一行一个字段）：
/// <code>
/// 客户端 → 助手                    助手 → 客户端
/// 5                               OK &lt;pid&gt;
/// &lt;游戏 exe 绝对路径&gt;              FAIL:&lt;reason&gt;
/// &lt;d3d11.dll 绝对路径&gt;
/// &lt;Jasm.UhdMapper.dll 绝对路径，可为空&gt;
/// &lt;工作目录 绝对路径&gt;
/// &lt;参数 base64(UTF-8)，可为空&gt;
/// </code>
///
/// **校验放在这里而不是只放主程序**：助手是提权的，任何同用户进程都能连它的管道；如果助手
/// 只信调用方，它就变成了「以管理员权限启动任意 exe 并注入任意 DLL」的提权原语。
/// 这里把可注入的对象收窄到 WWMI 的 <c>d3d11.dll</c> 与两个已知游戏 exe 名，且拒绝 UNC。
/// 主程序侧调用的仍是同一个 <see cref="TryParsePayload"/>，规则只有一份。
/// </summary>
public static class ElevatorLaunchInjectProtocol
{
    /// <summary>启动并注入命令；后面跟四行载荷。</summary>
    public const string LaunchInjectCommand = "5";

    /// <summary>助手认识 <see cref="LaunchInjectCommand"/> 的最低 FileVersion（只增不改）。</summary>
    public const string MinimumFileVersionForLaunchInject = "5.0.0.0";

    /// <summary>允许注入的 DLL 文件名。WWMI/3DMigoto 的加载器就是 d3d11 代理。</summary>
    public const string InjectedDllFileName = "d3d11.dll";

    /// <summary>DLL 必须位于名为 WWMI 的导入器目录下，避免被当成通用注入器。</summary>
    public const string ImporterDirectoryName = "WWMI";

    /// <summary>可选注入的 JASM UHD 映射 DLL 文件名（Phase 2 使用；为空表示只注入 d3d11）。</summary>
    public const string MapperDllFileName = "Jasm.UhdMapper.dll";

    /// <summary>映射 DLL 必须放在名为 UhdRuntime 的 JASM 管理目录下，避免被当成通用注入器。</summary>
    public const string MapperDirectoryName = "UhdRuntime";

    /// <summary>
    /// 映射 DLL 装好 D3D11 钩子后会 SetEvent 的就绪事件名前缀（后接游戏 pid）。
    /// 助手在 ResumeThread 之前等待它，避免游戏在钩子就绪前创建 D3D11 设备。
    /// 原生侧 `MapperApp.cpp` 的 `kReadyEventPrefix` 必须与此完全一致。
    /// </summary>
    public const string ReadyEventNamePrefix = @"Local\Jasm.UhdMapper.Ready.";

    /// <summary>拼出映射 DLL 的就绪事件名。</summary>
    public static string BuildReadyEventName(int processId) =>
        ReadyEventNamePrefix + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>允许启动的游戏 exe。不要加入 Launcher.exe —— 往启动器里注入 d3d11 代理没有意义。</summary>
    public static readonly string[] AllowedGameExecutableNames =
    [
        "Wuthering Waves.exe",
        "Client-Win64-Shipping.exe"
    ];

    /// <summary>参数上限。正常启动参数远小于这个值，超了直接拒绝。</summary>
    public const int MaxArgumentsBytes = 4096;

    /// <summary>路径上限，防止畸形载荷把日志/文本框撑爆。</summary>
    public const int MaxPathLength = 1024;

    // 原因 token：主程序按 token 映射用户文案，助手只负责给出稳定的机器可读值。
    public const string ReasonBadPayload = "bad-payload";
    public const string ReasonPathNotAllowed = "path-not-allowed";
    public const string ReasonExeMissing = "exe-missing";
    public const string ReasonDllMissing = "dll-missing";
    public const string ReasonCreateProcess = "create-process";
    public const string ReasonInjectFailed = "inject-failed";
    public const string ReasonInjectTimeout = "inject-timeout";
    public const string ReasonResumeFailed = "resume-failed";

    /// <summary>这个 <c>Elevator.exe</c> 认不认 <see cref="LaunchInjectCommand"/>。</summary>
    public static bool SupportsLaunchInject(string? elevatorFileVersion)
    {
        return Version.TryParse(elevatorFileVersion, out var actual)
               && Version.TryParse(MinimumFileVersionForLaunchInject, out var minimum)
               && actual >= minimum;
    }

    /// <summary>编成六行载荷，交给管道逐行写。</summary>
    public static string[] BuildPayload(ElevatorLaunchInjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return
        [
            LaunchInjectCommand,
            request.GameExecutablePath,
            request.DllPath,
            request.MapperDllPath ?? string.Empty,
            request.WorkingDirectory,
            request.EncodedArguments
        ];
    }

    /// <summary>
    /// 解析五行载荷并做完整校验。任何一项不合法都返回 false，<paramref name="failureReason"/> 是
    /// <see cref="ReasonBadPayload"/> 或 <see cref="ReasonPathNotAllowed"/>。
    /// </summary>
    public static bool TryParsePayload(IReadOnlyList<string?> payloadLines,
        out ElevatorLaunchInjectRequest? request, out string? failureReason)
    {
        request = null;
        failureReason = null;

        if (payloadLines.Count < 5)
        {
            failureReason = ReasonBadPayload;
            return false;
        }

        var exe = payloadLines[0]?.Trim();
        var dll = payloadLines[1]?.Trim();
        var mapper = payloadLines[2]?.Trim();
        var workingDirectory = payloadLines[3]?.Trim();
        var encodedArguments = payloadLines[4]?.Trim() ?? string.Empty;

        if (!TryValidatePaths(exe, dll, workingDirectory, mapper, out failureReason))
            return false;

        if (!TryDecodeArguments(encodedArguments, out var arguments))
        {
            failureReason = ReasonBadPayload;
            return false;
        }

        request = new ElevatorLaunchInjectRequest(exe!, dll!, workingDirectory!, arguments,
            string.IsNullOrEmpty(mapper) ? null : mapper);
        return true;
    }

    /// <summary>路径校验：绝对、本地（非 UNC）、长度受限、DLL 名/父目录与 exe 名都在白名单内。</summary>
    public static bool TryValidatePaths(string? gameExecutablePath, string? dllPath, string? workingDirectory,
        string? mapperDllPath, out string? failureReason)
    {
        failureReason = null;

        if (!IsUsableLocalAbsolutePath(gameExecutablePath) || !IsUsableLocalAbsolutePath(dllPath))
        {
            failureReason = ReasonPathNotAllowed;
            return false;
        }

        // 工作目录允许为空：由调用方决定，空时由 CreateProcess 继承当前目录。
        if (!string.IsNullOrEmpty(workingDirectory) && !IsUsableLocalAbsolutePath(workingDirectory))
        {
            failureReason = ReasonPathNotAllowed;
            return false;
        }

        var exeName = Path.GetFileName(gameExecutablePath!);
        if (!AllowedGameExecutableNames.Any(n => string.Equals(n, exeName, StringComparison.OrdinalIgnoreCase)))
        {
            failureReason = ReasonPathNotAllowed;
            return false;
        }

        if (!string.Equals(Path.GetFileName(dllPath!), InjectedDllFileName, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = ReasonPathNotAllowed;
            return false;
        }

        var importerDirectory = Path.GetFileName(Path.GetDirectoryName(dllPath!));
        if (!string.Equals(importerDirectory, ImporterDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            failureReason = ReasonPathNotAllowed;
            return false;
        }

        if (!string.IsNullOrEmpty(mapperDllPath))
        {
            if (!IsUsableLocalAbsolutePath(mapperDllPath))
            {
                failureReason = ReasonPathNotAllowed;
                return false;
            }

            if (!string.Equals(Path.GetFileName(mapperDllPath), MapperDllFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                failureReason = ReasonPathNotAllowed;
                return false;
            }

            var mapperDirectory = Path.GetFileName(Path.GetDirectoryName(mapperDllPath));
            if (!string.Equals(mapperDirectory, MapperDirectoryName, StringComparison.OrdinalIgnoreCase))
            {
                failureReason = ReasonPathNotAllowed;
                return false;
            }
        }

        return true;
    }

    /// <summary>绝对路径、不是 UNC、没有空字符、长度受控。不存在性由调用方在真正启动前检查。</summary>
    private static bool IsUsableLocalAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength || path.Contains('\0'))
            return false;

        if (!Path.IsPathFullyQualified(path))
            return false;

        // UNC：\\server\share 与 \\?\UNC\...；本地扩展路径 \\?\C:\... 允许，但后面要求盘符
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && root.Length >= 2 && root[1] == ':';
    }

    private static bool TryDecodeArguments(string? encoded, out string arguments)
    {
        arguments = string.Empty;
        if (string.IsNullOrEmpty(encoded))
            return true;

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length > MaxArgumentsBytes)
                return false;

            arguments = Encoding.UTF8.GetString(bytes);

            // CreateProcess 的命令行遇到 NUL 会截断；换行没有意义，直接拒绝畸形载荷
            return !arguments.Contains('\0')
                   && !arguments.Contains('\r')
                   && !arguments.Contains('\n');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string BuildOkReply(int processId) => $"OK {processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    public static string BuildFailureReply(string reason) => $"FAIL:{reason}";

    /// <summary>
    /// 解析回复。空行/EOF/认不出的行都算 <see cref="ElevatorLaunchInjectReply.None"/>，
    /// 调用方据此区分「助手拒绝（有原因）」与「助手压根没回话（版本过旧/已退出）」。
    /// </summary>
    public static ElevatorLaunchInjectResult ParseReply(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return ElevatorLaunchInjectResult.None;

        var text = line.Trim();

        if (text.StartsWith("OK ", StringComparison.Ordinal))
        {
            return int.TryParse(text.AsSpan(3), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0
                ? new ElevatorLaunchInjectResult(ElevatorLaunchInjectReply.Ok, pid, null)
                : ElevatorLaunchInjectResult.None;
        }

        if (text.StartsWith("FAIL:", StringComparison.Ordinal))
        {
            var reason = text[5..].Trim();
            return new ElevatorLaunchInjectResult(ElevatorLaunchInjectReply.Failure, 0,
                reason.Length == 0 ? ReasonBadPayload : reason);
        }

        return ElevatorLaunchInjectResult.None;
    }

    /// <summary>把助手的失败 token 转成一句可直接展示的话；认不出的 token 原样带出。</summary>
    public static string DescribeFailure(string? reasonToken)
    {
        return reasonToken switch
        {
            ReasonBadPayload => "提权助手收到的启动请求不合法（内部错误）。",
            ReasonPathNotAllowed => "提权助手拒绝了注入：只允许启动鸣潮并注入 WWMI 的 d3d11.dll。",
            ReasonExeMissing => "找不到游戏可执行文件，无法启动。",
            ReasonDllMissing => "找不到 WWMI 的 d3d11.dll，请先完成 Mod 环境配置。",
            ReasonCreateProcess => "创建游戏进程失败（可能被安全软件拦截）。",
            ReasonInjectFailed => "注入 d3d11.dll 失败。",
            ReasonInjectTimeout => "注入超时（10 秒内没有完成）。",
            ReasonResumeFailed => "注入完成但恢复游戏主线程失败。",
            null or "" => "提权助手没有说明原因。",
            _ => $"提权助手拒绝启动：{reasonToken}"
        };
    }
}
