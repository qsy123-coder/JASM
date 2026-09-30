using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Windows.Storage.Pickers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ErrorOr;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Contracts.ViewModels;
using GIMI_ModManager.WinUI.Helpers;
using GIMI_ModManager.WinUI.Models.Options;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.AppManagement.Updating;
using GIMI_ModManager.WinUI.Services.GameDataSync;
using GIMI_ModManager.WinUI.Services.ModEnv;
using GIMI_ModManager.WinUI.Services.ModHandling;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.Validators.PreConfigured;
using GIMI_ModManager.WinUI.ViewModels.SettingsViewModels;
using GIMI_ModManager.WinUI.Views;
using GIMI_ModManager.WinUI.ViewModels.SubVms;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels;

public partial class SettingsViewModel : ObservableRecipient, INavigationAware
{
    private readonly IThemeSelectorService _themeSelectorService;
    private readonly ILogger _logger;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly INavigationViewService _navigationViewService;
    private readonly IWindowManagerService _windowManagerService;
    private readonly ISkinManagerService _skinManagerService;
    private readonly IGameService _gameService;
    private readonly ILanguageLocalizer _localizer;
    private readonly AutoUpdaterService _autoUpdaterService;
    private readonly SingleFileSelfUpdater _singleFileSelfUpdater;
    private readonly SelectedGameService _selectedGameService;
    private readonly ModUpdateAvailableChecker _modUpdateAvailableChecker;
    private readonly LifeCycleService _lifeCycleService;
    private readonly INavigationService _navigationService;
    private readonly ModArchiveRepository _modArchiveRepository;
    private readonly ModEnvSetupFacade _modEnvSetupFacade;


    private readonly NotificationManager _notificationManager;
    private readonly UpdateChecker _updateChecker;
    private readonly GameDataSyncService _gameDataSyncService;
    public ElevatorService ElevatorService;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetGenshinExePathCommand))]
    public GenshinProcessManager _genshinProcessManager;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(Reset3DmigotoPathCommand))]
    public ThreeDMigtoProcessManager _threeDMigtoProcessManager;


    [ObservableProperty] private ElementTheme _elementTheme;

    [ObservableProperty] private string _versionDescription;

    [ObservableProperty] private string _latestVersion = string.Empty;
    [ObservableProperty] private bool _showNewVersionAvailable = false;

    /// <summary>
    /// 「这次更新了什么」的链接（清单的 notesUrl，缺省退回 appsettings 里的发布页）。类型是
    /// <see cref="Uri"/> 而不是 string：<c>LinkButton.Link</c> 是 Uri 依赖属性，x:Bind 不会替我们
    /// 把字符串转成 Uri。为 null 时 LinkButton 点不动（其 handler 自己会跳过），不会崩。
    /// </summary>
    [ObservableProperty] private Uri? _latestVersionNotesUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IgnoreNewVersionCommand))]
    private bool _CanIgnoreUpdate = false;

    // ---- 单文件版进程内自更新的进度（folder 版走外部更新器，这里始终是 false） ----

    /// <summary>
    /// 自更新进行中。既用来显示进度条，也用来禁用"更新"按钮 —— 下载百 MB 期间被点第二下会同时
    /// 跑两个下载，第二个还会去覆盖第一个正在写的 .part。
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateJasmCommand))]
    private bool _isUpdatingJasm = false;

    /// <summary>已下载百分比（0–100），直接绑到 ProgressBar。</summary>
    [ObservableProperty] private double _updateProgressPercent = 0;

    /// <summary>「下载中 45.3 MB / 120.0 MB（1.2 MB/s）」这类人读文案。</summary>
    [ObservableProperty] private string _updateProgressText = string.Empty;

    private bool CanUpdateJasm() => !IsUpdatingJasm;

    /// <summary>
    /// 下载结束后置 false。<see cref="Progress{T}"/> 的回调是 Post 到 UI 线程的，可能比
    /// <c>await</c> 的续体晚一步到达，会把"下载完成，正在替换并重启…"覆盖回百分比文案 —— 用这个
    /// 闸门挡掉那些迟到的回调。
    /// </summary>
    private bool _acceptUpdateProgress;

    private void OnUpdateProgress(AppUpdateDownloadProgress progress)
    {
        if (!_acceptUpdateProgress)
            return;

        UpdateProgressPercent = progress.Percent;
        UpdateProgressText = progress.ToDisplayText();
    }

    [ObservableProperty] private ObservableCollection<string> _languages = new();
    [ObservableProperty] private string _selectedLanguage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _games = new()
    {
        SupportedGames.Genshin.ToString(),
        SupportedGames.Honkai.ToString(),
        SupportedGames.WuWa.ToString(),
        SupportedGames.ZZZ.ToString()
    };

    [ObservableProperty] private string _selectedGame = string.Empty;

    [ObservableProperty] private string _modCheckerStatus = ModUpdateAvailableChecker.RunningState.Waiting.ToString();

    [ObservableProperty] private bool _isModUpdateCheckerEnabled = false;

    [ObservableProperty] private DateTime? _nextModCheckTime = null;

    [ObservableProperty] private bool _characterAsSkinsCheckbox = false;

    [ObservableProperty] private int _maxCacheLimit;

    [ObservableProperty] private Uri _archiveCacheFolderPath;

    [ObservableProperty] private bool _persistWindowSize = false;

    [ObservableProperty] private bool _persistWindowPosition = false;

    // Game data sync
    [ObservableProperty] private string _gameDataLastSyncTime = "Never";
    [ObservableProperty] private string _gameDataCurrentVersion = "None";
    [ObservableProperty] private string _gameDataSyncStatus = "Idle";
    [ObservableProperty] private bool _isGameDataSyncEnabled = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGameDataSyncNotRunning))]
    private bool _isGameDataSyncRunning = false;
    public bool IsGameDataSyncNotRunning => !IsGameDataSyncRunning;

    private Dictionary<string, string> _nameToLangCode = new();

    public PathPicker PathToGIMIFolderPicker { get; }
    public PathPicker PathToModsFolderPicker { get; }

    [ObservableProperty] private bool _legacyCharacterDetails;

    /// <summary>Whether the selected game supports one-click Mod environment setup (e.g. WuWa).</summary>
    [ObservableProperty] private bool _showModEnvSetupButton;


    private static bool _showElevatorStartDialog = true;

    private ModManagerOptions? _modManagerOptions = null!;

    [ObservableProperty] private string _modCacheSizeGB = string.Empty;

    public SettingsViewModel(
        IThemeSelectorService themeSelectorService, ILocalSettingsService localSettingsService,
        ElevatorService elevatorService, ILogger logger, NotificationManager notificationManager,
        INavigationViewService navigationViewService, IWindowManagerService windowManagerService,
        ISkinManagerService skinManagerService, UpdateChecker updateChecker,
        GenshinProcessManager genshinProcessManager, ThreeDMigtoProcessManager threeDMigtoProcessManager,
        IGameService gameService, AutoUpdaterService autoUpdaterService, SingleFileSelfUpdater singleFileSelfUpdater,
        ILanguageLocalizer localizer,
        SelectedGameService selectedGameService, ModUpdateAvailableChecker modUpdateAvailableChecker,
        LifeCycleService lifeCycleService, INavigationService navigationService,
        ModArchiveRepository modArchiveRepository,
        GameDataSyncService gameDataSyncService, ModEnvSetupFacade modEnvSetupFacade)
    {
        _themeSelectorService = themeSelectorService;
        _localSettingsService = localSettingsService;
        ElevatorService = elevatorService;
        _notificationManager = notificationManager;
        _navigationViewService = navigationViewService;
        _windowManagerService = windowManagerService;
        _skinManagerService = skinManagerService;
        _updateChecker = updateChecker;
        _gameService = gameService;
        _autoUpdaterService = autoUpdaterService;
        _singleFileSelfUpdater = singleFileSelfUpdater;
        _localizer = localizer;
        _selectedGameService = selectedGameService;
        _modUpdateAvailableChecker = modUpdateAvailableChecker;
        _lifeCycleService = lifeCycleService;
        _navigationService = navigationService;
        _modArchiveRepository = modArchiveRepository;
        _gameDataSyncService = gameDataSyncService;
        _modEnvSetupFacade = modEnvSetupFacade;
        GenshinProcessManager = genshinProcessManager;
        ThreeDMigtoProcessManager = threeDMigtoProcessManager;
        _logger = logger.ForContext<SettingsViewModel>();
        _elementTheme = _themeSelectorService.Theme;
        _versionDescription = GetVersionDescription();

        _updateChecker.NewVersionAvailable += UpdateCheckerOnNewVersionAvailable;

        if (_updateChecker.LatestRetrievedVersion is not null &&
            _updateChecker.LatestRetrievedVersion != _updateChecker.CurrentVersion)
        {
            LatestVersion = VersionFormatter(_updateChecker.LatestRetrievedVersion);
            LatestVersionNotesUrl = ToNotesUri(_updateChecker.LatestReleaseNotesUrl);
            ShowNewVersionAvailable = true;
            if (_updateChecker.LatestRetrievedVersion != _updateChecker.IgnoredVersion)
                CanIgnoreUpdate = true;
        }

        ArchiveCacheFolderPath = _modArchiveRepository.ArchiveDirectory;

        _modManagerOptions = localSettingsService.ReadSetting<ModManagerOptions>(ModManagerOptions.Section);
        PathToGIMIFolderPicker = new PathPicker();
        PathToModsFolderPicker = new PathPicker(ModsFolderValidator.Validators);

        CharacterAsSkinsCheckbox = _modManagerOptions?.CharacterSkinsAsCharacters ?? false;

        PathToGIMIFolderPicker.Path = _modManagerOptions?.GimiRootFolderPath;
        PathToModsFolderPicker.Path = _modManagerOptions?.ModsFolderPath;


        PathToGIMIFolderPicker.IsValidChanged += (sender, args) => SaveSettingsCommand.NotifyCanExecuteChanged();
        PathToModsFolderPicker.IsValidChanged +=
            (sender, args) => SaveSettingsCommand.NotifyCanExecuteChanged();


        PathToGIMIFolderPicker.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(PathPicker.Path))
                SaveSettingsCommand.NotifyCanExecuteChanged();
        };

        PathToModsFolderPicker.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName == nameof(PathPicker.Path))
                SaveSettingsCommand.NotifyCanExecuteChanged();
        };
        ElevatorService.CheckStatus();

        MaxCacheLimit = localSettingsService.ReadSetting<ModArchiveSettings>(ModArchiveSettings.Key)
            ?.MaxLocalArchiveCacheSizeGb ?? new ModArchiveSettings().MaxLocalArchiveCacheSizeGb;
        SetCacheString(MaxCacheLimit);

        var cultures = CultureInfo.GetCultures(CultureTypes.AllCultures);
        cultures = cultures.Append(new CultureInfo("zh-cn")).ToArray();


        var supportedCultures = _localizer.AvailableLanguages.Select(l => l.LanguageCode).ToArray();

        foreach (var culture in cultures)
        {
            if (!supportedCultures.Contains(culture.Name.ToLower())) continue;

            Languages.Add(culture.NativeName);
            _nameToLangCode.Add(culture.NativeName, culture.Name.ToLower());

            if (_localizer.CurrentLanguage.Equals(culture))
                SelectedLanguage = culture.NativeName;
        }

        ModCheckerStatus = _localizer.GetLocalizedStringOrDefault(_modUpdateAvailableChecker.Status.ToString(),
            _modUpdateAvailableChecker.Status.ToString());
        NextModCheckTime = _modUpdateAvailableChecker.NextRunAt;
        _modUpdateAvailableChecker.OnUpdateCheckerEvent += (sender, args) =>
        {
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                ModCheckerStatus = _localizer.GetLocalizedStringOrDefault(_modUpdateAvailableChecker.Status.ToString(),
                    _modUpdateAvailableChecker.Status.ToString());
                NextModCheckTime = args.NextRunAt;
            });
        };
    }


    [RelayCommand]
    private async Task SwitchThemeAsync(ElementTheme param)
    {
        if (ElementTheme != param)
        {
            var result = await _windowManagerService.ShowDialogAsync(new ContentDialog()
            {
                Title = _localizer.GetLocalizedStringOrDefault("SettingsVM_RestartRequiredTitle", defaultValue: "Restart required"),
                Content = new TextBlock()
                {
                    Text =
                        _localizer.GetLocalizedStringOrDefault("SettingsVM_SwitchThemeText", defaultValue: "You'll need to restart the application for the theme to take effect or else the application will become unstable. " +
                        "This is most likely me not configuring the theming correctly. Dark Mode is the recommended theme.\n\n" +
                        "Sorry for the inconvenience."),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_RestartBtn", defaultValue: "Restart"),
                CloseButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_CancelBtn", defaultValue: "Cancel"),
                DefaultButton = ContentDialogButton.Primary
            });

            if (result != ContentDialogResult.Primary) return;

            ElementTheme = param;
            await _themeSelectorService.SetThemeAsync(param);
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("SettingsVM_RestartingTitle", defaultValue: "Restarting..."), _localizer.GetLocalizedStringOrDefault("SettingsVM_RestartingMessage", defaultValue: "The application will restart now."),
                null);
            await RestartAppAsync();
        }
    }

    [RelayCommand]
    private async Task WindowSizePositionToggle(string? type)
    {
        if (type != "size" && type != "position") return;

        var windowSettings =
            await _localSettingsService.ReadOrCreateSettingAsync<ScreenSizeSettings>(ScreenSizeSettings.Key);

        if (type == "size")
        {
            PersistWindowSize = !PersistWindowSize;
            windowSettings.PersistWindowSize = PersistWindowSize;
        }
        else
        {
            PersistWindowPosition = !PersistWindowPosition;
            windowSettings.PersistWindowPosition = PersistWindowPosition;
        }

        await _localSettingsService.SaveSettingAsync(ScreenSizeSettings.Key, windowSettings).ConfigureAwait(false);
    }

    private static string GetVersionDescription()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version!;

        return
            $"{"AppDisplayName".GetLocalized()} - {VersionFormatter(version)}";
    }


    private bool ValidFolderSettings()
    {
        return PathToGIMIFolderPicker.IsValid && PathToModsFolderPicker.IsValid &&
               PathToGIMIFolderPicker.Path != PathToModsFolderPicker.Path &&
               (PathToGIMIFolderPicker.Path != _modManagerOptions?.GimiRootFolderPath ||
                PathToModsFolderPicker.Path != _modManagerOptions?.ModsFolderPath);
    }


    [RelayCommand(CanExecute = nameof(ValidFolderSettings))]
    private async Task SaveSettings()
    {
        var dialog = new ContentDialog();
        dialog.XamlRoot = App.MainWindow.Content.XamlRoot;
        dialog.Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style;
        dialog.Title = _localizer.GetLocalizedStringOrDefault("SettingsVM_UpdateFolderPathsTitle", defaultValue: "Update Folder Paths?");
        dialog.CloseButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_CancelBtn", defaultValue: "Cancel");
        dialog.PrimaryButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_SaveBtn", defaultValue: "Save");
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Content = _localizer.GetLocalizedStringOrDefault("SettingsVM_UpdateFolderPathsContent", defaultValue: "Do you want to save the new folder paths? The App will restart afterwards.");

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            var modManagerOptions = await _localSettingsService.ReadSettingAsync<ModManagerOptions>(
                ModManagerOptions.Section) ?? new ModManagerOptions();

            modManagerOptions.GimiRootFolderPath = PathToGIMIFolderPicker.Path;
            modManagerOptions.ModsFolderPath = PathToModsFolderPicker.Path;

            await _localSettingsService.SaveSettingAsync(ModManagerOptions.Section,
                modManagerOptions);
            _logger.Information("Saved startup settings: {@ModManagerOptions}", modManagerOptions);
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("SettingsVM_SettingsSavedRestartingTitle", defaultValue: "Settings saved. Restarting App..."), "", TimeSpan.FromSeconds(2));


            await RestartAppAsync();
        }
    }

    [RelayCommand]
    private async Task BrowseGimiFolderAsync()
    {
        await PathToGIMIFolderPicker.BrowseFolderPathAsync(App.MainWindow);
        if (PathToGIMIFolderPicker.PathHasValue &&
            !PathToModsFolderPicker.PathHasValue)
            PathToModsFolderPicker.Path = Path.Combine(PathToGIMIFolderPicker.Path!, "Mods");
    }


    [RelayCommand]
    private async Task BrowseModsFolderAsync()
    {
        await PathToModsFolderPicker.BrowseFolderPathAsync(App.MainWindow);
    }

    /// <summary>
    /// Opens the one-click Mod environment setup wizard; on success fills both path pickers and
    /// triggers the existing save flow (which restarts the app to apply the new paths).
    /// </summary>
    [RelayCommand]
    private async Task OneClickSetupAsync()
    {
        var dialog = App.GetService<ModEnvSetupDialog>();
        dialog.XamlRoot = App.MainWindow.Content.XamlRoot;

        // Reuse the XXMI root picked earlier; ignoring the saved value here would quietly install a
        // second copy at the default location.
        var modManagerOptions = await _localSettingsService.ReadOrCreateSettingAsync<ModManagerOptions>(
            ModManagerOptions.Section);
        dialog.ViewModel.CustomRootFolder = modManagerOptions.XxmiRootFolderPath;

        await dialog.ShowAsync();

        // The wizard's "安装位置" row may have moved the target. Saved here rather than by SaveSettings,
        // which only writes the two folder paths and needs them to have changed first.
        if (!string.Equals(modManagerOptions.XxmiRootFolderPath, dialog.CustomRootFolder,
                StringComparison.OrdinalIgnoreCase))
        {
            modManagerOptions.XxmiRootFolderPath = dialog.CustomRootFolder;
            await _localSettingsService.SaveSettingAsync(ModManagerOptions.Section, modManagerOptions);
            _logger.Information("Saved XXMI root folder: {XxmiRootFolder}", dialog.CustomRootFolder);
        }

        if (dialog.MiFolder is null || dialog.ModsFolder is null)
        {
            if (dialog.Result is { Success: false } && dialog.Result is { Cancelled: false })
                _notificationManager.ShowNotification("Mod 环境配置失败", string.Join("；", dialog.Result.Issues),
                    TimeSpan.FromSeconds(5));
            return;
        }

        PathToGIMIFolderPicker.Path = dialog.MiFolder;
        PathToGIMIFolderPicker.Validate();
        PathToModsFolderPicker.Path = dialog.ModsFolder;
        PathToModsFolderPicker.Validate();

        if (ValidFolderSettings())
        {
            await SaveSettingsCommand.ExecuteAsync(null);
        }
        else
        {
            _notificationManager.ShowNotification("Mod 环境配置完成", "路径已填入，路径设置未变化。",
                TimeSpan.FromSeconds(3));
        }
    }

    [RelayCommand]
    private async Task ReorganizeModsAsync()
    {
        var result = await _windowManagerService.ShowDialogAsync(new ContentDialog()
        {
            Title = _localizer.GetLocalizedStringOrDefault("SettingsVM_ReorganizeModsTitle", defaultValue: "Reorganize Mods?"),
            Content = new TextBlock()
            {
                Text =
                    _localizer.GetLocalizedStringOrDefault("SettingsVM_ReorganizeModsText", defaultValue: "Do you want to reorganize the Mods folder?\n" +
                    "This will prompt the application to sort existing mods that are directly in the Mods folder and Others folder, into folders assigned to their respective characters.\n\n" +
                    "Any mods that can't be reasonably matched will be placed in an 'Others' folder. While the mods already in 'Others' folder will remain there."),
                TextWrapping = TextWrapping.WrapWholeWords,
                IsTextSelectionEnabled = true
            },
            PrimaryButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_YesBtn", defaultValue: "Yes"),
            DefaultButton = ContentDialogButton.Primary,
            CloseButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_CancelBtn", defaultValue: "Cancel"),
            Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style
        });

        if (result == ContentDialogResult.Primary)
        {
            _navigationViewService.IsEnabled = false;

            try
            {
                var movedModsCount = await Task.Run(() =>
                    _skinManagerService.ReorganizeModsAsync()); // Mods folder

                movedModsCount += await Task.Run(() =>
                    _skinManagerService.ReorganizeModsAsync(
                        _gameService.GetCharacterByIdentifier(_gameService.OtherCharacterInternalName)!
                            .InternalName)); // Others folder

                await _skinManagerService.RefreshModsAsync();

                if (movedModsCount == -1)
                    _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("SettingsVM_ReorgFailedTitle", defaultValue: "Mods reorganization failed."),
                        _localizer.GetLocalizedStringOrDefault("SettingsVM_ReorgFailedMessage", defaultValue: "See logs for more details."), TimeSpan.FromSeconds(5));

                else
                    _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("SettingsVM_ReorganizedTitle", defaultValue: "Mods reorganized."),
                        string.Format(_localizer.GetLocalizedStringOrDefault("SettingsVM_ReorganizedMessage", defaultValue: "Moved {0} mods to character folders"), movedModsCount), TimeSpan.FromSeconds(5));
            }
            finally
            {
                _navigationViewService.IsEnabled = true;
            }
        }
    }


    private async Task RestartAppAsync(int delay = 2)
    {
        _navigationViewService.IsEnabled = false;

        await Task.Delay(TimeSpan.FromSeconds(delay));

        await _lifeCycleService.RestartAsync(notifyOnError: true);
    }

    private bool CanStartElevator()
    {
        return ElevatorService.ElevatorStatus == ElevatorStatus.NotRunning;
    }

    [RelayCommand(CanExecute = nameof(CanStartElevator))]
    private async Task StartElevator()
    {
        var text = new TextBlock
        {
            TextWrapping = TextWrapping.WrapWholeWords,
            Text = _localizer.GetLocalizedStringOrDefault("/Settings/StartElevatorDialogText") ??
                   "Press Start to launch the Elevator. The Elevator is an elevated (admin) process that is used for communication with the Genshin game process.\n\n" +
                   "While the Elevator is active, you can press F10 within this App to refresh active mods in Genshin.\n\n" +
                   "Enabling and disabling mods will also automatically refresh active mods in Genshin " +
                   "The Elevator process should automatically close when this program is closed.\n\n" +
                   "After pressing Start, a User Account Control (UAC) prompt will appear to confirm the elevation.\n\n" +
                   "(This requires that Genshin and that 3Dmigoto is running, when pressing F10\n\n" +
                   "Check the FAQ on the JASM github to download it separately as it gets flagged as malware.",
            Margin = new Thickness(0, 0, 0, 12),
            IsTextSelectionEnabled = true
        };


        var doNotShowAgainCheckBox = new CheckBox
        {
            Content = _localizer.GetLocalizedStringOrDefault("/Settings/StartElevatorDialogDontShowContent") ??
                      "Don't Show this Again",
            IsChecked = false
        };

        var stackPanel = new StackPanel
        {
            Children =
            {
                text,
                doNotShowAgainCheckBox
            }
        };


        var dialog = new ContentDialog
        {
            Title = _localizer.GetLocalizedStringOrDefault("/Settings/StartElevatorDialogTitle") ??
                    "Start Elevator Process?",
            Content = stackPanel,
            DefaultButton = ContentDialogButton.Primary,
            CloseButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_CancelBtn", defaultValue: "Cancel"),
            PrimaryButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_StartBtn", defaultValue: "Start"),
            XamlRoot = App.MainWindow.Content.XamlRoot
        };

        var start = true;

        if (_showElevatorStartDialog)
        {
            var result = await dialog.ShowAsync();
            start = result == ContentDialogResult.Primary;
            if (start)
                _showElevatorStartDialog = !doNotShowAgainCheckBox.IsChecked == true;
        }

        if (start && ElevatorService.ElevatorStatus == ElevatorStatus.NotRunning)
            try
            {
                ElevatorService.StartElevator();
            }
            catch (Win32Exception e)
            {
                _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("SettingsVM_UnableStartElevatorTitle", defaultValue: "Unable to start Elevator"), e.Message, TimeSpan.FromSeconds(10));
                _showElevatorStartDialog = true;
            }
    }

    private bool CanResetGenshinExePath()
    {
        return GenshinProcessManager.ProcessStatus != ProcessStatus.NotInitialized;
    }

    [RelayCommand(CanExecute = nameof(CanResetGenshinExePath))]
    private async Task ResetGenshinExePath()
    {
        await GenshinProcessManager.ResetProcessOptions();
    }

    private bool CanReset3DmigotoPath()
    {
        return ThreeDMigtoProcessManager.ProcessStatus != ProcessStatus.NotInitialized;
    }

    [RelayCommand(CanExecute = nameof(CanReset3DmigotoPath))]
    private async Task Reset3DmigotoPath()
    {
        await ThreeDMigtoProcessManager.ResetProcessOptions();
    }

    private void UpdateCheckerOnNewVersionAvailable(object? sender, UpdateChecker.NewVersionEventArgs e)
    {
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (e.Version == new Version())
            {
                CanIgnoreUpdate = _updateChecker.LatestRetrievedVersion != _updateChecker.IgnoredVersion;
                return;
            }

            LatestVersion = VersionFormatter(e.Version);
            LatestVersionNotesUrl = ToNotesUri(_updateChecker.LatestReleaseNotesUrl);
        });
    }

    private static string VersionFormatter(Version version)
    {
        return $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>把更新源给的说明链接转成 <c>LinkButton.Link</c> 要的 Uri；给不出合法绝对地址就返回 null。</summary>
    private static Uri? ToNotesUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    [RelayCommand(CanExecute = nameof(CanIgnoreUpdate))]
    private async Task IgnoreNewVersion()
    {
        await _updateChecker.IgnoreCurrentVersionAsync();
    }

    [ObservableProperty] private bool _exportingMods = false;
    [ObservableProperty] private int _exportProgress = 0;
    [ObservableProperty] private string _exportProgressText = string.Empty;
    [ObservableProperty] private string? _currentModName;

    [RelayCommand]
    private async Task ExportMods(ContentDialog contentDialog)
    {
        var dialog = new ContentDialog()
        {
            PrimaryButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_ExportBtn", defaultValue: "Export"),
            IsPrimaryButtonEnabled = true,
            CloseButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_CancelBtn", defaultValue: "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.Title = _localizer.GetLocalizedStringOrDefault("SettingsVM_ExportModsTitle", defaultValue: "Export Mods");

        dialog.ContentTemplate = contentDialog.ContentTemplate;

        var model = new ExportModsDialogModel(_gameService.GetAllModdableObjects());
        dialog.DataContext = model;
        var result = await _windowManagerService.ShowDialogAsync(dialog);

        if (result != ContentDialogResult.Primary)
            return;

        var folderPicker = new FolderPicker();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);
        folderPicker.FileTypeFilter.Add("*");
        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder == null)
            return;

        ExportingMods = true;
        _navigationViewService.IsEnabled = false;

        var charactersToExport =
            model.CharacterModsToBackup.Where(modList => modList.IsChecked).Select(ch => ch.Character);
        var modsList = new List<ICharacterModList>();
        foreach (var character in charactersToExport)
            modsList.Add(_skinManagerService.GetCharacterModList(character.InternalName));

        try
        {
            _skinManagerService.ModExportProgress += HandleProgressEvent;
            await Task.Run(() =>
            {
                _skinManagerService.ExportMods(modsList, folder.Path,
                    removeLocalJasmSettings: model.RemoveJasmSettings, zip: false,
                    keepCharacterFolderStructure: model.KeepFolderStructure, setModStatus: model.SetModStatus);
            });
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("SettingsVM_ModsExportedTitle", defaultValue: "Mods exported"), string.Format(_localizer.GetLocalizedStringOrDefault("SettingsVM_ModsExportedMessage", defaultValue: "Mods exported to {0}"), folder.Path),
                TimeSpan.FromSeconds(5));
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error exporting mods");
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("SettingsVM_ErrorExportingModsTitle", defaultValue: "Error exporting mods"), e.Message, TimeSpan.FromSeconds(10));
        }
        finally
        {
            _skinManagerService.ModExportProgress -= HandleProgressEvent;
            ExportingMods = false;
            _navigationViewService.IsEnabled = true;
        }
    }

    private void HandleProgressEvent(object? sender, ExportProgress args)
    {
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            ExportProgress = args.Progress;
            ExportProgressText = args.Operation;
            CurrentModName = args.ModName;
        });
    }


    [RelayCommand]
    private async Task SelectLanguage(string selectedLanguageName)
    {
        if (_nameToLangCode.TryGetValue(selectedLanguageName, out var langCode))
        {
            if (langCode == _localizer.CurrentLanguage.LanguageCode)
                return;

            var restartDialog = new ContentDialog()
            {
                Title = _localizer.GetLocalizedStringOrDefault("SettingsVM_LanguageRestartRequiredTitle", defaultValue: "Restart Required"),
                Content = new TextBlock()
                {
                    Text = _localizer.GetLocalizedStringOrDefault("/Settings/ChangeLanguageDialogText",
                        defaultValue:
                        "Changing the language requires a restart of the application.\n" +
                        "This is required to ensure that the application is configured correctly for the selected language.\n\n" +
                        "Do you want to change the language?"),
                    TextWrapping = TextWrapping.WrapWholeWords,
                    IsTextSelectionEnabled = true
                },
                PrimaryButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_ChangeLanguageRestartBtn", defaultValue: "Change Language and restart"),
                CloseButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_CancelBtn", defaultValue: "Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await _windowManagerService.ShowDialogAsync(restartDialog);

            var currentLanguage = _localizer.CurrentLanguage.LanguageName;
            if (result != ContentDialogResult.Primary)
            {
                SelectedLanguage = currentLanguage;
                return;
            }

            await _localizer.SetLanguageAsync(langCode);

            var appSettings = await _localSettingsService.ReadOrCreateSettingAsync<AppSettings>(AppSettings.Key);
            appSettings.Language = langCode;
            await _localSettingsService.SaveSettingAsync(AppSettings.Key, appSettings);
            currentLanguage = _localizer.CurrentLanguage.LanguageName;
            SelectedLanguage = currentLanguage;

            await RestartAppAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateJasm))]
    private async Task UpdateJasm()
    {
        // folder 版：走外部更新器（存在 JASM - Auto Updater.exe）
        if (_autoUpdaterService.AutoUpdaterExists)
        {
            var errors = Array.Empty<Error>();
            try
            {
                errors = _autoUpdaterService.StartSelfUpdateProcess();
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error starting update process");
                _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("SettingsVM_ErrorStartingUpdateTitle", defaultValue: "Error starting update process"), e.Message, TimeSpan.FromSeconds(10));
            }

            if (errors is not null && errors.Any())
            {
                var errorMessages = errors.Select(e => e.Description).ToArray();
                _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("SettingsVM_CouldNotStartUpdateTitle", defaultValue: "Could not start update process"), string.Join('\n', errorMessages),
                    TimeSpan.FromSeconds(10));
            }

            return;
        }

        // 单文件版：进程内自更新（下载 → 解出单个 exe → 临时脚本替换并重启）
        try
        {
            _logger.Information("Single-file build detected, using in-app self-update.");

            IsUpdatingJasm = true;
            UpdateProgressPercent = 0;
            UpdateProgressText = "正在准备下载更新包…";
            _acceptUpdateProgress = true;

            // new Progress<T> 在 UI 线程（本方法就在 UI 线程上跑）构造，因此回调自动 Post 回 UI 线程，
            // 下面的属性赋值不必再手工 DispatcherQueue.TryEnqueue。
            var result = await _singleFileSelfUpdater.TryUpdateAsync(_updateChecker.CurrentVersion,
                new Progress<AppUpdateDownloadProgress>(OnUpdateProgress));

            _acceptUpdateProgress = false; // 挡住晚到的进度回调，别覆盖下面的完成文案

            if (result.Success)
            {
                _logger.Information("Single-file update handed off to replacement script. Exiting app.");
                UpdateProgressPercent = 100;
                UpdateProgressText = "下载完成，正在替换并重启…";
                await Task.Delay(800); // 让进度条先渲染一下,再交给脚本替换
                Application.Current.Exit();
            }
            else
            {
                _logger.Warning("Single-file self update did not proceed: {Error}", result.Error);
                _notificationManager.ShowNotification("更新失败", result.Error ?? "未知错误", TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception e)
        {
            _logger.Error(e, "Error starting single-file self update");
            _notificationManager.ShowNotification("更新启动出错", e.Message, TimeSpan.FromSeconds(10));
        }
        finally
        {
            // 退出路径上置位也无意义，但失败 / 异常路径要靠它把按钮和进度条恢复成可用状态。
            _acceptUpdateProgress = false;
            IsUpdatingJasm = false;
        }
    }


    [RelayCommand]
    private async Task SelectGameAsync(string? game)
    {
        var jasmSelectedGame = await _selectedGameService.GetSelectedGameAsync();

        if (game.IsNullOrEmpty() || game == jasmSelectedGame)
            return;

        var switchGameDialog = new ContentDialog()
        {
            Title = _localizer.GetLocalizedStringOrDefault("SettingsVM_SwitchGameTitle", defaultValue: "Switch Game"),
            Content = new TextBlock()
            {
                Text =
                    _localizer.GetLocalizedStringOrDefault("SettingsVM_SwitchGameText", defaultValue: "Switching games will restart the application. " +
                    "This is required to ensure that the application is configured correctly for the selected game.\n\n" +
                    "Do you want to switch games?"),
                TextWrapping = TextWrapping.WrapWholeWords
            },

            PrimaryButtonText = string.Format(_localizer.GetLocalizedStringOrDefault("SettingsVM_SwitchToGameBtn", defaultValue: "Switch to {0}"), game),
            CloseButtonText = _localizer.GetLocalizedStringOrDefault("SettingsVM_CancelBtn", defaultValue: "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await _windowManagerService.ShowDialogAsync(switchGameDialog);

        if (result != ContentDialogResult.Primary)
        {
            SelectedGame = game;
            return;
        }

        await _selectedGameService.SetSelectedGame(game);
        await RestartAppAsync(0).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task ToggleCharacterSkinsAsCharacters()
    {
        var modManagerOptions =
            await _localSettingsService.ReadOrCreateSettingAsync<ModManagerOptions>(ModManagerOptions.Section);

        var result = await new CharacterSkinsDialog().ShowDialogAsync(modManagerOptions.CharacterSkinsAsCharacters);

        if (result != ContentDialogResult.Primary)
        {
            CharacterAsSkinsCheckbox = modManagerOptions.CharacterSkinsAsCharacters;
            return;
        }


        modManagerOptions.CharacterSkinsAsCharacters = !modManagerOptions.CharacterSkinsAsCharacters;

        await _localSettingsService.SaveSettingAsync(ModManagerOptions.Section, modManagerOptions);

        CharacterAsSkinsCheckbox = modManagerOptions.CharacterSkinsAsCharacters;

        await RestartAppAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private Task NavigateToCommandsSettings()
    {
        _navigationService.NavigateTo(typeof(CommandsSettingsViewModel).FullName!,
            transitionInfo: new SlideNavigationTransitionInfo() { Effect = SlideNavigationTransitionEffect.FromRight });
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ToggleModUpdateChecker()
    {
        var modUpdateCheckerSettings =
            await _localSettingsService.ReadOrCreateSettingAsync<BackGroundModCheckerSettings>(
                BackGroundModCheckerSettings.Key);

        await Task.Run(async () =>
        {
            if (modUpdateCheckerSettings.Enabled)
                await _modUpdateAvailableChecker.DisableAutoCheckerAsync();
            else
                await _modUpdateAvailableChecker.EnableAutoCheckerAsync();

            await Task.Delay(1000).ConfigureAwait(false);
        });

        modUpdateCheckerSettings = await _localSettingsService.ReadOrCreateSettingAsync<BackGroundModCheckerSettings>(
            BackGroundModCheckerSettings.Key);

        IsModUpdateCheckerEnabled = modUpdateCheckerSettings.Enabled;
    }

    [RelayCommand]
    private async Task CheckAndSyncGameDataAsync()
    {
        if (IsGameDataSyncRunning) return;
        IsGameDataSyncRunning = true;
        GameDataSyncStatus = "Checking...";

        try
        {
            var result = await _gameDataSyncService.CheckAndSyncAsync(
                Enum.Parse<SupportedGames>(SelectedGame), isManual: true);

            GameDataSyncStatus = result switch
            {
                SyncResult.Success => "Sync successful",
                SyncResult.AlreadyUpToDate => "Already up to date",
                SyncResult.Failed => "Sync failed",
                SyncResult.NoReleaseFound => "No data release found",
                _ => ""
            };
        }
        finally
        {
            IsGameDataSyncRunning = false;
            RefreshSyncDisplay();
        }
    }

    [RelayCommand]
    private async Task ToggleGameDataAutoSyncAsync()
    {
        var settings = await _localSettingsService
            .ReadOrCreateSettingAsync<GameDataSyncSettings>(GameDataSyncSettings.Key);
        settings.AutoSyncOnStartup = IsGameDataSyncEnabled;
        await _localSettingsService.SaveSettingAsync(GameDataSyncSettings.Key, settings);
    }

    private void RefreshSyncDisplay()
    {
        var game = Enum.Parse<SupportedGames>(SelectedGame);
        GameDataCurrentVersion = _gameDataSyncService.GetCurrentDataVersion(game) ?? "None";
        var lastSync = _gameDataSyncService.GetLastSyncTime(game);
        GameDataLastSyncTime = lastSync?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "Never";
    }

    public async void OnNavigatedTo(object parameter)
    {
        SelectedGame = await _selectedGameService.GetSelectedGameAsync();
        var modUpdateCheckerOptions =
            await _localSettingsService.ReadOrCreateSettingAsync<BackGroundModCheckerSettings>(
                BackGroundModCheckerSettings.Key);

        IsModUpdateCheckerEnabled = modUpdateCheckerOptions.Enabled;
        var gameInfo = await GameService.GetGameInfoAsync(Enum.Parse<SupportedGames>(SelectedGame));

        if (gameInfo is not null)
        {
            PathToGIMIFolderPicker.SetValidators(GimiFolderRootValidators.Validators(gameInfo.GameModelImporterExeNames));
            ShowModEnvSetupButton = gameInfo.ModEnv is not null;
        }
        else
        {
            ShowModEnvSetupButton = false;
        }

        var windowSettings =
            await _localSettingsService.ReadOrCreateSettingAsync<ScreenSizeSettings>(ScreenSizeSettings.Key);

        var characterDetailsSettings = await _localSettingsService.ReadCharacterDetailsSettingsAsync(SettingScope.App);

        PersistWindowSize = windowSettings.PersistWindowSize;
        PersistWindowPosition = windowSettings.PersistWindowPosition;
        await GenshinProcessManager.TryInitialize();
        await ThreeDMigtoProcessManager.TryInitialize();
        ModCacheSizeGB = _modArchiveRepository.GetTotalCacheSizeInGB().ToString("F");

        // Game data sync initialization
        var syncSettings = await _localSettingsService
            .ReadOrCreateSettingAsync<GameDataSyncSettings>(GameDataSyncSettings.Key);
        IsGameDataSyncEnabled = syncSettings.AutoSyncOnStartup;
        RefreshSyncDisplay();

        if (IsGameDataSyncEnabled)
        {
            _ = Task.Run(async () =>
            {
                await _gameDataSyncService.CheckAndSyncAsync(
                    Enum.Parse<SupportedGames>(SelectedGame), isManual: false);
            });
        }
    }

    [ObservableProperty] private string _maxCacheSizeString = string.Empty;

    private void SetCacheString(int value)
    {
        MaxCacheSizeString = $"{value} GB";
    }

    [RelayCommand]
    private async Task SetCacheLimit(int maxValue)
    {
        var modArchiveSettings =
            await _localSettingsService.ReadOrCreateSettingAsync<ModArchiveSettings>(ModArchiveSettings.Key);

        modArchiveSettings.MaxLocalArchiveCacheSizeGb = maxValue;

        await _localSettingsService.SaveSettingAsync(ModArchiveSettings.Key, modArchiveSettings);

        MaxCacheLimit = maxValue;
        SetCacheString(maxValue);
    }


    [RelayCommand]
    private static Task ShowCleanModsFolderDialogAsync()
    {
        var dialog = new ClearEmptyFoldersDialog();
        return dialog.ShowDialogAsync();
    }


    [RelayCommand]
    private Task ShowDisableAllModsDialogAsync()
    {
        var dialog = new DisableAllModsDialog();
        return dialog.ShowDialogAsync();
    }

    public void OnNavigatedFrom()
    {
    }
}

public partial class ExportModsDialogModel : ObservableObject
{
    [ObservableProperty] private bool _zipMods = false;
    [ObservableProperty] private bool _keepFolderStructure = true;

    [ObservableProperty] private bool _removeJasmSettings = false;

    public ObservableCollection<CharacterCheckboxModel> CharacterModsToBackup { get; set; } = new();

    public ObservableCollection<SetModStatus> SetModStatuses { get; set; } = new()
    {
        SetModStatus.KeepCurrent,
        SetModStatus.EnableAllMods,
        SetModStatus.DisableAllMods
    };

    [ObservableProperty] private SetModStatus _setModStatus = SetModStatus.KeepCurrent;

    public ExportModsDialogModel(IEnumerable<IModdableObject> characters)
    {
        SetModStatus = SetModStatus.KeepCurrent;
        foreach (var character in characters) CharacterModsToBackup.Add(new CharacterCheckboxModel(character));
    }
}

public partial class CharacterCheckboxModel : ObservableObject
{
    [ObservableProperty] private bool _isChecked = true;
    [ObservableProperty] private IModdableObject _character;

    public CharacterCheckboxModel(IModdableObject character)
    {
        _character = character;
    }
}