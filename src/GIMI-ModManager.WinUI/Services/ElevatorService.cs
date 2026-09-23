using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkitWrapper;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.Input;
using Microsoft.UI.Xaml;
using Serilog;
using Windows.Win32.Foundation;

namespace GIMI_ModManager.WinUI.Services;

public partial class ElevatorService : ObservableRecipient
{
    private readonly ISkinManagerService _skinManagerService;

    /// <summary>
    /// 单 exe 版没有随包的 <c>Elevator.exe</c>，助手只能靠它从主 exe 的内嵌资源里释放出来。
    /// folder 版同目录那份就够用，它会一行都不写（见 <see cref="ElevatorProvisioning.ShouldProvision"/>）。
    /// </summary>
    private readonly ElevatorProvisioner _provisioner;

    public const string ElevatorPipeName = "MyPipess";
    public const string ElevatorProcessName = "Elevator.exe";
    private readonly ILogger _logger;
    private Task? _refreshTask;
    private readonly object _refreshLock = new();
    private readonly SemaphoreSlim _copyGate = new(1, 1);

    [ObservableProperty] private ElevatorStatus _elevatorStatus = ElevatorStatus.NotRunning;
    [ObservableProperty] private bool _canStartElevator;
    private Process? _elevatorProcess;

    /// <summary>
    /// 实际要用的那个助手的绝对路径（<see cref="Initialize"/> 里定一次）。
    /// 两个候选里选出来的：folder 版是 exe 同目录那份，单 exe 版是释放到 <c>%LOCALAPPDATA%</c> 的副本。
    /// <c>null</c> = 没有可用助手，走「启动不了」的既有分支。
    /// </summary>
    private string? _elevatorPath;

    /// <summary>
    /// <see cref="_elevatorPath"/> 那个助手的 FileVersion（<see cref="Initialize"/> 里读一次缓存），
    /// 以及「它认不认带目标窗口的刷新命令」。正在运行的 exe 无法被覆盖（文件被锁），
    /// 所以磁盘上的版本 == 正在跑的那个进程的版本。
    /// </summary>
    private string? _elevatorFileVersion;

    private bool _supportsTargetedRefresh;

    /// <summary>助手认不认送键命令 <c>"3"</c>；不认识就只能退回「请用户自己以管理员身份运行 JASM」。</summary>
    private bool _supportsKeySend;

    public string? ErrorMessage { get; private set; }

    private bool _exitHandlerRegistered;

    private bool _IsInitialized;

    public ElevatorService(ILogger logger, ISkinManagerService skinManagerService, ElevatorProvisioner provisioner)
    {
        _skinManagerService = skinManagerService;
        _provisioner = provisioner;
        _logger = logger.ForContext<ElevatorService>();
    }

    public void Initialize()
    {
        if (_IsInitialized) throw new InvalidOperationException("ElevatorService is already initialized");
        _logger.Debug("Initializing ElevatorService");

        // 候选一：随包安装的同目录助手（folder 版 / 开发机）
        string? siblingPath = null;
        string? siblingVersion = null;
        var siblingCandidate = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ElevatorProcessName);
        if (Path.Exists(siblingCandidate))
        {
            siblingPath = siblingCandidate;
            siblingVersion = ReadElevatorFileVersion(siblingCandidate);
        }

        // 候选二：从主 exe 的内嵌资源释放到 %LOCALAPPDATA% 的副本（单 exe 版的唯一来源）。
        // 把同目录那份的版本传进去：它已经够用时这一步一行都不会写盘。
        var extractedPath = _provisioner.EnsureProvisioned(siblingVersion);
        var extractedVersion = extractedPath is null ? null : ReadElevatorFileVersion(extractedPath);

        // 取版本高的那个。**不能**简单地「同目录优先」：单 exe 用户的目录里可能残留着旧 folder 安装
        // 留下的 1.0.0.0 助手，那样会把刚从内嵌资源写出来的、能用的新助手盖掉（理由见 Select 的注释）。
        (_elevatorPath, _elevatorFileVersion) =
            ElevatorProvisioning.Select(siblingPath, siblingVersion, extractedPath, extractedVersion);

        if (_elevatorPath is not null)
        {
            _logger.Debug(ElevatorProcessName + " found at: " + _elevatorPath);
            _supportsTargetedRefresh = ElevatorRefreshProtocol.SupportsTargetedRefresh(_elevatorFileVersion);
            _supportsKeySend = ElevatorKeySendProtocol.SupportsKeySend(_elevatorFileVersion);
            _logger.Information(
                "[ElevatorService] {ProcessName} FileVersion={FileVersion}（路径 {Path}），支持带目标窗口的刷新={SupportsTargetedRefresh}，"
                + "支持代发按键={SupportsKeySend}",
                ElevatorProcessName, _elevatorFileVersion ?? "(读不到)", _elevatorPath, _supportsTargetedRefresh,
                _supportsKeySend);
            App.MainWindow.DispatcherQueue.TryEnqueue(() => CanStartElevator = true);
            _IsInitialized = true;
            return;
        }

        _logger.Warning("Elevator.exe not found");
        ErrorMessage = "Elevator.exe not found";
        ElevatorStatus = ElevatorStatus.InitializingFailed;
        App.MainWindow.DispatcherQueue.TryEnqueue(() => CanStartElevator = false);
    }

    /// <summary>
    /// 读 Elevator.exe 的 FileVersion。读不到（没有版本信息 / 打不开文件）返回 null ——
    /// 调用方 <see cref="ElevatorRefreshProtocol.SupportsTargetedRefresh"/> 按「旧版」处理，即保守地不改既有行为。
    /// </summary>
    private string? ReadElevatorFileVersion(string elevatorPath)
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(elevatorPath).FileVersion;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(e, "[ElevatorService] 读 {Path} 的版本信息失败", elevatorPath);
            return null;
        }
    }

    public bool StartElevator()
    {
        if (_elevatorProcess is { HasExited: false })
        {
            _logger.Information("Elevator.exe is already running");
            return false;
        }

        // 绝对路径是必需的：UseShellExecute=true 时裸名靠 shell / CWD 解析，而助手现在可能住在
        // %LOCALAPPDATA%\JASM（只有 folder 版才在 exe 同目录）。WorkingDirectory 同理 —— 不设的话
        // 会把调用方的当前目录传染给提权进程。
        if (_elevatorPath is null)
        {
            _logger.Error("[ElevatorService] 没有可用的助手路径，无法启动 " + ElevatorProcessName);
            App.MainWindow.DispatcherQueue.TryEnqueue(() => ElevatorStatus = ElevatorStatus.InitializingFailed);
            ErrorMessage = "Elevator.exe not found";
            return false;
        }

        var currentUser = WindowsIdentity.GetCurrent().Name;
        currentUser = currentUser.Split("\\").LastOrDefault() ?? currentUser;

        _elevatorProcess = Process.Start(new ProcessStartInfo(_elevatorPath)
        {
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(_elevatorPath) ?? string.Empty,
            Verb = "runas",
            ArgumentList = { currentUser }
        });

        if (_elevatorProcess == null || _elevatorProcess.HasExited)
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() => ElevatorStatus = ElevatorStatus.InitializingFailed);
            ErrorMessage = "Failed to start Elevator.exe";
            _logger.Error("Failed to start Elevator.exe");
            return false;
        }

        App.MainWindow.DispatcherQueue.TryEnqueue(() => ElevatorStatus = ElevatorStatus.Running);
        ;

        _elevatorProcess.Exited += (sender, args) =>
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                ElevatorStatus = ElevatorStatus.NotRunning;
                CanStartElevator = true;
            });
            _logger.Information("Elevator.exe exited with exit code: {ExitCode}", _elevatorProcess.ExitCode);
        };

        App.MainWindow.DispatcherQueue.TryEnqueue(() => CanStartElevator = false);

        if (_exitHandlerRegistered) return true;


        App.MainWindow.Closed += MainWindowExitHandler;
        _exitHandlerRegistered = true;
        return true;
    }


    private void MainWindowExitHandler(object sender, WindowEventArgs args)
    {
        if (_elevatorProcess is { HasExited: false })
        {
            _logger.Information("Killing Elevator.exe");
            _elevatorProcess.Kill();
            _logger.Debug("Elevator.exe killed");
        }
    }

    public Task RefreshGenshinMods()
    {
        if (_elevatorProcess is null || _elevatorProcess.HasExited)
        {
            _logger.Debug("Elevator.exe is not running");
            return Task.CompletedTask;
        }

        // 目标窗口在这里解析、并**同步**切到前台（不 await、也不丢进下面的 Task.Run）：
        // 前台身份属于「刚点了 / 按了 JASM 的那个进程」，一旦让出就可能易主；而助手是后台进程，
        // 前台锁与提权无关，它自己抢不到 —— 理由与现场签名见 ForegroundWindowActivator。
        // 解析不出来就照旧退回历史命令 "0"：老路径下原神照旧能刷新，
        // 不能因为另一个游戏没配好就把它一起弄丢。
        var targetWindow = ResolveTargetWindow(out var failureReason);
        if (targetWindow is { } window)
        {
            ForegroundWindowActivator.Activate(window, handOverRightToSetForeground: true, _logger);
        }
        else
        {
            _logger.Warning("[ElevatorService] {Reason}；退回历史命令 {Command}（只能刷新助手内部写死的原神）",
                failureReason, ElevatorRefreshProtocol.LegacyRefreshCommand);
        }

        lock (_refreshLock)
        {
            if (_refreshTask is { IsCompleted: false })
            {
                return _refreshTask;
            }

            _refreshTask = Task.Run(async () =>
            {
                await InternalRefreshGenshinMods(targetWindow).ConfigureAwait(false);
                await Task.Delay(500).ConfigureAwait(false); // Debounce
            });

            return _refreshTask;
        }
    }

    private FileSystemWatcher? _userIniWatcher;

    private TaskCompletionSource<bool>? _userIniChangedTcs;

    public async Task RefreshAndWaitForUserIniChangesAsync()
    {
        var userIniPath = Path.Combine(_skinManagerService.ThreeMigotoRootfolder, Constants.UserIniFileName);
        if (!Directory.Exists(_skinManagerService.ThreeMigotoRootfolder) || !File.Exists(userIniPath))
        {
            await RefreshGenshinMods().ConfigureAwait(false);
            return;
        }

        _userIniChangedTcs = new TaskCompletionSource<bool>();

        if (_userIniWatcher is null)
        {
            _userIniWatcher ??= new FileSystemWatcher
            {
                Path = _skinManagerService.ThreeMigotoRootfolder,
                Filter = Constants.UserIniFileName,
                NotifyFilter = NotifyFilters.LastWrite
            };

            _userIniWatcher.Changed += (sender, args) =>
            {
                if (_userIniChangedTcs is { Task: { IsCompleted: false } })
                    _userIniChangedTcs.TrySetResult(true);
            };
        }

        try
        {
            _userIniWatcher.EnableRaisingEvents = true;

            // Create timeout
            _ = Task.Delay(5000).ContinueWith(task =>
            {
                if (_userIniChangedTcs is { Task: { IsCompleted: false } })
                    _userIniChangedTcs.TrySetResult(true);
            });


            await RefreshGenshinMods().ConfigureAwait(false);

            await _userIniChangedTcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _userIniWatcher.EnableRaisingEvents = false;
        }
    }

    /// <summary>
    /// 刷新当前游戏（模拟 F10）。名字沿用历史命名，实现已从「只刷原神」扩成「刷 ini 里配的那个游戏」；
    /// 公开 API 的整体改名（约 12 个文件）单独一次做。
    ///
    /// <paramref name="targetWindow"/> 是调用方 <see cref="RefreshGenshinMods"/> 解析好的目标窗口
    /// （它同时已经把这个窗口切到前台了）。为 null 表示没解析出来 —— 那就退回历史命令 <c>"0"</c>
    /// （助手内部写死原神、不回执），老路径下原神照旧能刷新。
    /// </summary>
    private async Task InternalRefreshGenshinMods(HWND? targetWindow)
    {
        var commandSent = false;

        try
        {
            if (targetWindow is { } window)
            {
                await SendTargetedRefreshAsync(window).ConfigureAwait(false);
                commandSent = true;
            }
            else
            {
                commandSent = await RefreshLegacyAsync().ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or TimeoutException)
        {
            // 服务端每轮连接都会重建管道实例，两次之间有一段没有实例的空窗，
            // 此时 ConnectAsync 抛的是 FileNotFoundException（IOException 的子类）而不是 TimeoutException。
            // 原来只 catch TimeoutException，于是刷新任务 fault，被上层显示成「应用预设失败」——是假失败。
            _logger.Warning(e, "[ElevatorService] 刷新失败：连不上 {ProcessName} 或它中途断开", ElevatorProcessName);
        }

        // 连都没连上就别抢用户的焦点
        if (!commandSent) return;

        // 刷新完把焦点还给 JASM。**必须排在读完回执之后**：这次 activate（自身还延迟 500ms）
        // 若与助手的前台校验重叠，助手回读到的前台窗口就是 JASM，会把本来能刷新的场景判成「没抢到前台」而拒发 F10。
        App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
        {
            await Task.Delay(500);
            App.MainWindow.SetForegroundWindow();
            App.MainWindow.Activate();
        });
    }

    /// <summary>
    /// 历史刷新命令 <c>"0"</c>：给版本过旧的助手用，单向无回执，助手内部写死目标（原神）。
    /// 返回「命令是否已经写进管道」。
    /// </summary>
    private async Task<bool> RefreshLegacyAsync()
    {
        await using var pipeClient = new NamedPipeClientStream(".", ElevatorPipeName, PipeDirection.Out);
        await pipeClient.ConnectAsync(TimeSpan.FromSeconds(5), default);
        await using var writer = new StreamWriter(pipeClient);
        _logger.Debug("Sending command: {Command}", ElevatorRefreshProtocol.LegacyRefreshCommand);
        await writer.WriteLineAsync(ElevatorRefreshProtocol.LegacyRefreshCommand);
        await writer.FlushAsync();
        _logger.Debug("Done");
        return true;
    }

    /// <summary>
    /// 带目标窗口的刷新命令 <c>"2"</c>：助手负责（再）还原窗口 → 回读校验 → 发 F10，并回执结果。
    ///
    /// 切前台**已经由调用方在同步段里做完**，并把这次的权利让了出去；助手是后台进程，
    /// 自己抢不到（理由见 <see cref="ForegroundWindowActivator"/>），它这里的
    /// <c>SetForegroundWindow</c> 只是白捡的一手，真正的判定仍以回读为准。
    ///
    /// 目标窗口（进程名 → 句柄）由调用方解析后传进来，不交给助手：只有主程序知道 d3dx.ini 在哪，
    /// 而且窗口必须用 EnumWindows 现找（<c>Process.MainWindowHandle</c> 首次访问即缓存，
    /// 游戏重建窗口后就陈旧了）。
    ///
    /// 回执**返回**给调用方（日志照旧在这里记）：调用方按自己的语义用它 ——
    /// 原神那条刷新路只关心"命令送出去没有"，回执读不到也不算它失败。
    /// （浮窗的「勾选即刷新」已改走 <c>GameKeySender</c>，不再经过这里。）
    /// </summary>
    private async Task<(ElevatorRefreshReply Reply, string? ReasonToken)> SendTargetedRefreshAsync(HWND window)
    {
        await using var pipeClient = new NamedPipeClientStream(".", ElevatorPipeName, PipeDirection.InOut);
        await pipeClient.ConnectAsync(TimeSpan.FromSeconds(5), default).ConfigureAwait(false);

        using var reader = new StreamReader(pipeClient);
        await using var writer = new StreamWriter(pipeClient) { AutoFlush = true };

        _logger.Debug("[ElevatorService] 发送带目标的刷新命令 {Command}，目标窗口 {Window}",
            ElevatorRefreshProtocol.TargetedRefreshCommand, WindowProcessQuery.FormatWindow(window));

        // HWND → nint 走的是 CsWin32 生成的 implicit operator IntPtr，所以这里不用 unsafe
        foreach (var line in ElevatorRefreshProtocol.BuildTargetedRefreshPayload(window))
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }

        // 到这里命令已经送出去了：即使回执读不到，也按「刷新已触发」把焦点还给 JASM
        return await ReadRefreshReplyAsync(reader).ConfigureAwait(false);
    }

    /// <summary>
    /// 等助手的回执、记日志，并把它返回给调用方。读超时只记 Warning、不往外抛 ——
    /// 一来旧版助手收到新命令是**静默无视**（不回话也不断开），不设超时就会一直卡住；
    /// 二来 <c>_refreshTask</c> 一旦卡在未完成状态，之后每次刷新都只会复用那个卡死的 task。
    /// </summary>
    private async Task<(ElevatorRefreshReply Reply, string? ReasonToken)> ReadRefreshReplyAsync(StreamReader reader)
    {
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        string? reply;
        try
        {
            reply = await reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("[ElevatorService] {ProcessName} 没有在 {Seconds}s 内回执刷新命令 {Command}；"
                            + "若游戏没有刷新，说明它可能版本过旧，更新 JASM 即可（助手随主 exe 一起更新）",
                ElevatorProcessName, 3, ElevatorRefreshProtocol.TargetedRefreshCommand);
            return (ElevatorRefreshReply.None, null);
        }

        var parsed = ElevatorRefreshProtocol.ParseReply(reply, out var failureReason);

        switch (parsed)
        {
            case ElevatorRefreshReply.Ok:
                _logger.Debug("[ElevatorService] {ProcessName} 已把目标切到前台并发出 F10", ElevatorProcessName);
                break;
            case ElevatorRefreshReply.Failure:
                _logger.Warning("[ElevatorService] {ProcessName} 拒绝发送 F10：{Reason}",
                    ElevatorProcessName, failureReason ?? "(未说明原因)");
                break;
            default:
                _logger.Warning("[ElevatorService] {ProcessName} 没有回执刷新命令 {Command}（可能版本过旧或已退出）",
                    ElevatorProcessName, ElevatorRefreshProtocol.TargetedRefreshCommand);
                break;
        }

        return (parsed, failureReason);
    }

    /// <summary>
    /// 解析「这次要刷新的游戏窗口」：d3dx.ini 里的 target 进程名 → 进程 → 可见顶层窗口。
    /// 任何一步失败都返回 null 并写出 <paramref name="failureReason"/>（日志要能一眼看出卡在哪一步）。
    ///
    /// 路径取自 <see cref="ISkinManagerService"/> 而不是 <c>ModManagerOptions</c>：
    /// 这个服务本来就持有它，不必为一个只读路径再往构造函数加依赖。
    /// </summary>
    private HWND? ResolveTargetWindow(out string failureReason)
    {
        var gimiRootFolderPath = _skinManagerService.ThreeMigotoRootfolder;
        var modsFolderPath = _skinManagerService.ActiveModsFolderPath;

        var iniPath = D3dxIniTargetResolver.ResolveD3dxIniPath(gimiRootFolderPath, modsFolderPath);
        if (iniPath is null)
        {
            failureReason = $"找不到 {D3dxIniTargetResolver.D3dxIniFileName}"
                            + $"（GIMI 根目录={gimiRootFolderPath}，Mods 目录={modsFolderPath}）";
            return null;
        }

        var processName = D3dxIniTargetResolver.ReadTargetProcessName(iniPath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            failureReason = $"{iniPath} 里没有可用的 target";
            return null;
        }

        var processIds = WindowProcessQuery.GetProcessIds(processName);
        if (processIds.Length == 0)
        {
            failureReason = $"目标进程 {processName} 没有运行";
            return null;
        }

        var gameWindow = WindowProcessQuery.FindGameWindow(processIds);
        if (gameWindow.IsNull)
        {
            failureReason = $"目标进程 {processName} 没有可见的顶层窗口";
            return null;
        }

        failureReason = string.Empty;
        return gameWindow;
    }

    /// <summary>
    /// Requests an elevated recursive copy from <paramref name="sourceDir"/> to <paramref name="targetDir"/>
    /// via the Elevator process (UAC prompt shown when it is not already running).
    /// Returns true when the elevated copy reported success.
    /// </summary>
    public async Task<bool> CopyDirectoryAsync(string sourceDir, string targetDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || string.IsNullOrWhiteSpace(targetDir))
            return false;

        await _copyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CopyDirectoryCoreAsync(sourceDir, targetDir, ct).ConfigureAwait(false);
        }
        finally
        {
            _copyGate.Release();
        }
    }

    private async Task<bool> CopyDirectoryCoreAsync(string sourceDir, string targetDir, CancellationToken ct)
    {
        // Make sure an elevated Elevator.exe is running (shows the UAC prompt when it is not).
        if (_elevatorProcess is not { HasExited: false })
        {
            if (!CanStartElevator)
            {
                _logger.Warning("Elevator.exe unavailable, cannot perform elevated copy");
                return false;
            }

            try
            {
                if (!StartElevator()) return false;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to start Elevator.exe for elevated copy");
                return false;
            }
        }

        try
        {
            await using var pipeClient = new NamedPipeClientStream(".", ElevatorPipeName, PipeDirection.InOut);
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await pipeClient.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipeClient);
            await using var writer = new StreamWriter(pipeClient) { AutoFlush = true };

            await writer.WriteLineAsync("1".AsMemory(), linkedCts.Token).ConfigureAwait(false);
            await writer.WriteLineAsync(sourceDir.AsMemory(), linkedCts.Token).ConfigureAwait(false);
            await writer.WriteLineAsync(targetDir.AsMemory(), linkedCts.Token).ConfigureAwait(false);

            var response = await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);
            var ok = string.Equals(response, "OK", StringComparison.Ordinal);
            if (!ok)
                _logger.Error("Elevated copy returned: {Response}", response);
            return ok;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Elevated copy {Src} -> {Dst} failed", sourceDir, targetDir);
            return false;
        }
    }

    /// <summary>
    /// 请提权助手代发一个按键和弦 —— 游戏以管理员身份运行时**唯一**能送进按键的路径。
    ///
    /// 为什么非助手不可：UIPI 会把「中 → 高」的 <c>SendInput</c> 静默丢弃，主程序自己发
    /// 拿到的返回值是成功的、游戏却收不到（见 <see cref="ElevatorKeySendProtocol"/> 的类注释）。
    ///
    /// **但抢前台不归助手管**：前台锁只认「自己就是前台进程 / 最近收到输入的那个进程」，与提权
    /// 与否无关，助手是个后台进程，它自己抢是抢不到的（回执就是 <c>not-foreground</c>）。
    /// 所以切前台由调用方 <c>GameKeySender</c> 在同步段里做完，并用 <c>AllowSetForegroundWindow</c>
    /// 把这一次的权利让出来；助手这边只负责「发之前回读校验」，校验不过就拒发。
    ///
    /// 助手没在跑就**先把它拉起来**，那一次会弹 UAC；之后整个会话复用同一个提权进程。
    /// 三种结果的含义见 <see cref="ElevatedKeySendResult"/>，调用方据此决定给用户看什么。
    ///
    /// 可见性是 <c>internal</c> 而不是 <c>public</c>：参数里的 <c>HWND</c> 是 CsWin32 生成的 internal 类型，
    /// 放进 public 签名会直接 CS0051。唯一调用方 <c>GameKeySender</c> 在同一程序集里，internal 足够。
    /// </summary>
    internal async Task<ElevatedKeySendOutcome> TrySendKeyChordAsync(ushort virtualKey,
        IReadOnlyList<ushort> modifierKeyCodes, HWND targetWindow, CancellationToken ct = default)
    {
        // 先分辨「没拿到助手」与「版本旧」—— 两者都是叫用户更新，但日志里必须能一眼区分，
        // 否则用户报「送不了键」时无从查起。
        if (_elevatorFileVersion is null)
        {
            // 走到这里只有一种可能：同目录没有助手（单 exe 版），且内嵌副本也没能释放出来
            //（没有内嵌资源，或写盘被锁 / 被拦）。路径不写进用户可见文案，只记日志。
            _logger.Warning("[ElevatorService] 没有可用助手：同目录无 Elevator.exe，内嵌副本也没能释放到 {Path}，无法代发按键",
                ElevatorProvisioner.ProvisionedHelperPath);
            return ElevatedKeySendOutcome.Unavailable("提权助手不可用，按键没有发送。请更新 JASM 后重试。");
        }

        if (!_supportsKeySend)
        {
            _logger.Warning("[ElevatorService] 助手 FileVersion={Version} 低于 {Minimum}，不认识送键命令 {Command}",
                _elevatorFileVersion, ElevatorKeySendProtocol.MinimumFileVersionForKeySend,
                ElevatorKeySendProtocol.SendKeyCommand);
            return ElevatedKeySendOutcome.Unavailable(
                $"提权助手版本过旧（{_elevatorFileVersion}），不认识送键命令。请更新 JASM 后重试。");
        }

        if (_elevatorProcess is not { HasExited: false })
        {
            if (!CanStartElevator)
            {
                _logger.Warning("[ElevatorService] 提权助手不可用（起不来），无法代发按键");
                return ElevatedKeySendOutcome.Unavailable("提权助手不可用，按键没有发送。");
            }

            try
            {
                StartElevator();
            }
            catch (Exception ex)
            {
                // 用户在 UAC 上点「否」也走这里（Win32Exception: 操作已被用户取消）——
                // 那是选择不是故障，日志按 Information 记，别在用户日志里留下像是崩溃的东西
                _logger.Information(ex, "[ElevatorService] 启动提权助手被取消或失败，无法代发按键");
                return ElevatedKeySendOutcome.Unavailable("启动提权助手被取消，按键没有发送。");
            }

            // StartElevator 的返回值不可信（「本来就在跑」同样返回 false），以进程状态为准
            if (_elevatorProcess is not { HasExited: false })
            {
                _logger.Warning("[ElevatorService] 提权助手没能起来，无法代发按键");
                return ElevatedKeySendOutcome.Unavailable("提权助手没能启动，按键没有发送。");
            }
        }

        try
        {
            await using var pipeClient = new NamedPipeClientStream(".", ElevatorPipeName, PipeDirection.InOut);

            // 助手是单连接串行的：它正忙于复制目录时，这里的 Connect 会一直等，
            // 所以要设超时。15s 已经远超正常送键所需（还原窗口 + 500ms 前台轮询 + 300ms 稳定 + 80ms 按住）。
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            await pipeClient.ConnectAsync(linkedCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipeClient);
            await using var writer = new StreamWriter(pipeClient) { AutoFlush = true };

            // HWND → nint 走 CsWin32 生成的隐式转换，所以这里不用 unsafe
            foreach (var line in ElevatorKeySendProtocol.BuildSendKeyPayload(virtualKey, modifierKeyCodes,
                         targetWindow))
            {
                await writer.WriteLineAsync(line.AsMemory(), linkedCts.Token).ConfigureAwait(false);
            }

            var reply = await reader.ReadLineAsync(linkedCts.Token).ConfigureAwait(false);

            switch (ElevatorKeySendProtocol.ParseReply(reply, out var failureReason))
            {
                case ElevatorKeySendReply.Ok:
                    _logger.Information(
                        "[ElevatorService] 提权助手已代发按键 vk=0x{VirtualKey:X2} mods=[{Modifiers}] 目标窗口={Window}",
                        virtualKey, string.Join(",", modifierKeyCodes),
                        WindowProcessQuery.FormatWindow(targetWindow));
                    return ElevatedKeySendOutcome.Sent;

                case ElevatorKeySendReply.Failure:
                    // 原因 token 原样记日志（将来助手加了原因，这里不用改），给用户的文案单独映射
                    _logger.Warning("[ElevatorService] 提权助手拒绝代发按键：{Reason}", failureReason);
                    return ElevatedKeySendOutcome.Failed(
                        ElevatorKeySendProtocol.DescribeFailure(failureReason));

                default:
                    _logger.Warning(
                        "[ElevatorService] 提权助手没有回执送键命令 {Command}（可能版本过旧或已退出）",
                        ElevatorKeySendProtocol.SendKeyCommand);
                    return ElevatedKeySendOutcome.Unavailable("提权助手没有响应送键请求，请重试。");
            }
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
        {
            _logger.Warning(ex, "[ElevatorService] 请提权助手代发按键失败");
            return ElevatedKeySendOutcome.Unavailable("连不上提权助手，按键没有发送。");
        }
    }

    public ElevatorStatus CheckStatus()
    {
        ElevatorStatus = _elevatorProcess is { HasExited: false } ? ElevatorStatus.Running : ElevatorStatus.NotRunning;
        return ElevatorStatus;
    }
}

public enum ElevatorStatus
{
    InitializingFailed = -1,
    NotRunning = 0,
    Running
}

/// <summary>请提权助手代发按键的结果。</summary>
public enum ElevatedKeySendResult
{
    /// <summary>助手已把按键发出 —— 这是唯一不需要打扰用户的结局。</summary>
    Sent,

    /// <summary>
    /// 助手这条路压根走不通（没随包安装 / 版本过旧 / 拉不起来）。
    /// 调用方据此退回既有行为：提示用户自己以管理员身份运行 JASM。
    /// </summary>
    Unavailable,

    /// <summary>助手在跑，但拒发或没回话。原因见 <see cref="ElevatedKeySendOutcome.Message"/>。</summary>
    Failed
}

/// <summary>
/// 代发按键的结果。<see cref="Message"/> 是**给用户看**的一句话，只有失败时才有值 ——
/// 失败原因是助手回执里的动态 token（没抢到前台 / 被反作弊拦下…），一句写死的文案盖不住，
/// 而每种原因对应的下一步动作并不相同（「点一下游戏画面再试」≠「更新 JASM」）。
/// </summary>
public readonly record struct ElevatedKeySendOutcome(ElevatedKeySendResult Result, string? Message)
{
    public static ElevatedKeySendOutcome Sent { get; } = new(ElevatedKeySendResult.Sent, null);

    public static ElevatedKeySendOutcome Unavailable(string message)
        => new(ElevatedKeySendResult.Unavailable, message);

    public static ElevatedKeySendOutcome Failed(string message)
        => new(ElevatedKeySendResult.Failed, message);
}