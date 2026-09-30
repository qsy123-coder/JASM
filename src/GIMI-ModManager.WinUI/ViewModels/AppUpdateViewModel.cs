using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ErrorOr;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.AppManagement.Updating;
using GIMI_ModManager.WinUI.Services.Notifications;
using Microsoft.UI.Xaml;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels;

/// <summary>
/// 「有新版本可用」与「开始更新」这份状态的唯一持有者，注册为**单例**。
/// 设置页与角色概览页都绑它：两个入口共享同一个下载进度，也就不会各自复制一份
/// 「folder 版拉起外部更新器 / 单文件版进程内自更新」的分支逻辑。
/// </summary>
public partial class AppUpdateViewModel : ObservableObject
{
    private readonly UpdateChecker _updateChecker;
    private readonly AutoUpdaterService _autoUpdaterService;
    private readonly SingleFileSelfUpdater _singleFileSelfUpdater;
    private readonly NotificationManager _notificationManager;
    private readonly ILanguageLocalizer _localizer;
    private readonly ILogger _logger;

    [ObservableProperty] private string _latestVersion = string.Empty;

    /// <summary>有新版本可提示（且没被用户忽略掉）时置 true，两个页面的更新入口都绑它。</summary>
    [ObservableProperty] private bool _showNewVersionAvailable = false;

    /// <summary>
    /// 「这次更新了什么」的链接（清单的 notesUrl，缺省退回 appsettings 里的发布页）。类型是
    /// <see cref="Uri"/> 而不是 string：<c>LinkButton.Link</c> 是 Uri 依赖属性，x:Bind 不会替我们
    /// 把字符串转成 Uri。为 null 时 LinkButton 点不动（其 handler 自己会跳过），不会崩。
    /// </summary>
    [ObservableProperty] private Uri? _latestVersionNotesUrl;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(IgnoreNewVersionCommand))]
    private bool _canIgnoreUpdate = false;

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

    public AppUpdateViewModel(UpdateChecker updateChecker, AutoUpdaterService autoUpdaterService,
        SingleFileSelfUpdater singleFileSelfUpdater, NotificationManager notificationManager,
        ILanguageLocalizer localizer, ILogger logger)
    {
        _updateChecker = updateChecker;
        _autoUpdaterService = autoUpdaterService;
        _singleFileSelfUpdater = singleFileSelfUpdater;
        _notificationManager = notificationManager;
        _localizer = localizer;
        _logger = logger.ForContext<AppUpdateViewModel>();

        _updateChecker.NewVersionAvailable += UpdateCheckerOnNewVersionAvailable;

        // 构造函数快照：更新检查（ActivationService 启动时拉起）可能早于本单例第一次被解析完成，
        // 那时事件已经发过了，只订阅事件会漏掉这一次。
        if (_updateChecker.LatestRetrievedVersion is not null)
        {
            LatestVersion = VersionFormatter(_updateChecker.LatestRetrievedVersion);
            LatestVersionNotesUrl = ToNotesUri(_updateChecker.LatestReleaseNotesUrl);
        }

        RefreshAvailability();
    }

    private void UpdateCheckerOnNewVersionAvailable(object? sender, UpdateChecker.NewVersionEventArgs e)
    {
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            // 空版本号是 UpdateChecker 的「状态变了」信号（例如刚忽略掉一个版本），不是真有新版本。
            if (e.Version != new Version())
            {
                LatestVersion = VersionFormatter(e.Version);
                LatestVersionNotesUrl = ToNotesUri(_updateChecker.LatestReleaseNotesUrl);
            }

            RefreshAvailability();
        });
    }

    /// <summary>
    /// 「有新版本可提示」= 检查器抓到了比当前版本新的版本（<see cref="UpdateChecker.LatestRetrievedVersion"/>
    /// 只在那种情况下才被赋值），且用户没有忽略过它。
    /// </summary>
    private void RefreshAvailability()
    {
        var latest = _updateChecker.LatestRetrievedVersion;
        ShowNewVersionAvailable = latest is not null && latest != _updateChecker.IgnoredVersion;
        CanIgnoreUpdate = ShowNewVersionAvailable;
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

    [RelayCommand(CanExecute = nameof(CanIgnoreUpdate))]
    private async Task IgnoreNewVersion()
    {
        await _updateChecker.IgnoreCurrentVersionAsync();
    }

    private static string VersionFormatter(Version version)
    {
        return $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>把更新源给的说明链接转成 <c>LinkButton.Link</c> 要的 Uri；给不出合法绝对地址就返回 null。</summary>
    private static Uri? ToNotesUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}