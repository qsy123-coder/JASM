using System.Security.Principal;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using CommunityToolkitWrapper;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Activation;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Options;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.AppManagement.Updating;
using GIMI_ModManager.WinUI.Services.ModHandling;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.Services.Overlay;
using GIMI_ModManager.WinUI.Views;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace GIMI_ModManager.WinUI.Services;

public class ActivationService : IActivationService
{
    private readonly NotificationManager _notificationManager;
    private readonly ISkinManagerService _skinManagerService;
    private readonly INavigationViewService _navigationViewService;
    private readonly ActivationHandler<LaunchActivatedEventArgs> _defaultHandler;
    private readonly IEnumerable<IActivationHandler> _activationHandlers;
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly ILogger _logger;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly IGameService _gameService;
    private readonly ILanguageLocalizer _languageLocalizer;
    private readonly ElevatorService _elevatorService;
    private readonly GenshinProcessManager _genshinProcessManager;
    private readonly ThreeDMigtoProcessManager _threeDMigtoProcessManager;
    private readonly UpdateChecker _updateChecker;
    private readonly IWindowManagerService _windowManagerService;
    private readonly AutoUpdaterService _autoUpdaterService;
    private readonly SelectedGameService _selectedGameService;
    private readonly ModUpdateAvailableChecker _modUpdateAvailableChecker;
    private readonly ModNotificationManager _modNotificationManager;
    private readonly LifeCycleService _lifeCycleService;
    private UIElement? _shell = null;

    private readonly string[] _args = Environment.GetCommandLineArgs().Skip(1).ToArray();

    public ActivationService(ActivationHandler<LaunchActivatedEventArgs> defaultHandler,
        IEnumerable<IActivationHandler> activationHandlers, IThemeSelectorService themeSelectorService,
        ILocalSettingsService localSettingsService,
        ElevatorService elevatorService, GenshinProcessManager genshinProcessManager,
        ThreeDMigtoProcessManager threeDMigtoProcessManager, UpdateChecker updateChecker,
        IWindowManagerService windowManagerService, AutoUpdaterService autoUpdaterService, IGameService gameService,
        ILanguageLocalizer languageLocalizer, SelectedGameService selectedGameService,
        ModUpdateAvailableChecker modUpdateAvailableChecker, ILogger logger,
        ModNotificationManager modNotificationManager, INavigationViewService navigationViewService,
        ISkinManagerService skinManagerService, NotificationManager notificationManager,
        LifeCycleService lifeCycleService)
    {
        _defaultHandler = defaultHandler;
        _activationHandlers = activationHandlers;
        _themeSelectorService = themeSelectorService;
        _localSettingsService = localSettingsService;
        _elevatorService = elevatorService;
        _genshinProcessManager = genshinProcessManager;
        _threeDMigtoProcessManager = threeDMigtoProcessManager;
        _updateChecker = updateChecker;
        _windowManagerService = windowManagerService;
        _autoUpdaterService = autoUpdaterService;
        _gameService = gameService;
        _languageLocalizer = languageLocalizer;
        _selectedGameService = selectedGameService;
        _modUpdateAvailableChecker = modUpdateAvailableChecker;
        _modNotificationManager = modNotificationManager;
        _navigationViewService = navigationViewService;
        _skinManagerService = skinManagerService;
        _notificationManager = notificationManager;
        _lifeCycleService = lifeCycleService;
        _logger = logger.ForContext<ActivationService>();
    }

    public async Task ActivateAsync(object activationArgs)
    {
#if DEBUG
        _logger.Information("JASM starting up in DEBUG mode...");
#elif RELEASE
        _logger.Information("JASM starting up in RELEASE mode...");
#endif

        // 启动就把"这一份进程到底提没提权"记进日志。
        //
        // 提到 Information 的理由：提权会让**跨完整性级别的拖放**在 UIPI 那层被整个掐掉
        // （Explorer 与它的完整性级别不同），表现是浮窗与主窗口的拖放**同时**只剩禁止光标、
        // 松手没反应、且不抛任何异常 —— 收用户日志时，这一行是唯一能一眼排除或坐实它的证据。
        // 原本这个判定只用在启动弹窗上，而用户勾了「不再显示此警告」之后它就彻底无声了。
        _logger.Information("是否以管理员身份运行：{IsElevated}（提权时拖拽安装不可用）",
            IsRunningAsAdministrator());

        await HandleLaunchArgsAsync();

        // Check if there is another instance of JASM running
        await CheckIfAlreadyRunningAsync();

        // Execute tasks before activation.
        await InitializeAsync();

        // Set the MainWindow Content.
        if (App.MainWindow.Content == null)
        {
            _shell = App.GetService<ShellPage>();
            App.MainWindow.Content = _shell ?? new Frame();
        }

        // Handle activation via ActivationHandlers.
        await HandleActivationAsync(activationArgs);

        // Activate the MainWindow.
        App.MainWindow.Activate();

        // Set MainWindow Cleanup on Close.
        App.MainWindow.Closed += OnApplicationExit;

        // Execute tasks after activation.
        await StartupAsync();

        // Show popups
        ShowStartupPopups();
    }

    private async Task CheckIfAlreadyRunningAsync()
    {
        // 去提权重启的交接**必须排在单实例检查前面**：新的一份与正在退出的那一份映像路径完全相同、
        // 命令行也相同，不先认这一手，下面就会把新进程当成「已经有一个 JASM 在跑」而把它挡回去 ——
        // 用户看到的是「点了重启，什么都没发生」。见 UnelevatedRelaunchProtocol。
        if (await TryHandOverFromPredecessorAsync())
            return;

        var otherInstance = _lifeCycleService.FindOtherInstance();

        if (otherInstance is null) return;

        var instance = otherInstance.Value;

        if (!instance.IsSameApp)
        {
            // 另一个安装的 JASM 也在跑（进程名同样是 JASM，但 exe 不是本程序这一份）。
            // **不**把对方的窗口拉到前台 —— 那正是用户报的毛病：以为启动成功了，看到的却是另一个
            // 安装的界面。但也不能放行：两份 JASM 共用 %LOCALAPPDATA%\JASM（设置、Mod 环境备份、
            // 提权助手都在那儿），同时跑会互相覆盖。所以拦住，并把"是谁在跑"讲清楚。
            _logger.Warning(
                "Another JASM installation is already running, refusing to start: {OtherImagePath} (this instance: {OwnImagePath})",
                instance.ImagePath, Environment.ProcessPath);

            PInvoke.MessageBox(HWND.Null,
                $"另一个 JASM 正在运行（不是本程序这一份）：\n{instance.ImagePath}\n\n"
                + "两份 JASM 共用同一份数据目录（%LOCALAPPDATA%\\JASM），同时运行会互相覆盖设置与 Mod 环境备份，"
                + "所以本程序无法启动。\n请先关闭上面那个 JASM，再启动本程序。\n\n"
                + "Another JASM installation is already running:\n"
                + $"{instance.ImagePath}\n\n"
                + "Both installations share the same data folder (%LOCALAPPDATA%\\JASM), so they cannot run at the same time. "
                + "Please close the other JASM first.",
                "JASM",
                MESSAGEBOX_STYLE.MB_ICONWARNING | MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_SETFOREGROUND);

            Application.Current.Exit();
            await Task.Delay(-1);
            return;
        }

        var hWnd = new HWND(instance.WindowHandle);

        _logger.Information("JASM is already running, exiting...");
        try
        {
            PInvoke.ShowWindow(hWnd, SHOW_WINDOW_CMD.SW_RESTORE);
            PInvoke.SetWindowPos(hWnd, new HWND(IntPtr.Zero), 0, 0, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
            PInvoke.SetForegroundWindow(hWnd);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Could not bring JASM to foreground");
            return;
        }

        Application.Current.Exit();
        await Task.Delay(-1);
    }


    /// <summary>
    /// 去提权重启的第二半：认领前任留下的交接凭条，等它退出再继续启动。
    ///
    /// <para>
    /// 认到了就**不再走单实例检查**（它们要处理的是同一件事，凭条这边知道得更准：它带着前任的 pid）。
    /// 等不到前任退出（超时）时不硬着头皮继续：两份 JASM 共用 <c>%LOCALAPPDATA%\JASM</c>
    /// （设置、Mod 环境备份、提权助手），同时跑会互相覆盖 —— 那是单实例检查存在的全部意义，
    /// 不能为了这一次重启把它丢掉。这时把本进程退掉，用户手上还剩原来那个提权的实例，
    /// 至少不是「两个半成品」。
    /// </para>
    /// </summary>
    private async Task<bool> TryHandOverFromPredecessorAsync()
    {
        if (UnelevatedRelaunchMarker.Consume(_logger) is not { } predecessorProcessId)
            return false;

        _logger.Information("[去提权] 认到交接凭条，等前一份（pid={PredecessorProcessId}）退出再继续启动",
            predecessorProcessId);

        if (await UnelevatedRelaunchMarker.WaitForExitAsync(predecessorProcessId, _logger))
        {
            _logger.Information("[去提权] 交接完成；本进程完整性={Integrity}",
                UnelevatedLauncher.DescribeIntegrity((uint)Environment.ProcessId));
            return true;
        }

        PInvoke.MessageBox(HWND.Null,
            "以普通权限重启时，上一个 JASM 没有在预期时间内退出，为避免两份 JASM 同时修改同一份数据，"
            + "本次启动已取消。\n\n请手动关掉 JASM（如果它还开着），再双击图标重新打开。",
            "JASM",
            MESSAGEBOX_STYLE.MB_ICONWARNING | MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_SETFOREGROUND);

        Application.Current.Exit();
        await Task.Delay(-1);
        return true;
    }


    private async Task HandleActivationAsync(object activationArgs)
    {
        var activationHandler = _activationHandlers.FirstOrDefault(h => h.CanHandle(activationArgs));


        if (activationHandler is not null)
        {
            _logger.Debug("Handling activation: {ActivationName}",
                activationHandler?.ActivationName);

            await activationHandler?.HandleAsync(activationArgs)!;
        }

        if (_defaultHandler.CanHandle(activationArgs))
        {
            _logger.Debug("Handling activation: {ActivationName}", _defaultHandler.ActivationName);
            await _defaultHandler.HandleAsync(activationArgs);
        }
    }

    private async Task InitializeAsync()
    {
        await _selectedGameService.InitializeAsync();
        await SetLanguage();
        await SetWindowSettings();
        await _themeSelectorService.InitializeAsync().ConfigureAwait(false);
        _notificationManager.Initialize();
    }


    private async Task StartupAsync()
    {
        await _themeSelectorService.SetRequestedThemeAsync();
        await _genshinProcessManager.TryInitialize();
        await _threeDMigtoProcessManager.TryInitialize();
        await _updateChecker.InitializeAsync();
        await _modUpdateAvailableChecker.InitializeAsync().ConfigureAwait(false);
        await Task.Run(() => _autoUpdaterService.UpdateAutoUpdater()).ConfigureAwait(false);
        await Task.Run(() => _elevatorService.Initialize()).ConfigureAwait(false);

        // 游戏内浮窗：建窗口 + 注册全局热键（当前只有鸣潮会真的建，见 OverlayWindowService）。
        // 走 GetService 而不是构造注入：这个服务是 internal 的，塞进本类 public 的构造函数会撞上可访问性检查；
        // 它也只是"启动时装一次"，没必要把 ActivationService 的构造函数撑得更长。
        await App.GetService<OverlayWindowService>().InitializeAsync();
    }

    const int MinimizedPosition = -32000;

    private async Task SetWindowSettings()
    {
        var screenSize = await _localSettingsService.ReadSettingAsync<ScreenSizeSettings>(ScreenSizeSettings.Key);
        if (screenSize == null)
            return;

        if (screenSize.PersistWindowSize && screenSize.Width != 0 && screenSize.Height != 0)
        {
            _logger.Debug($"Window size loaded: {screenSize.Width}x{screenSize.Height}");
            App.MainWindow.SetWindowSize(screenSize.Width, screenSize.Height);
        }

        if (screenSize.PersistWindowPosition)
        {
            if (screenSize.XPosition != 0 && screenSize.YPosition != 0 &&
                screenSize.XPosition != MinimizedPosition && screenSize.YPosition != MinimizedPosition)
                App.MainWindow.AppWindow.Move(new PointInt32(screenSize.XPosition, screenSize.YPosition));
            else
                App.MainWindow.CenterOnScreen();

            if (screenSize.IsFullScreen)
                App.MainWindow.Maximize();
        }
    }

    private async void OnApplicationExit(object sender, WindowEventArgs args)
    {
        if (App.ShutdownComplete) return;

        args.Handled = true;

        if (App.IsShuttingDown)
        {
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(async () =>
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            {
                var softShutdownGracePeriod = TimeSpan.FromSeconds(2);
                await Task.Delay(softShutdownGracePeriod);

                _logger.Warning(
                    "JASM shutdown took too long (>{maxShutdownGracePeriod}s), ignoring cleanup and exiting...",
                    softShutdownGracePeriod);
                App.ShutdownComplete = true;
                App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    Application.Current.Exit();
                    App.MainWindow.Close();
                });
            });

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(async () =>
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            {
                var maxShutdownGracePeriod = TimeSpan.FromSeconds(5);
                await Task.Delay(maxShutdownGracePeriod);

                _logger.Fatal("JASM failed to close after {maxShutdownGracePeriod} seconds, forcing exit...",
                    maxShutdownGracePeriod);
                Environment.Exit(1);
            });
            return;
        }


        await _lifeCycleService.StartShutdownAsync().ConfigureAwait(false);
    }

    // Declared here for now, might move to a different class later.
    private const string IgnoreAdminWarningKey = "IgnoreAdminPrivelegesWarning";

    private async Task HandleLaunchArgsAsync()
    {
        if (_args.Length == 0)
            return;

        var supportedGames = Enum.GetNames<SupportedGames>().Select(v => v.ToLower()).ToArray();

        if (_args.Any(arg => arg.Contains("help", StringComparison.OrdinalIgnoreCase)) &&
            // WinUI doesnt seem to launch like a regular console app.
            // This kinda works, but it's not perfect.
            // Overriding the main entry point didn't seem to work either.
            PInvoke.AttachConsole(unchecked((uint)-1)))
        {
            // TODO: Use CommandLineParser or something similar.
            Console.WriteLine("JASM Command line arguments:");

            Console.WriteLine(
                $"      --game <game> - Launch JASM with the specified game selected. Supported games: {string.Join('|', supportedGames)}");

            Console.WriteLine(
                "           --switch - If used with --game, will switch to the selected game if JASM is already running. This is done by exiting the already running instance.");

            Application.Current.Exit();
            await Task.Delay(-1);
        }


        await _selectedGameService.InitializeAsync().ConfigureAwait(false);
        var notSelectedGames = await _selectedGameService.GetNotSelectedGameAsync().ConfigureAwait(false);

        var launchGameArgIndex =
            Array.FindIndex(_args, arg => arg.Equals("--game", StringComparison.OrdinalIgnoreCase));
        if (launchGameArgIndex == -1)
            return;

        var launchGameArgValue = _args.ElementAtOrDefault(launchGameArgIndex + 1);
        if (launchGameArgValue.IsNullOrEmpty())
        {
            _logger.Warning("No game specified for arg: --game <{ValidGames}>",
                string.Join('|', Enum.GetNames<SupportedGames>().Select(v => v.ToLower())));
            return;
        }

        var selectedGame = Enum.TryParse<SupportedGames>(launchGameArgValue, true, out var game)
            ? game
            : SupportedGames.Genshin;

        if (!notSelectedGames.Contains(selectedGame))
            return;

        var otherProcess = _lifeCycleService.GetOtherInstanceProcess();

        if (otherProcess is not null)
        {
            if (!_args.Contains("--switch"))
            {
                // If the other instance is running, and the switch flag is not present, return.
                // Will be handled by the OtherInstance check later.
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

                _logger.Information("Closing running instance of JASM to switch to {SelectedGame}", selectedGame);
                otherProcess.CloseMainWindow();
                await otherProcess.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to close running instance of JASM");
                return;
            }

            try
            {
                await _selectedGameService.SaveSelectedGameAsync(selectedGame.ToString()).ConfigureAwait(false);
                await _selectedGameService.InitializeAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // If this errors then I don't know what to do.
                _logger.Error(e, "Failed to save selected game");
            }

            return;
        }

        try
        {
            await _selectedGameService.SaveSelectedGameAsync(selectedGame.ToString()).ConfigureAwait(false);
            await _selectedGameService.InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // If this errors then I don't know what to do.
            _logger.Error(e, "Failed to save selected game");
            return;
        }


        _logger.Information("Game selected via launch args: {SelectedGame}", selectedGame);
    }

    private void ShowStartupPopups()
    {
        App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
        {
            await Task.Delay(2000);
            await AdminWarningPopup();
            await Task.Delay(1000);
            await NewFolderStructurePopup();
        });
    }

    /// <summary>
    /// 本进程是否以管理员身份运行。判定只有一份，在 <see cref="AppElevation"/> ——
    /// 启动日志、这个弹窗、主窗口的常驻提示、浮窗的状态行问的必须是同一个结论，
    /// 四处各自判断就会出现「日志说提权了、横幅却不弹」这类不报错的偏差。
    /// </summary>
    private static bool IsRunningAsAdministrator() => AppElevation.IsElevated();

    /// <summary>
    /// 提权时启动弹窗。**它必须是有出路的一条路**，不能只是"不推荐 + 我知道了"。
    ///
    /// <para>
    /// 因为提权真的会毁掉一个功能：跨完整性级别的拖拽被 UIPI 整个掐掉（浮窗与主窗口同时只剩禁止光标、
    /// 松手没反应、且不报任何错）。用户报「拖动安装显示禁用」时，多半就是在这个弹窗上点了
    /// 「不再显示此警告」，之后再没有任何地方告诉他原因。所以这里补上：
    /// ① 拖拽不可用这件事本身；② 怎么解除；③ 一个「以普通权限重新启动」的按钮。
    /// </para>
    ///
    /// <para>
    /// 「拖拽不可用」那两段只在 <see cref="AppElevation.IsDragDropBlocked"/> 为真时才加：
    /// 关掉了 UAC 的机器上 explorer 自己也是高完整性、两者同级，拖拽本来是好的，加进去就是假警报。
    /// </para>
    /// </summary>
    private async Task AdminWarningPopup()
    {
        if (!IsRunningAsAdministrator()) return;

        var ignoreWarning = await _localSettingsService.ReadSettingAsync<bool>(IgnoreAdminWarningKey);

        if (ignoreWarning) return;

        var stackPanel = new StackPanel();
        var textWarning = new TextBlock()
        {
            Text = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_AdminWarningText", defaultValue: "You are running JASM as an administrator. This is not recommended.\n" +
                   "JASM was NOT designed to run with administrator privileges.\n" +
                   "Simple bugs, though unlikely, can potentially cause serious damage to your file system.\n\n" +
                   "Operations that need administrator rights (writing to protected folders, sending keys to a game running as administrator) " +
                   "are handed to the built-in elevation helper automatically.\n\n" +
                   "Use at your own risk, you have been warned"),
            TextWrapping = TextWrapping.WrapWholeWords
        };
        stackPanel.Children.Add(textWarning);

        if (AppElevation.IsDragDropBlocked())
            AddDragDropWarning(stackPanel);

        var doNotShowAgain = new CheckBox()
        {
            IsChecked = false,
            Content = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_AdminWarningDoNotShowAgain", defaultValue: "Do not show this warning again"),
            Margin = new Thickness(0, 10, 0, 0)
        };

        stackPanel.Children.Add(doNotShowAgain);


        var dialog = new ContentDialog
        {
            Title = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_AdminWarningTitle", defaultValue: "Running as Administrator Warning"),
            Content = stackPanel,

            // 主按钮 = 出路（一键去提权）。次按钮保留原来的「退出」，Close（右上角的 X / Esc）
            // 是「继续以管理员身份运行」—— 让"随手关掉弹窗"这个动作落在无害的一边。
            PrimaryButtonText = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_AdminWarningRestartBtn", defaultValue: "Restart without administrator rights"),
            SecondaryButtonText = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_AdminWarningExitBtn", defaultValue: "Exit"),
            CloseButtonText = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_AdminWarningStayBtn", defaultValue: "Keep running as administrator"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await _windowManagerService.ShowDialogAsync(dialog);

        // 复选框先落盘：下面 Primary 会把进程关掉，晚一步就永远存不上，
        // 用户下次启动又被同一个弹窗拦一遍
        if (doNotShowAgain.IsChecked == true)
            await _localSettingsService.SaveSettingAsync(IgnoreAdminWarningKey, true);

        switch (result)
        {
            case ContentDialogResult.Primary:
                await _lifeCycleService.RestartWithoutElevationAsync();
                break;

            case ContentDialogResult.Secondary:
                Application.Current.Exit();
                break;
        }
    }

    /// <summary>把「拖拽安装不可用」与解除办法两段加进弹窗。</summary>
    private void AddDragDropWarning(Panel panel)
    {
        panel.Children.Add(new TextBlock
        {
            Text = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_AdminDragDropWarning",
                defaultValue: "Drag and drop does not work while running as an administrator: dropping a mod archive or folder " +
                              "on JASM only shows a \"no\" cursor and nothing happens when you release it. " +
                              "This is a Windows restriction (a medium-integrity process cannot drop onto a high-integrity one) " +
                              "and JASM cannot work around it. Both the main window and the in-game overlay are affected."),
            TextWrapping = TextWrapping.WrapWholeWords,
            Margin = new Thickness(0, 12, 0, 0)
        });

        panel.Children.Add(new TextBlock
        {
            Text = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_AdminDragDropFix",
                defaultValue: "To get drag and drop back, run JASM without administrator rights: " +
                              "close JASM, right-click its shortcut (or the exe) → Properties → Compatibility → " +
                              "uncheck \"Run this program as an administrator\" → OK, then open it again."),
            TextWrapping = TextWrapping.WrapWholeWords,
            Margin = new Thickness(0, 8, 0, 0)
        });
    }

    public const string IgnoreNewFolderStructureKey = "IgnoreNewFolderStructureWarning";

    private async Task NewFolderStructurePopup()
    {
        if (!_skinManagerService.IsInitialized)
        {
            await _localSettingsService.SaveSettingAsync(IgnoreNewFolderStructureKey, true);
        }

        var ignoreWarning = await _localSettingsService.ReadOrCreateSettingAsync<bool>(IgnoreNewFolderStructureKey);

        if (ignoreWarning) return;

        var stackPanel = new StackPanel();
        var textWarning = new TextBlock()
        {
            Text = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_NewFolderStructureText", defaultValue: """
                   This version of JASM has a new folder structure.

                   Now Characters are organized by category, and each category has its own folder. So new format is as follows:
                   Mods/Category/Character/<Mod Folders>
                   Therefore, JASM won't see any of your mods until you reorganize them.
                   This is a one time thing, and you can do it manually if you want.

                   Also character folders are now created on demand and it is possible to clean up empty folders on the settings page.
                   """),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.WrapWholeWords
        };
        stackPanel.Children.Add(textWarning);

        var textWarning2 = new TextBlock()
        {
            Text = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_NewFolderStructureBackupText", defaultValue: "If you're uncertain about this then back up your mods first. I've tested this on my own mods."),
            FontWeight = FontWeights.Bold,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.WrapWholeWords
        };

        stackPanel.Children.Add(textWarning2);


        var textWarning3 = new TextBlock()
        {
            Text = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_NewFolderStructurePopupText", defaultValue: """

                   This popup will be shown until you chose an option below. You can also use the reorganize button on the settings page.
                   Check the logs if you want to see what's happening.
                   """),
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.WrapWholeWords
        };

        stackPanel.Children.Add(textWarning3);


        var dialog = new ContentDialog
        {
            Title = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_NewFolderStructureTitle", defaultValue: "New Folder structure"),
            Content = stackPanel,
            PrimaryButtonText = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_NewFolderStructureReorganizeBtn", defaultValue: "Reorganize my mods"),
            SecondaryButtonText = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_NewFolderStructureDoItMyselfBtn", defaultValue: "I will do it myself"),
            CloseButtonText = _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_NewFolderStructureCancelBtn", defaultValue: "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await _windowManagerService.ShowDialogAsync(dialog);

        if (result == ContentDialogResult.Primary)
        {
            _navigationViewService.IsEnabled = false;

            try
            {
                var movedModsCount = await Task.Run(() =>
                    _skinManagerService.ReorganizeModsAsync()); // Mods folder

                await _skinManagerService.RefreshModsAsync();

                if (movedModsCount == -1)
                    _notificationManager.ShowNotification(_languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_ReorganizeFailedTitle", defaultValue: "Mods reorganization failed."),
                        _languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_ReorganizeFailedMessage", defaultValue: "See logs for more details."), TimeSpan.FromSeconds(5));

                else
                    _notificationManager.ShowNotification(_languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_ReorganizeSuccessTitle", defaultValue: "Mods reorganized."),
                        string.Format(_languageLocalizer.GetLocalizedStringOrDefault("ActivationSvc_ReorganizeSuccessMessage", defaultValue: "Moved {0} mods to new character folders"), movedModsCount), TimeSpan.FromSeconds(5));
            }
            finally
            {
                _navigationViewService.IsEnabled = true;
                await _localSettingsService.SaveSettingAsync(IgnoreNewFolderStructureKey, true);
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            await _localSettingsService.SaveSettingAsync(IgnoreNewFolderStructureKey, true);
        }
        else
        {
        }
    }


    private async Task SetLanguage()
    {
        var selectedLanguage = (await _localSettingsService.ReadOrCreateSettingAsync<AppSettings>(AppSettings.Key))
            .Language?.ToLower().Trim();
        if (selectedLanguage == null)
        {
            return;
        }

        var supportedLanguages = _languageLocalizer.AvailableLanguages;
        var language = supportedLanguages.FirstOrDefault(lang =>
            lang.LanguageCode.Equals(selectedLanguage, StringComparison.CurrentCultureIgnoreCase));

        if (language != null)
            await _languageLocalizer.SetLanguageAsync(language);
    }
}