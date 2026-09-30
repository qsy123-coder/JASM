using System.Reflection;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Options;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.AppManagement.Updating;

public sealed class UpdateChecker
{
    private readonly ILogger _logger;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly Notifications.NotificationManager _notificationManager;
    private readonly AppUpdateManifestService _appUpdateManifestService;

    public Version CurrentVersion { get; private set; }
    public Version? LatestRetrievedVersion { get; private set; }

    /// <summary>
    /// "What's new" page for <see cref="LatestRetrievedVersion"/>. Null until a new version is found, and
    /// null afterwards too when neither the manifest nor the config offered a usable link.
    /// </summary>
    public string? LatestReleaseNotesUrl { get; private set; }

    public event EventHandler<NewVersionEventArgs>? NewVersionAvailable;
    private Version? _ignoredVersion;
    public Version? IgnoredVersion => _ignoredVersion;
    private bool DisableChecker;
    private CancellationTokenSource _cancellationTokenSource;

    public UpdateChecker(ILogger logger, ILocalSettingsService localSettingsService,
        Notifications.NotificationManager notificationManager, AppUpdateManifestService appUpdateManifestService,
        CancellationToken cancellationToken = default)
    {
        _logger = logger.ForContext<UpdateChecker>();
        _localSettingsService = localSettingsService;
        _notificationManager = notificationManager;
        _appUpdateManifestService = appUpdateManifestService;
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var version = Assembly.GetExecutingAssembly().GetName().Version;

        if (version is null)
        {
            _logger.Error("Failed to get current version");
            DisableChecker = true;
            CurrentVersion = new Version(0, 0, 0, 0);
            return;
        }

        CurrentVersion = version;
    }

    public async Task InitializeAsync()
    {
        var options = await _localSettingsService.ReadSettingAsync<UpdateCheckerOptions>(UpdateCheckerOptions.Key) ??
                      new UpdateCheckerOptions();
        if (options.IgnoreNewVersion is not null)
            _ignoredVersion = options.IgnoreNewVersion;

        if (options.IgnoreNewVersion is not null && options.IgnoreNewVersion <= CurrentVersion)
        {
            options.IgnoreNewVersion = null;
            await _localSettingsService.SaveSettingAsync(UpdateCheckerOptions.Key, options);
        }


        InitCheckerLoop(_cancellationTokenSource.Token);
    }

    public async Task IgnoreCurrentVersionAsync()
    {
        var options = await _localSettingsService.ReadSettingAsync<UpdateCheckerOptions>(UpdateCheckerOptions.Key) ??
                      new UpdateCheckerOptions();
        options.IgnoreNewVersion = LatestRetrievedVersion;
        await _localSettingsService.SaveSettingAsync(UpdateCheckerOptions.Key, options);
        _ignoredVersion = LatestRetrievedVersion;
        OnNewVersionAvailable(new Version());
    }

    private void InitCheckerLoop(CancellationToken cancellationToken)
    {
        Task.Factory.StartNew(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
                try
                {
                    await CheckForUpdatesAsync(cancellationToken);
                    await Task.Delay(TimeSpan.FromHours(2), cancellationToken);
                }
                catch (TaskCanceledException e)
                {
                }
                catch (OperationCanceledException e)
                {
                }
                catch (Exception e)
                {
                    _logger.Error(e, "Failed to check for updates. Stopping Update checker");
                    break;
                }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }


    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken)
    {
        if (DisableChecker)
            return;

        var release = await GetLatestReleaseAsync(cancellationToken);

        if (release is null)
        {
            _logger.Warning("No published release found; skipping this update check");
            return;
        }

        // Parsed leniently (unlike the old new Version(tag) this replaced): a dirty version string in the
        // manifest or a stray GitHub tag must not throw out of the polling loop, which would stop update
        // checks for the rest of the session with nothing visible in the UI.
        if (!AppUpdateManifestParser.TryParseVersion(release.Version, out var latestVersion) ||
            latestVersion is null)
        {
            _logger.Warning("Published version {Version} is not parseable; ignoring it", release.Version);
            return;
        }

        if (CurrentVersion == latestVersion || LatestRetrievedVersion == latestVersion)
        {
            _logger.Debug("No new version available");
            return;
        }


        if (CurrentVersion < latestVersion)
        {
            if (_ignoredVersion is not null && _ignoredVersion >= latestVersion)
                return;
            LatestRetrievedVersion = latestVersion;
            LatestReleaseNotesUrl = _appUpdateManifestService.GetNotesUrl(release);
            OnNewVersionAvailable(latestVersion);
        }
    }

    /// <summary>
    /// Asks <see cref="AppUpdateManifestService"/> (COS manifest, GitHub fallback) for the current release.
    /// Returns null when neither channel produced a usable one, which the caller treats as "no update".
    /// </summary>
    private async Task<AppUpdateRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var result = await _appUpdateManifestService.ResolveLatestAsync(cancellationToken);
        return result.Release;
    }


    public void CancelAndStop()
    {
        if (_cancellationTokenSource is null || _cancellationTokenSource.IsCancellationRequested)
            return;
        var cts = _cancellationTokenSource;
        _cancellationTokenSource = null!;
        cts.Cancel();
        cts.Dispose();
        _logger.Debug("JASM update checker stopped");
    }

    private void OnNewVersionAvailable(Version e)
    {
        NewVersionAvailable?.Invoke(this, new NewVersionEventArgs(e));
    }


    public class NewVersionEventArgs : EventArgs
    {
        public Version Version { get; }

        public NewVersionEventArgs(Version version)
        {
            Version = version;
        }
    }
}