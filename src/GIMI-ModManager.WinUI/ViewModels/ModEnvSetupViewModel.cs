using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GIMI_ModManager.Core.Services.CommandService;
using GIMI_ModManager.WinUI.Models.ModEnvSetup;
using GIMI_ModManager.WinUI.Services;
using GIMI_ModManager.WinUI.Services.ModEnv;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels;

/// <summary>
/// Drives the one-click Mod environment setup wizard: runs the idempotency pre-check,
/// then the install pipeline, surfacing progress/logs for the dialog.
/// All public methods are expected to be called from the UI thread.
/// </summary>
public partial class ModEnvSetupViewModel : ObservableRecipient
{
    private readonly ModEnvSetupFacade _facade;
    private readonly CommandService _commandService;
    private readonly CommandHandlerService _commandHandlerService;
    private readonly ILogger _logger;

    private const string NotInstalledVersionText = "未检测到已安装版本";

    public ObservableCollection<ModEnvPackagePreCheck> Packages { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>Selectable base-package versions, newest first. A single entry when the catalogue is down.</summary>
    public ObservableCollection<ModEnvCatalogVersion> Versions { get; } = new();

    /// <summary>Snapshots taken before earlier version switches, newest first.</summary>
    public ObservableCollection<ModEnvBackupInfo> Backups { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _needsManualGameDir;
    [ObservableProperty] private string? _gameInstallDir;

    /// <summary>
    /// XXMI root chosen on the startup page, or null for the default "&lt;game drive&gt;\XXMI". Set by the
    /// caller before the dialog is shown; the wizard itself does not offer a picker for it.
    /// </summary>
    [ObservableProperty] private string? _customRootFolder;
    [ObservableProperty] private string? _rootFolder;
    [ObservableProperty] private string? _gameVersion;
    [ObservableProperty] private bool _hasRootFolder;
    [ObservableProperty] private bool _hasGameVersion;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private InfoBarSeverity _statusSeverity = InfoBarSeverity.Informational;
    [ObservableProperty] private bool _statusVisible;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _showTestLaunch;
    [ObservableProperty] private bool _canTestLaunch;
    [ObservableProperty] private bool _isTestingLaunch;
    [ObservableProperty] private ModEnvCatalogVersion? _selectedVersion;
    [ObservableProperty] private ModEnvBackupInfo? _selectedBackup;
    [ObservableProperty] private string _installedVersionText = NotInstalledVersionText;
    [ObservableProperty] private string _selectionHint = string.Empty;
    [ObservableProperty] private bool _hasSelectionHint;
    [ObservableProperty] private string _versionNotes = string.Empty;
    [ObservableProperty] private bool _hasVersionNotes;
    [ObservableProperty] private bool _hasXxmiVersions;
    [ObservableProperty] private bool _hasBackups;
    [ObservableProperty] private bool _isRestoring;

    /// <summary>
    /// Whether the version and backup pickers accept input. A run in flight has to lock them, otherwise the
    /// request already being executed would no longer match what the dialog displays.
    /// </summary>
    public bool CanPickVersion => !IsRunning && !IsRestoring;

    /// <summary>Version currently installed at the XXMI root, kept for the picker hint.</summary>
    private string? _installedXxmiVersion;

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(CanPickVersion));

    partial void OnIsRestoringChanged(bool value) => OnPropertyChanged(nameof(CanPickVersion));

    partial void OnSelectedVersionChanged(ModEnvCatalogVersion? value) => UpdateSelectionHint();

    /// <summary>Result of the last completed setup run (null until one completes).</summary>
    public ModEnvSetupResult? Result { get; private set; }

    public bool Succeeded => Result is { Success: true };

    public string LogText => string.Join(Environment.NewLine, LogLines);

    public ModEnvSetupViewModel(ModEnvSetupFacade facade, CommandService commandService,
        CommandHandlerService commandHandlerService, ILogger logger)
    {
        _facade = facade;
        _commandService = commandService;
        _commandHandlerService = commandHandlerService;
        _logger = logger.ForContext<ModEnvSetupViewModel>();
    }

    /// <summary>Runs the idempotency pre-check and updates the package/status display.</summary>
    public async Task RunPreCheckAsync()
    {
        IsBusy = true;
        try
        {
            var pre = await _facade.PreCheckAsync(BuildRequest());

            ApplyPreCheck(pre);

            RootFolder = pre.RootFolder;
            GameVersion = pre.GameVersion;
            HasRootFolder = !string.IsNullOrWhiteSpace(pre.RootFolder);
            HasGameVersion = !string.IsNullOrWhiteSpace(pre.GameVersion);
            NeedsManualGameDir = pre.Issues.Any(i => i.Contains("未检测到游戏安装位置"));
            HasResult = false;

            SetStatus(
                pre.Issues.Count == 0
                    ? "检测完成，已就绪。点击「开始配置」安装或更新。"
                    : string.Join(Environment.NewLine, pre.Issues),
                pre.Issues.Count == 0 ? InfoBarSeverity.Informational
                    : NeedsManualGameDir || pre.Issues.Any(i => i.Contains("版本清单"))
                        ? InfoBarSeverity.Warning
                        : InfoBarSeverity.Informational);

            // Cannot start when a game dir is still required or the manifest could not be loaded.
            CanStart = !NeedsManualGameDir && pre.Issues.All(i => !i.Contains("版本清单"));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ModEnv pre-check failed");
            SetStatus("预检失败：" + ex.Message, InfoBarSeverity.Error);
            CanStart = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AppendLog(string message)
    {
        LogLines.Add(message);
        while (LogLines.Count > 300)
            LogLines.RemoveAt(0);

        OnPropertyChanged(nameof(LogText));
        ProgressText = message;
    }

    /// <summary>Runs the full setup pipeline. <see cref="Result"/> holds the outcome on completion.</summary>
    public async Task RunSetupAsync(CancellationToken ct)
    {
        IsRunning = true;
        IsBusy = true;
        CanStart = false;
        HasResult = false;
        Result = null;
        ShowTestLaunch = false;
        CanTestLaunch = false;
        LogLines.Clear();
        OnPropertyChanged(nameof(LogText));
        ProgressText = string.Empty;

        try
        {
            var progress = new Progress<string>(AppendLog);
            AppendLog("开始配置 Mod 环境...");
            Result = await _facade.SetupAsync(BuildRequest(), progress, ct);
            HasResult = true;

            if (Result.Success)
            {
                SetStatus("配置完成，可以开始使用 Mod 了。", InfoBarSeverity.Success);
                AppendLog("配置完成。");
                foreach (var issue in Result.Issues)
                    AppendLog("注意：" + issue);
            }
            else if (Result.Cancelled)
            {
                SetStatus("已取消，未完成配置。", InfoBarSeverity.Warning);
            }
            else
            {
                SetStatus("配置失败：" + string.Join("；", Result.Issues), InfoBarSeverity.Error);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消，未完成配置。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ModEnv setup failed");
            SetStatus("配置失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsRunning = false;
            IsBusy = false;
            CanStart = !Succeeded;
            ShowTestLaunch = Succeeded;
            CanTestLaunch = Succeeded;

            // The per-package statuses were computed by the pre-check before install; refresh them now so
            // the wizard reflects what actually got installed — e.g. "已是最新" after a successful setup,
            // or the surviving packages after a mid-pipeline failure/cancel.
            await RefreshPackageStatusesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Re-runs the pre-check and rebuilds the package list so statuses reflect what's on disk. Kept separate
    /// from <see cref="RunPreCheckAsync"/> so it can update the display without resetting <see cref="Result"/>
    /// or the status message (which the setup pipeline has just set).
    /// </summary>
    private async Task RefreshPackageStatusesAsync(CancellationToken ct)
    {
        try
        {
            var pre = await _facade.PreCheckAsync(BuildRequest(), ct);
            ApplyPreCheck(pre);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to refresh package statuses after setup");
        }
    }

    /// <summary>Rebuilds the package list, the version picker and the backup list from a pre-check result.</summary>
    private void ApplyPreCheck(ModEnvPreCheck pre)
    {
        Packages.Clear();
        foreach (var package in pre.Packages)
            Packages.Add(package);

        ApplyVersions(pre);
        RefreshBackups();
    }

    /// <summary>
    /// Rebuilds the version picker from a pre-check result, keeping the user's choice when it is still
    /// offered. Only the first pre-check preselects — a later refresh must not yank the dropdown back
    /// to the default while the user is looking at it.
    /// </summary>
    private void ApplyVersions(ModEnvPreCheck pre)
    {
        var previousSelection = SelectedVersion?.Version;

        Versions.Clear();
        foreach (var version in pre.XxmiVersions)
            Versions.Add(version);

        HasXxmiVersions = Versions.Count > 0;

        _installedXxmiVersion = pre.InstalledXxmiVersion;
        InstalledVersionText = string.IsNullOrWhiteSpace(pre.InstalledXxmiVersion)
            ? NotInstalledVersionText
            : $"当前版本：v{pre.InstalledXxmiVersion}";

        var target = previousSelection ?? pre.DefaultXxmiVersion;
        SelectedVersion = Versions.FirstOrDefault(v => string.Equals(v.Version, target, StringComparison.Ordinal))
                          ?? Versions.FirstOrDefault();

        UpdateSelectionHint();
    }

    /// <summary>Rebuilds the backup list, preserving the current selection when that snapshot still exists.</summary>
    private void RefreshBackups()
    {
        var previousSelection = SelectedBackup?.Folder;

        Backups.Clear();
        foreach (var backup in _facade.ListBackups())
            Backups.Add(backup);

        // Newest first: preselecting the most recent snapshot is what the user wants after a bad switch.
        SelectedBackup = Backups.FirstOrDefault(b =>
                             string.Equals(b.Folder, previousSelection, StringComparison.OrdinalIgnoreCase))
                         ?? Backups.FirstOrDefault();

        HasBackups = Backups.Count > 0;
    }

    /// <summary>
    /// Describes what picking <see cref="SelectedVersion"/> would do relative to what is installed.
    /// </summary>
    /// <remarks>
    /// Computed locally rather than by re-running the pre-check: that resolves the game drive, which can
    /// scan every fixed disk, and is far too slow to run on each arrow-key press in the dropdown.
    /// </remarks>
    private void UpdateSelectionHint()
    {
        // Maintainer-written caveats ("v1.1.6 与 2.4 客户端不兼容") travel with the choice, so the warning
        // lands while the user is picking — not after the version is already on disk.
        VersionNotes = SelectedVersion?.Notes ?? string.Empty;
        HasVersionNotes = !string.IsNullOrWhiteSpace(VersionNotes);

        var target = SelectedVersion?.Version;
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(_installedXxmiVersion))
        {
            SelectionHint = string.Empty;
            HasSelectionHint = false;
            return;
        }

        var installed = _installedXxmiVersion;
        SelectionHint = ModEnvVersion.Compare(installed, target) switch
        {
            0 => $"已安装该版本（v{installed}），无需重复安装",
            < 0 => $"将从 v{installed} 更新到 v{target}",
            _ => $"将从 v{installed} 回退到 v{target}，当前版本会先自动备份"
        };
        HasSelectionHint = true;
    }

    private ModEnvSetupRequest BuildRequest() => new()
    {
        GameInstallDir = GameInstallDir,
        CustomRootFolder = CustomRootFolder,
        SelectedXxmiVersion = SelectedVersion?.Version
    };

    /// <summary>
    /// Restores a snapshot taken before an earlier version switch. Snapshots the current files first, so
    /// this is itself undoable.
    /// </summary>
    public async Task RunRestoreAsync(CancellationToken ct)
    {
        if (SelectedBackup is null || IsRestoring)
            return;

        IsRestoring = true;
        IsBusy = true;
        CanStart = false;
        HasResult = false;
        Result = null;
        ShowTestLaunch = false;
        CanTestLaunch = false;
        LogLines.Clear();
        OnPropertyChanged(nameof(LogText));
        ProgressText = string.Empty;

        try
        {
            var progress = new Progress<string>(AppendLog);
            AppendLog($"开始恢复备份 {SelectedBackup.DisplayName}...");
            Result = await _facade.RestoreBackupAsync(BuildRequest(), SelectedBackup, progress, ct);
            HasResult = true;

            if (Result.Success)
            {
                SetStatus("已恢复备份版本。", InfoBarSeverity.Success);
                AppendLog("恢复完成。");
                foreach (var issue in Result.Issues)
                    AppendLog("注意：" + issue);
            }
            else if (Result.Cancelled)
            {
                SetStatus("已取消，未恢复。", InfoBarSeverity.Warning);
            }
            else
            {
                SetStatus("恢复失败：" + string.Join("；", Result.Issues), InfoBarSeverity.Error);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消，未恢复。", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "ModEnv backup restore failed");
            SetStatus("恢复失败：" + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsRestoring = false;
            IsBusy = false;
            CanStart = !Succeeded;
            ShowTestLaunch = Succeeded;
            CanTestLaunch = Succeeded;

            await RefreshPackageStatusesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// After a successful setup, runs the auto-configured "Start Game" command once so the user can
    /// verify that the game launches with mods. Reuses the saved special command written by
    /// <see cref="ModEnvSetupFacade.EnsureLaunchCommandsAsync"/>; only spawns the process, the actual
    /// mod-injection check happens in-game.
    /// </summary>
    public async Task RunTestLaunchAsync()
    {
        if (!Succeeded || IsTestingLaunch)
            return;

        IsTestingLaunch = true;
        CanTestLaunch = false;
        AppendLog("正在测试启动...");

        try
        {
            var gameStartCommand =
                (await _commandService.GetCommandDefinitionsAsync()).FirstOrDefault(c => c.IsGameStartCommand);

            if (gameStartCommand is null)
            {
                AppendLog("未找到「启动游戏」命令（可能未安装 launcher 包），无法测试启动。");
                SetStatus("未找到启动命令，无法测试启动。", InfoBarSeverity.Warning);
                return;
            }

            var errors = await _commandHandlerService.CanRunCommandAsync(gameStartCommand.Id, null);
            if (errors.Count > 0)
            {
                AppendLog("启动前置检查未通过：" + string.Join("；", errors));
                SetStatus("测试启动失败：前置检查未通过。", InfoBarSeverity.Error);
                return;
            }

            var result = await Task.Run(() =>
                _commandHandlerService.RunCommandAsync(gameStartCommand.Id, null));

            if (result.IsSuccess)
            {
                AppendLog(
                    $"已发起测试启动：{gameStartCommand.CommandDisplayName}（后台注入 + 游戏直启）。请在游戏中确认 mod 生效。");
                SetStatus("测试启动已发起，请在游戏中确认 mod 是否生效。", InfoBarSeverity.Success);
            }
            else
            {
                var message = result.Exception switch
                {
                    Win32Exception e when e.NativeErrorCode == 1223 => "用户取消了 UAC 提权。",
                    Win32Exception e when e.NativeErrorCode == 740 =>
                        "需要管理员权限，请以管理员身份运行 JASM 后重试。",
                    _ => result.Exception?.Message ?? result.Notification?.Message ?? "未知错误"
                };
                AppendLog("测试启动失败：" + message);
                SetStatus("测试启动失败，请查看日志。", InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Test launch failed");
            AppendLog("测试启动失败：" + ex.Message);
            SetStatus("测试启动失败，请查看日志。", InfoBarSeverity.Error);
        }
        finally
        {
            IsTestingLaunch = false;
            CanTestLaunch = Succeeded;
        }
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusText = message;
        StatusSeverity = severity;
        StatusVisible = !string.IsNullOrWhiteSpace(message);
    }
}
