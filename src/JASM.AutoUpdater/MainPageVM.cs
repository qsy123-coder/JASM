using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GIMI_ModManager.Core.Helpers;
using Windows.Storage;
using Windows.System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JASM.AutoUpdater;

public partial class MainPageVM : ObservableRecipient
{
    private readonly string WorkDir = Path.Combine(Path.GetTempPath(), "JASM_Auto_Updater");
    private string _zipPath = string.Empty;
    private DirectoryInfo _extractedJasmFolder = null!;
    private DirectoryInfo _installedJasmFolder = null!;
    private string _newJasmExePath = string.Empty;

    private readonly string _7zPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"Assets\7z\", "7z.exe");

    [ObservableProperty] private bool _inStartupView = true;

    [ObservableProperty] private bool _updateProcessStarted = false;
    [ObservableProperty] private string _latestVersion = "-----";

    /// <summary>仓库的 releases 页，作为清单没给 notesUrl 时的兜底"看看更新了什么"地址。</summary>
    private const string DefaultReleasesPageUrl = "https://github.com/qsy123-coder/JASM/releases";

    [ObservableProperty] private Uri _defaultBrowserUri = new(DefaultReleasesPageUrl);

    public ObservableCollection<LogEntry> ProgressLog { get; } = new();

    public UpdateProgress UpdateProgress { get; } = new();

    public Version InstalledVersion { get; }

    [ObservableProperty] private bool _isLoading = false;
    [ObservableProperty] private bool _finishedSuccessfully = false;

    [ObservableProperty] private bool _stopped;
    [ObservableProperty] private string? _stopReason;

    public MainPageVM(string installedJasmVersion)
    {
        InstalledVersion = Version.TryParse(installedJasmVersion, out var version) ? version : new Version(0, 0, 0, 0);
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task StartUpdateAsync(CancellationToken cancellationToken)
    {
        UpdateProgress.Reset();
        IsLoading = true;
        InStartupView = false;
        UpdateProcessStarted = true;
        Stopped = false;
        StopReason = null;

        Log(InstalledVersion.Equals(new Version(0, 0, 0, 0))
            ? "Could not determine installed JASM version..."
            : $"Installed JASM version: {InstalledVersion}");


        try
        {
            var release = await IsNewerVersionAvailable(cancellationToken);
            UpdateProgress.NextStage();
            if (Stopped || release is null)
                return;

            await Task.Delay(1000, cancellationToken);
            await DownloadLatestVersion(release, cancellationToken);
            UpdateProgress.NextStage();

            if (Stopped)
            {
                CleanUp();
                return;
            }

            await Task.Delay(1000, cancellationToken);
            await UnzipLatestVersion(cancellationToken);
            UpdateProgress.NextStage();

            if (Stopped)
            {
                CleanUp();
                return;
            }

            await Task.Delay(1000, cancellationToken);
            await InstallLatestVersion();
            if (Stopped)
            {
                CleanUp();
                return;
            }

            UpdateProgress.NextStage();
        }
        catch (TaskCanceledException e)
        {
            Stop("User cancelled");
        }
        catch (OperationCanceledException e)
        {
            Stop("User cancelled");
        }
        catch (Exception e)
        {
            Log("An error occurred!", e.Message);
            Serilog.Log.Error(e, "An error occurred! Full error");
            Stop(e.Message);
        }
        finally
        {
            IsLoading = false;
        }

        if (Stopped)
        {
            CleanUp();
            return;
        }

        CleanUp();
        Finish();
    }


    private void Finish()
    {
        IsLoading = false;
        FinishedSuccessfully = true;
    }

    private async Task<UpdatePackage?> IsNewerVersionAvailable(CancellationToken cancellationToken)
    {
        var resolved = await ResolveLatestReleaseAsync(cancellationToken);
        var newestRelease = resolved.Release;

        Log($"Newest version found: {newestRelease?.Version ?? "none"}");

        if (newestRelease is null)
        {
            Stop($"Could not determine the newest JASM version. {resolved.Diagnostic}");
            return null;
        }

        // 宽松解析（旧代码这里是 new Version(tag)，脏 tag 会抛到 StartUpdateAsync 的 catch 里报错退出）。
        if (!AppUpdateManifestParser.TryParseVersion(newestRelease.Version, out var newestVersion) ||
            newestVersion is null)
        {
            Stop($"The newest published version \"{newestRelease.Version}\" could not be parsed");
            return null;
        }

        if (newestVersion <= InstalledVersion)
        {
            Stop("Installed version is newer than or equal to the newest published version");
            return null;
        }

        // folder 包 = 清单里的 kind "folder"，缺省退回 JASM_ 前缀（维护者手写清单漏字段、或回退到
        // GitHub 通道时，asset 的 kind 就是按这个名字前缀反推出来的）。
        var jasmAsset = AppUpdateManifestParser.FindAsset(newestRelease, AppUpdateAsset.KindFolder, "JASM_");

        if (jasmAsset?.Url is null)
        {
            Stop(
                "Could not find the JASM archive in the newest release. This may be due to the developer having to manually upload it which can take a few minutes. " +
                "If the problem persists, then you may have to update JASM manually");
            return null;
        }

        var release = new UpdatePackage
        {
            Version = newestVersion,
            PreRelease = newestRelease.Prerelease,
            PublishedAt = newestRelease.PublishedAt,
            DownloadUrl = new Uri(jasmAsset.Url),
            BrowserUrl = ResolveBrowserUri(newestRelease.NotesUrl),
            FileName = string.IsNullOrWhiteSpace(jasmAsset.Name) ? "JASM.zip" : jasmAsset.Name
        };

        LatestVersion = release.Version.ToString();

        DefaultBrowserUri = release.BrowserUrl;

        return release;
    }

    /// <summary>清单里的 notesUrl 是远端字符串，得自己验一遍；给不出合法 http(s) 地址就退回仓库 releases 页。</summary>
    private static Uri ResolveBrowserUri(string? notesUrl) =>
        Uri.TryCreate(notesUrl, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : new Uri(DefaultReleasesPageUrl);

    public void Stop(string stopReason)
    {
        Stopped = true;
        StopReason = stopReason;
    }

    private async Task DownloadLatestVersion(UpdatePackage updatePackage, CancellationToken cancellationToken)
    {
        if (Directory.Exists(WorkDir))
        {
            Directory.Delete(WorkDir, true);
        }

        Directory.CreateDirectory(WorkDir);


        _zipPath = Path.Combine(WorkDir, updatePackage.FileName);
        if (File.Exists(_zipPath))
        {
            File.Delete(_zipPath);
        }

        var httpClient = CreateHttpClient();
        httpClient.DefaultRequestHeaders.Add("Accept", "application/octet-stream");

        Log("Downloading latest version...");
        var result = await httpClient.GetAsync(updatePackage.DownloadUrl, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!result.IsSuccessStatusCode)
        {
            Stop($"Failed to download latest version. Status Code: {result.StatusCode}, Reason: {result.ReasonPhrase}");
            return;
        }

        await using var stream = await result.Content.ReadAsStreamAsync(cancellationToken);

        await using var fileStream = File.Create(_zipPath);
        await stream.CopyToAsync(fileStream, cancellationToken);
        Log($"Latest version downloaded from {updatePackage.DownloadUrl}");
    }

    private async Task UnzipLatestVersion(CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo =
            {
                FileName = _7zPath,
                Arguments = $"x \"{_zipPath}\" -o\"{WorkDir}\" -y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        Log("Extracting downloaded zip file...");
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            Stop($"Failed to extract downloaded zip file. Exit Code: {process.ExitCode}");
            return;
        }


        _extractedJasmFolder = new DirectoryInfo(WorkDir).EnumerateDirectories().FirstOrDefault(folder =>
            folder.Name.StartsWith("JASM", StringComparison.CurrentCultureIgnoreCase))!;

        if (_extractedJasmFolder is null)
        {
            Stop("Failed to find JASM folder in extracted zip file");
            return;
        }

        Log($"JASM Application folder extracted successfully. Path: {_extractedJasmFolder.FullName}");
    }

    private async Task InstallLatestVersion()
    {
        _installedJasmFolder = new DirectoryInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory)).Parent!;
        if (_installedJasmFolder is null)
        {
            Stop("Failed to find installed JASM folder in path");
            return;
        }

        const string jasmExe = "JASM - Just Another Skin Manager.exe";
        _newJasmExePath = Path.Combine(_installedJasmFolder.FullName, jasmExe);

        var containsJasmExe = false;
        var containsSystemFiles = false;
        var systemFileFound = string.Empty;
        var warningFiles = new List<string>();

        foreach (var fileSystemInfo in _installedJasmFolder.EnumerateFileSystemInfos())
        {
            if (fileSystemInfo.Name.Equals(jasmExe,
                    StringComparison.CurrentCultureIgnoreCase))
            {
                containsJasmExe = true;
            }

            if (SystemEntries.WindowsEntries.Any(fileEntry => fileSystemInfo.Name.Equals(fileEntry,
                    StringComparison.CurrentCultureIgnoreCase)))
            {
                containsSystemFiles = true;
                systemFileFound = fileSystemInfo.Name;
            }

            if (fileSystemInfo.Name.Equals("3DMigoto Loader.exe", StringComparison.CurrentCultureIgnoreCase))
            {
                warningFiles.Add(fileSystemInfo.Name);
            }

            if (fileSystemInfo.Name.Equals("3dmigoto", StringComparison.CurrentCultureIgnoreCase))
            {
                warningFiles.Add(fileSystemInfo.Name);
            }

            if (fileSystemInfo.Name.Equals("Mods", StringComparison.CurrentCultureIgnoreCase))
            {
                warningFiles.Add("Mods");
            }
        }

        if (!containsJasmExe)
        {
            Stop(
                $"Failed to find '{jasmExe}' in installed JASM folder. Path: {_installedJasmFolder}");
            return;
        }

        if (containsSystemFiles)
        {
            Stop(
                $"JASM folder seems to contain windows system files, this should never happen. File Found: '{systemFileFound}' at " +
                $"Path: {_installedJasmFolder}");
            return;
        }

        var result = await ShowDeleteWarning(warningFiles);

        if (result is ContentDialogResult.Secondary or ContentDialogResult.None)
        {
            Stop("User cancelled");
            return;
        }

        var autoUpdaterFolder = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);

        Log("Deleting old files...", $"Path: {_installedJasmFolder.FullName}");

        string[] doNotDeleteFiles = ["Elevator.exe", "JASM - Just Another Skin Manager.exe.WebView2", "logs"];

        foreach (var fileSystemInfo in _installedJasmFolder.EnumerateFileSystemInfos())
        {
            if (fileSystemInfo.Name.StartsWith(autoUpdaterFolder.Name,
                    StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }

            if (doNotDeleteFiles.Any(fileEntry => fileSystemInfo.Name.Equals(fileEntry,
                    StringComparison.CurrentCultureIgnoreCase)))
            {
                Serilog.Log.Logger.Information("Not deleting file: {FileName}", fileSystemInfo.Name);
                continue;
            }

            if (fileSystemInfo is DirectoryInfo directoryInfo)
                directoryInfo.Delete(true);
            else
                fileSystemInfo.Delete();
        }

        Log("Copying new files...", $"Path: {_installedJasmFolder.FullName}");

        await Task.Run(() => { CopyFilesRecursively(_extractedJasmFolder, _installedJasmFolder); });

        Log("JASM updated successfully");
    }

    // https://stackoverflow.com/questions/58744/copy-the-entire-contents-of-a-directory-in-c-sharp
    private static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target)
    {
        foreach (var dir in source.GetDirectories())
            CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name));
        foreach (var file in source.GetFiles())
            file.CopyTo(Path.Combine(target.FullName, file.Name));
    }

    private void CleanUp()
    {
        Log("Cleaning up work dir...", WorkDir);
        if (Directory.Exists(WorkDir))
        {
            Directory.Delete(WorkDir, true);
        }

        Log("Clean up finished");
    }

    /// <summary>
    /// COS 上的更新清单（首选通道）。与主程序 <c>appsettings.json</c> 的 <c>AppUpdate:ManifestUrl</c>
    /// 指向同一个对象 —— 本 exe 是随包分发的独立进程，拿不到主程序的配置，只能写死。
    /// </summary>
    private const string ManifestUrl =
        "https://jasm-modenv-1327973389.cos.ap-guangzhou.myqcloud.com/app/update.json";

    /// <summary>GitHub 回退通道。清单拉不到时用它 —— 这也是 COS 出事时把资产重新挂回 release 就能救回来的那条路。</summary>
    private const string ReleasesApiUrl = "https://api.github.com/repos/qsy123-coder/JASM/releases?per_page=2";

    /// <summary>
    /// 「当前该更新到哪个版本、包在哪」—— 与主程序共用 Core 里的同一份解析器（COS 清单优先，GitHub 回退），
    /// 由 csproj 源链接编译进来。共用是硬要求：主程序负责亮徽标、本进程负责实际下载，两边各自判断"最新版
    /// 是哪个"就会出现"徽标说 2.31.0、这里却下 2.30.0"这种不报错的偏差。
    /// </summary>
    private async Task<AppUpdateReleaseResolver.Result> ResolveLatestReleaseAsync(CancellationToken cancellationToken)
    {
        Serilog.Log.Information("Checking for latest version...");

        using var httpClient = CreateHttpClient();

        return await AppUpdateReleaseResolver.ResolveAsync(httpClient, ManifestUrl, ReleasesApiUrl,
            cancellationToken);
    }

    private HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 2
        });
        httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "JASM-Just_Another_Skin_Manager-Update-Checker");
        return httpClient;
    }

    /// <summary>待安装的更新包（版本 / 下载地址 / 说明页）。来源可能是 COS 清单，也可能是 GitHub 回退。</summary>
    private class UpdatePackage
    {
        public Version Version;
        public bool PreRelease;
        public DateTime PublishedAt = DateTime.MinValue;

        public Uri BrowserUrl = null!;
        public Uri DownloadUrl = null!;
        public string FileName = null!;
    }

    private void Log(string logMessage, string? footer = null)
    {
        Serilog.Log.Information("Install Step {StepIndex} | Msg: {Message} | footer: {Footer}", (ProgressLog.Count + 1),
            logMessage, footer);

        var logEntry = new LogEntry
        {
            Message = logMessage,
            Footer = footer,
            TimeStamp = DateTime.Now
        };
        ProgressLog.Insert(0, logEntry);
    }

    [RelayCommand]
    private async Task StartJasm()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _newJasmExePath,
            UseShellExecute = true
        });
        await Task.Delay(500);
        Application.Current.Exit();
    }


    private async Task<ContentDialogResult> ShowDeleteWarning(ICollection<string> warningFiles)
    {
        var content = new ContentDialog
        {
            Title = "Warning",
            PrimaryButtonText = "Continue",
            DefaultButton = ContentDialogButton.Primary,
            SecondaryButtonText = "Cancel",
            XamlRoot = App.MainWindow.Content.XamlRoot
        };

        var stackPanel = new StackPanel();

        stackPanel.Children.Add(new TextBlock
        {
            Text =
                "All files/folders in the installed JASM folder will be deleted permanently!\n" +
                "This excludes the update folder itself. This action cannot be undone.\n" +
                $"JASM Directory: {_installedJasmFolder.FullName}",
            TextWrapping = TextWrapping.WrapWholeWords,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 0, 0, 10)
        });

        if (warningFiles.Any())
            stackPanel.Children.Add(new TextBlock
            {
                Text = "These files/folders do not belong to JASM and will be deleted as well:\n" +
                       string.Join("\n", warningFiles),
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.WrapWholeWords,
                Margin = new Thickness(0, 0, 0, 10)
            });

        stackPanel.Children.Add(new Button()
        {
            Content = "Open installed JASM folder...",
            Margin = new Thickness(0, 8, 0, 8),
            Command = new AsyncRelayCommand(async () =>
            {
                await Launcher.LaunchFolderAsync(
                    await StorageFolder.GetFolderFromPathAsync(_installedJasmFolder.FullName));
            })
        });

        content.Content = stackPanel;

        return await content.ShowAsync();
    }
}

internal class ApiAssets
{
    public string? name;
    public string? browser_download_url;
}

public class LogEntry
{
    public string Message { get; set; } = string.Empty;
    public string? Footer { get; set; }
    public DateTime TimeStamp { get; set; } = DateTime.Now;
}

public partial class UpdateProgress : ObservableObject
{
    [ObservableProperty] private bool _checkingForLatestUpdate = false;

    [ObservableProperty] private bool _downloadingLatestUpdate = false;

    [ObservableProperty] private bool _extractingLatestUpdate = false;

    [ObservableProperty] private bool _installingLatestUpdate = false;

    public void Reset()
    {
        CheckingForLatestUpdate = false;
        DownloadingLatestUpdate = false;
        ExtractingLatestUpdate = false;
        InstallingLatestUpdate = false;
    }

    public void NextStage()
    {
        if (!CheckingForLatestUpdate)
        {
            CheckingForLatestUpdate = true;
        }
        else if (!DownloadingLatestUpdate)
        {
            DownloadingLatestUpdate = true;
        }
        else if (!ExtractingLatestUpdate)
        {
            ExtractingLatestUpdate = true;
        }
        else if (!InstallingLatestUpdate)
        {
            InstallingLatestUpdate = true;
        }
    }
}