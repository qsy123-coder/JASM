using System.Diagnostics;
using System.Reflection;
using CommunityToolkitWrapper;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services.CommandService;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services.AppManagement.Updating;
using GIMI_ModManager.WinUI.Services.Input;
using GIMI_ModManager.WinUI.Services.ModHandling;
using GIMI_ModManager.WinUI.Services.Notifications;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.AppManagement;

public class LifeCycleService(
    ILogger logger,
    NotificationManager notificationManager,
    ILocalSettingsService localSettingsService,
    ModNotificationManager modNotificationManager,
    IWindowManagerService windowManagerService,
    UpdateChecker updateChecker,
    ModUpdateAvailableChecker modUpdateAvailableChecker,
    CommandService commandService)
{
    private readonly ILogger _logger = logger.ForContext<LifeCycleService>();
    private readonly NotificationManager _notificationManager = notificationManager;
    private readonly ILocalSettingsService _localSettingsService = localSettingsService;
    private ModNotificationManager _modNotificationManager = modNotificationManager;
    private readonly IWindowManagerService _windowManagerService = windowManagerService;
    private readonly UpdateChecker _updateChecker = updateChecker;
    private readonly ModUpdateAvailableChecker _modUpdateAvailableChecker = modUpdateAvailableChecker;
    private readonly CommandService _commandService = commandService;
    private readonly ILanguageLocalizer _localizer = App.GetService<ILanguageLocalizer>();

    /// <summary>
    /// This method will try to restart the app using the suggested method from Microsoft.
    /// </summary>
    /// <param name="args">Args to pass to the app when starting up again</param>
    /// <param name="useLegacyRestartOnError">Fallback to manually starting the app right before closing</param>
    /// <param name="notifyOnError"></param>
    /// <param name="postShutdownLogic">Shutdown logic to run after the main application shutdown logic right before restarting. Some services may have been stopped at this point</param>
    /// <returns></returns>
    public async Task RestartAsync(string args = "", bool useLegacyRestartOnError = true, bool notifyOnError = false,
        Func<Task>? postShutdownLogic = null)
    {
        await StartShutdownAsync(false);
        if (postShutdownLogic is not null)
            await postShutdownLogic();
        _logger.Debug("Restarting app with args: {Args}", args);
        var error = AppInstance.Restart(args);


        if (notifyOnError)
        {
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("LifeCycle_ErrorRestartingTitle", defaultValue: "Error restarting app"),
                useLegacyRestartOnError
                    ? string.Format(_localizer.GetLocalizedStringOrDefault("LifeCycle_TryingLegacyRestartMessage", defaultValue: "Trying legacy restart method. Reason: {0}"), error)
                    : string.Format(_localizer.GetLocalizedStringOrDefault("LifeCycle_PleaseRestartManuallyMessage", defaultValue: "Please restart manually. Reason: {0}"), error),
                TimeSpan.FromSeconds(4));
        }


        if (!useLegacyRestartOnError)
        {
            _logger.Error("Error restarting app: {Error}", error);
            return;
        }

        _logger.Warning("Error restarting app: {Error}. Falling back to legacy restart method", error);
        await Task.Delay(1);
        await LegacyRestartAsync(args).ConfigureAwait(false);
    }

    /// <summary>
    /// 以**普通权限**重新启动自己（去提权），成功就关掉当前进程。
    ///
    /// <para>
    /// 什么时候用：本进程是以管理员身份运行时。那种情况下跨完整性级别的拖拽会被 UIPI 整个掐掉
    /// （浮窗与主窗口同时只剩禁止光标、松手没反应、且不报任何错），而用户通常不知道自己是怎么
    /// 被提权的 —— 多半是 exe 属性 → 兼容性里勾了「以管理员身份运行此程序」。
    /// </para>
    ///
    /// <para>
    /// 顺序**必须是「先起新的、再退旧的」**：起不来的话用户手上还有原来那个进程，
    /// 不至于两个都没了。新旧之间的交接靠一个落盘的凭条（见 <see cref="UnelevatedRelaunchMarker"/>）——
    /// 两份的映像路径完全相同，没有凭条的话新进程会被单实例检查挡回去。
    /// </para>
    /// </summary>
    /// <returns>新进程是否已经发起（false 时当前进程照常活着，由调用方告诉用户怎么办）。</returns>
    public async Task<bool> RestartWithoutElevationAsync()
    {
        var exePath = TryResolveOwnExePath();
        if (exePath is null)
        {
            _logger.Error("去提权重启：找不到自己的 exe 路径，放弃");
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("LifeCycle_ErrorRestartingTitle", defaultValue: "Error restarting app"),
                "找不到 JASM 自己的 exe 路径，无法以普通权限重启。请手动关掉 JASM，再双击它的图标重新打开（不要用「以管理员身份运行」）。",
                TimeSpan.FromSeconds(15));
            return false;
        }

        // 凭条要**先写**：写得晚了，新的一份会先跑完单实例检查然后自己退出
        UnelevatedRelaunchMarker.Write(_logger, Environment.ProcessId);

        var result = UnelevatedLauncher.Launch(exePath, _logger);

        if (!result.Success)
        {
            // 绝不静默失败：静默失败的用户只会以为按钮坏了，然后继续对着禁止光标猜
            _logger.Error("去提权重启失败：{Detail}", result.Detail);
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("LifeCycle_ErrorRestartingTitle", defaultValue: "Error restarting app"),
                "以普通权限重启失败。请手动关掉 JASM，再双击它的图标重新打开（不要用「以管理员身份运行」）。"
                + $"原因：{result.Detail}",
                TimeSpan.FromSeconds(15));
            return false;
        }

        _logger.Information("去提权重启已发起（{Detail}）；本进程即将退出", result.Detail);

        await StartShutdownAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 本程序自己的 exe 全路径。找不到返回 <c>null</c>。
    ///
    /// <para>
    /// 顺序有讲究：<c>Assembly.GetEntryAssembly().Location</c> 在**单文件版**下是空串
    /// （程序集是从自解压目录里的元数据加载的，没有独立的磁盘文件），所以那时只能靠
    /// <see cref="Environment.ProcessPath"/> —— 它给的是用户双击的那个真实 exe。
    /// 单文件版正是我们的主分发形态，这条阶梯不能少。
    /// </para>
    /// </summary>
    private string? TryResolveOwnExePath()
    {
        var exePath = Assembly.GetEntryAssembly()!.Location;
        exePath = Path.ChangeExtension(exePath, ".exe");

        if (exePath.IsNullOrEmpty() || !File.Exists(exePath))
        {
            exePath = Environment.ProcessPath;
            exePath = Path.ChangeExtension(exePath, ".exe");
            _logger.Debug("Restarting from process path: {ExePath}", exePath);
        }

        return exePath.IsNullOrEmpty() || !File.Exists(exePath) ? null : exePath;
    }

    /// <summary>
    /// This method is used as a fallback if the new restart method fails.
    /// It will try to restart the app by starting a new process of itself and then exit the current process.
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    public async Task LegacyRestartAsync(string args = "")
    {
        var exePath = TryResolveOwnExePath();

        if (exePath is null)
        {
            _logger.Error("Unable to find own exe path. Shutting down...");
            Application.Current.Exit();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Arguments = args,

                // 不带工作目录的话子进程会继承我们的 —— 单文件版下我们的工作目录可能就是
                // 那个自解压临时目录（会被清理掉）。日志虽然走绝对路径不受影响，
                // 但让子进程继承一个随时会消失的目录没有任何好处
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty
            });
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error restarting app");
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("LifeCycle_ErrorRestartingTitle", defaultValue: "Error restarting app"), _localizer.GetLocalizedStringOrDefault("LifeCycle_PleaseRestartManuallyShortMessage", defaultValue: "Please restart manually"),
                TimeSpan.FromSeconds(4));
            await Task.Delay(TimeSpan.FromSeconds(3));
            await StartShutdownAsync();
            return;
        }


        await StartShutdownAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 另一个「同名进程」以及它的身份 —— 启动时的单实例检查据此决定把谁拉到前台、要不要拦住。
    /// </summary>
    /// <param name="IsSameApp">是不是本程序自己的另一个实例（<c>false</c> = 另一个安装的 JASM 也在跑）。</param>
    /// <param name="WindowHandle">对方的主窗口句柄（拿不到为 0）。</param>
    /// <param name="ImagePath">对方的映像全路径（读不到为 <c>null</c>）。</param>
    public readonly record struct OtherInstanceInfo(bool IsSameApp, nint WindowHandle, string? ImagePath);

    /// <summary>
    /// 找另一个同名进程，并判断它是不是**本程序自己的**另一个实例（判据见 <see cref="InstanceIdentity"/>）。
    /// 没有别的实例返回 <c>null</c>。
    ///
    /// 与 <see cref="GetOtherInstanceProcess"/> 的区别：那个只认进程名 —— <c>--switch</c> 那条路够用
    /// （要关掉的就是别处那个 JASM），但启动检查不够：别的安装的 JASM 也同名，只看名字会把**对方的**
    /// 窗口当成自己的拉到前台，用户以为启动成功了、看到的却是另一个安装的界面（2026-09-24 实测到的串台）。
    /// </summary>
    public OtherInstanceInfo? FindOtherInstance()
    {
        var otherProcess = GetOtherInstanceProcess();
        if (otherProcess is null) return null;

        try
        {
            // 读不到对方路径（进程刚退出 / 权限不足）时这里给 null，InstanceIdentity 会保守判成「自己」：
            // 退回改动前的行为（拉前台 + 退出），而不是凭一次读失败给用户弹一个他看不懂的窗口
            var otherImagePath = WindowProcessQuery.TryGetProcessImagePath((uint)otherProcess.Id);

            return new OtherInstanceInfo(InstanceIdentity.IsSameApp(Environment.ProcessPath, otherImagePath),
                otherProcess.MainWindowHandle, otherImagePath);
        }
        catch (Exception e)
        {
            // MainWindowHandle 在对方已退出的竞态下会抛。这里是启动路径，绝不能因此起不来
            _logger.Error(e, "Error identifying the other JASM instance");
            return new OtherInstanceInfo(true, 0, null);
        }
        finally
        {
            otherProcess.Dispose();
        }
    }

    public Process? GetOtherInstanceProcess()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(currentProcess.ProcessName);

            if (processes.Length <= 1) return null;

            var currentProcessId = currentProcess.Id;
            var currentProcessName = currentProcess.ProcessName;

            foreach (var process in processes)
            {
                if (process.Id == currentProcessId) continue;

                var processName = process.ProcessName;


                if (currentProcessName!.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    return process;
            }
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error checking for other instances of the app");
            return null;
        }


        return null;
    }

    public async Task StartShutdownAsync(bool shutdown = true)
    {
        App.IsShuttingDown = true;
        try
        {
            await ShutdownCleanupAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error during shutdown cleanup");
        }
        finally
        {
            App.ShutdownComplete = true;
            if (shutdown)
            {
                _logger.Debug("Shutting down application...");

                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    Application.Current.Exit();
                    App.MainWindow.Close();
                });
            }
        }
    }

    private async Task ShutdownCleanupAsync()
    {
        _logger.Debug("JASM starting shutdown cleanup...");

        if (App.OverrideShutdown)
        {
            _logger.Information("Shutdown override enabled, skipping shutdown...");
            _logger.Information("Shutdown override will be disabled in at most 1 second.");
            await Task.Run(async () =>
            {
                await Task.Delay(500);
                App.OverrideShutdown = false;
                _logger.Information("Shutdown override disabled.");
            });
            await StartShutdownAsync();
        }


        var notificationCleanupTask =
            Task.Run(async () => await _modNotificationManager.CleanupAsync().ConfigureAwait(false));

        var stopBackgroundTasks = Task.Run(() =>
        {
            _modUpdateAvailableChecker.CancelAndStop();
            _updateChecker.CancelAndStop();
            _notificationManager.CancelAndStop();
            commandService.Cleanup();
        });

        await notificationCleanupTask;

        if (DispatcherQueue.GetForCurrentThread() is not null)
        {
            await SaveWindowSettingsAsync();
            await _windowManagerService.CloseWindowsAsync().ConfigureAwait(false);
        }
        else
        {
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                await SaveWindowSettingsAsync();
                await _windowManagerService.CloseWindowsAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }


        var tmpDirCleanupTask = Task.Run(() =>
        {
            var tmpDir = new DirectoryInfo(App.TMP_DIR);
            if (tmpDir.Exists)
            {
                tmpDir.Refresh();
                _logger.Debug("Deleting temporary directory: {Path}", tmpDir.FullName);
                try
                {
                    tmpDir.EnumerateFiles("*", SearchOption.AllDirectories)
                        .ForEach(f => f.Attributes = FileAttributes.Normal);
                    tmpDir.EnumerateDirectories("*", SearchOption.AllDirectories)
                        .ForEach(d => d.Attributes = FileAttributes.Normal);
                    tmpDir.Delete(true);
                }
                catch (Exception e)
                {
                    _logger.Warning(e, "Failed to delete temporary directory: {Path}", tmpDir.FullName);
                }
            }
        });

        await stopBackgroundTasks.ConfigureAwait(false);
        await tmpDirCleanupTask.ConfigureAwait(false);


        _logger.Debug("JASM shutdown cleanup complete.");
    }

    private async Task SaveWindowSettingsAsync()
    {
        if (App.MainWindow is null || App.MainWindow.AppWindow is null)
            return;


        var windowSettings = await _localSettingsService
            .ReadOrCreateSettingAsync<ScreenSizeSettings>(ScreenSizeSettings.Key);

        if (windowSettings is { PersistWindowPosition: false, PersistWindowSize: false })
        {
            await _localSettingsService.SaveSettingAsync(ScreenSizeSettings.Key, new ScreenSizeSettings()
            {
                PersistWindowPosition = false,
                PersistWindowSize = false
            })
                .ConfigureAwait(false);
            return;
        }


        var isFullScreen = App.MainWindow.WindowState == WindowState.Maximized;

        var width = windowSettings.Width;
        var height = windowSettings.Height;
        var xPosition = windowSettings.XPosition;
        var yPosition = windowSettings.YPosition;


        if (!isFullScreen && App.MainWindow.WindowState != WindowState.Minimized)
        {
            width = (int)App.MainWindow.Width;
            height = (int)App.MainWindow.Height;
        }

        if (App.MainWindow.WindowState != WindowState.Minimized)
        {
            xPosition = App.MainWindow.AppWindow.Position.X;
            yPosition = App.MainWindow.AppWindow.Position.Y;
        }

        _logger.Debug($"Saving Window size: {width}x{height} | IsFullscreen: {isFullScreen}");

        var newWindowSettings = new ScreenSizeSettings(width, height)
        {
            IsFullScreen = isFullScreen,
            XPosition = xPosition,
            YPosition = yPosition,
            PersistWindowPosition = windowSettings.PersistWindowPosition,
            PersistWindowSize = windowSettings.PersistWindowSize
        };

        await _localSettingsService.SaveSettingAsync(ScreenSizeSettings.Key, newWindowSettings)
            .ConfigureAwait(false);
    }
}