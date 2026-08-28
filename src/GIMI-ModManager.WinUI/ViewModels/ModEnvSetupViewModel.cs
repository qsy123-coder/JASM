using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
    private readonly ILogger _logger;

    public ObservableCollection<ModEnvPackagePreCheck> Packages { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _needsManualGameDir;
    [ObservableProperty] private string? _gameInstallDir;
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

    /// <summary>Result of the last completed setup run (null until one completes).</summary>
    public ModEnvSetupResult? Result { get; private set; }

    public bool Succeeded => Result is { Success: true };

    public string LogText => string.Join(Environment.NewLine, LogLines);

    public ModEnvSetupViewModel(ModEnvSetupFacade facade, ILogger logger)
    {
        _facade = facade;
        _logger = logger.ForContext<ModEnvSetupViewModel>();
    }

    /// <summary>Runs the idempotency pre-check and updates the package/status display.</summary>
    public async Task RunPreCheckAsync()
    {
        IsBusy = true;
        try
        {
            var request = new ModEnvSetupRequest { GameInstallDir = GameInstallDir };
            var pre = await _facade.PreCheckAsync(request);

            Packages.Clear();
            foreach (var package in pre.Packages)
                Packages.Add(package);

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
        LogLines.Clear();
        OnPropertyChanged(nameof(LogText));
        ProgressText = string.Empty;

        try
        {
            var request = new ModEnvSetupRequest { GameInstallDir = GameInstallDir };
            var progress = new Progress<string>(AppendLog);
            AppendLog("开始配置 Mod 环境...");
            Result = await _facade.SetupAsync(request, progress, ct);
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
        }
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusText = message;
        StatusSeverity = severity;
        StatusVisible = !string.IsNullOrWhiteSpace(message);
    }
}
