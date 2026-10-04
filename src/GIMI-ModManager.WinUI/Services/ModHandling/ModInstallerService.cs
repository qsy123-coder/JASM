using System.Diagnostics.CodeAnalysis;
using CommunityToolkitWrapper;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Entities.Mods.Contract;
using GIMI_ModManager.Core.Entities.Mods.Helpers;
using GIMI_ModManager.Core.Entities.Mods.SkinMod;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.ViewModels;
using GIMI_ModManager.WinUI.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModHandling;

public class ModInstallerService(
    IWindowManagerService windowManagerService,
    ILocalSettingsService localSettingsService)
{
    private readonly ILocalSettingsService _localSettingsService = localSettingsService;
    private readonly IWindowManagerService _windowManagerService = windowManagerService;

    public async Task<InstallMonitor> StartModInstallationAsync(DirectoryInfo modFolder, ICharacterModList modList,
        ICharacterSkin? inGameSkin = null, Action<InstallOptions>? setup = null)
    {
        ArgumentNullException.ThrowIfNull(modFolder);
        ArgumentNullException.ThrowIfNull(modList);


        if (inGameSkin is not null && modList.Character is not ICharacter)
            throw new ArgumentException("The mod list must be a character mod list if inGameSkin is not null");

        var dispatcherQueue = DispatcherQueue.GetForCurrentThread() ?? App.MainWindow.DispatcherQueue;

        var modOptions = new InstallOptions();
        setup?.Invoke(modOptions);


        var monitor =
            await dispatcherQueue.EnqueueAsync(() => InternalStartAsync(modFolder, modList, inGameSkin, modOptions));

        return monitor;
    }

    /// <summary>
    /// 把一个**已经解压好的目录**整个装进这个角色的列表，**不打开向导窗**。
    ///
    /// 落盘那一手与向导点「添加模组」完全同一条（<see cref="ModInstallation.AddModAsync"/> →
    /// <c>SkinManagerService.AddMod</c>：把目录搬进角色的 Mod 目录并登记），差别只有三点：
    /// 不建窗口、不问用户要名字（用目录名）、装完把它启用。
    ///
    /// 给「把包拖到游戏内浮窗上」那条路用 —— 浮窗那块地方没有向导的容身之处，
    /// 为一次拖拽弹一个窗也不是拖拽该有的手感。
    /// </summary>
    public async Task<ISkinMod> InstallFolderSilentlyAsync(DirectoryInfo modFolder, ICharacterModList modList)
    {
        ArgumentNullException.ThrowIfNull(modFolder);
        ArgumentNullException.ThrowIfNull(modList);

        var options = new AddModOptions { NewModFolderName = modFolder.Name };

        // 预览图要和向导那条路一样自动认出来（`preview.png` / `0.png` 这些约定名）。
        // 不设的话缩略图就退回占位图 —— 实机反馈：「preview 图片都有的，缩略图却不显示」。
        // 认图策略（根目录 → 往下探一层）与启动时的兜底共用一份实现，别在这里另写一套。
        try
        {
            var detected = SkinModHelpers.DetectModPreviewImageIncludingSubfolders(modFolder.FullName);

            if (detected is not null)
                options.ModImage = detected;
        }
        catch (Exception e)
        {
            // 认不出预览图不影响装：缩略图退占位图即可
            Serilog.Log.Warning(e, "静默安装：自动识别预览图失败 {Folder}", modFolder.FullName);
        }

        using var installation = ModInstallation.Start(modFolder, modList);

        var skinMod = await installation.AddModAsync(options).ConfigureAwait(false);

        // 只**启用**新装的这个，不去动用户原有的启用组合：静默通道不该顺手改别的东西。
        // （向导那条路会「只启用它」，那是用户当着面勾的，不是这里该替他做的决定。）
        //
        // 只有带 DISABLED_ 前缀的才需要这一步：新装进来的通常没有前缀，也就是**已经启用**了，
        // 那种情况再调 EnableMod 会抛「Cannot enable a enabled mod」—— 那不是失败，是已经到位。
        if (ModFolderHelpers.FolderHasDisabledPrefix(skinMod.Name))
        {
            try
            {
                modList.EnableMod(skinMod.Id);
            }
            catch (Exception e)
            {
                // 装是装上了，只是没启用 —— 让用户自己去勾一下就行，不该因此把整次安装判失败
                Serilog.Log.Warning(e, "静默安装后启用 Mod 失败: {Mod}", skinMod.Name);
            }
        }

        return skinMod;
    }

    private async Task<InstallMonitor> InternalStartAsync(DirectoryInfo modFolder, ICharacterModList modList,
        ICharacterSkin? inGameSkin = null, InstallOptions? options = null)
    {
        var modTitle = Guid.TryParse(modFolder.Name, out _)
            ? modFolder.EnumerateDirectories().FirstOrDefault()?.Name
            : modFolder.Name;

        modTitle ??= modFolder.Name;

        var modInstallerSettings =
            await _localSettingsService.ReadOrCreateSettingAsync<ModInstallerSettings>(ModInstallerSettings.Key);

        var modInstallPage = new ModInstallerPage(modList, modFolder, inGameSkin, options);
        var modInstallWindow = new WindowEx()
        {
            SystemBackdrop = new MicaBackdrop(),
            Title = $"Mod Installer Helper: {modTitle}",
            Content = modInstallPage,
            Width = 1200,
            Height = 750,
            MinHeight = 415,
            MinWidth = 1024,
            IsAlwaysOnTop = modInstallerSettings.ModInstallerWindowOnTop
        };
        _windowManagerService.CreateWindow(modInstallWindow, modList);

        return new InstallMonitor(modInstallPage, modInstallWindow);
    }
}

public class InstallOptions
{
    public Uri? ModUrl { get; set; }
    public Guid? ExistingModIdToUpdate { get; set; }

    /// <summary>
    /// 由调用方钦定的 mod 根。
    ///
    /// <para>
    /// <b>为什么需要</b>：多合一包（根目录自己带 ini，子目录是它的变体/资源）必须<b>整体</b>当一个 Mod 装。
    /// 交给 <see cref="ModInstallation.AutoSetModRootFolder"/> 去猜的话，它会取「整棵树里第一个
    /// <c>mod.ini</c>」—— 对这类包就是某个子目录，用户只装到包的一个碎片，而按键切换之类的逻辑
    /// 还留在没被装进去的根 ini 里。
    /// </para>
    ///
    /// <para>置空（或目录不存在）时走原有的启发式，单 Mod 包那条路一点没变。</para>
    /// </summary>
    public DirectoryInfo? ModRootFolder { get; set; }
}

public sealed class InstallMonitor : IDisposable
{
    private readonly TaskCompletionSource<CloseRequestedArgs> _taskCompletionSource = new();
    private readonly ModInstallerPage _modInstallerPage;
    private readonly WindowEx _modInstallerWindow;
    private CancellationTokenRegistration? _cancellationTokenRegistration;

    public Task Task => _taskCompletionSource.Task;

    public InstallMonitor(ModInstallerPage modInstallerPage, WindowEx modInstallerWindow)
    {
        _modInstallerPage = modInstallerPage;
        _modInstallerWindow = modInstallerWindow;

        _modInstallerPage.CloseRequested += (_, e) =>
        {
            _taskCompletionSource.SetResult(e);
            _modInstallerWindow.Close();
        };

        _modInstallerWindow.Closed += (_, _) =>
        {
            if (!_taskCompletionSource.Task.IsCompleted)
                _taskCompletionSource.TrySetResult(new CloseRequestedArgs(CloseRequestedArgs.CloseReasons.Canceled));
        };
    }

    public Task<CloseRequestedArgs> WaitForCloseAsync(CancellationToken? cancellationToken = null)
    {
        if (cancellationToken is not null)
            _cancellationTokenRegistration = cancellationToken.Value.Register(() =>
            {
                if (!_taskCompletionSource.Task.IsCompleted)
                {
                    _taskCompletionSource.TrySetCanceled();
                    _modInstallerWindow.Close();
                }
            });

        return _taskCompletionSource.Task;
    }

    public void Dispose()
    {
        _cancellationTokenRegistration?.Dispose();
        _cancellationTokenRegistration = null;
    }
}

public sealed class ModInstallation : IDisposable
{
    private readonly ModCrawlerService _modCrawlerService = App.GetService<ModCrawlerService>();
    private readonly ISkinManagerService _skinManagerService = App.GetService<ISkinManagerService>();
    private readonly ICharacterModList _destinationModList;
    private readonly DirectoryInfo _originalModFolder;
    private readonly List<FileStream> _lockedFiles = new();

    private FileInfo? _jasmConfigFile;
    public DirectoryInfo ModFolder { get; private set; }
    private DirectoryInfo? _shaderFixesFolder;

    // TODO: Enable later
    private List<FileInfo> _shaderFixesFiles = new();


    private ModInstallation(DirectoryInfo originalModFolder, ICharacterModList destinationModList)
    {
        _originalModFolder = originalModFolder;
        _destinationModList = destinationModList;
        SetRootModFolder(originalModFolder);
        LockFiles();
    }

    private void LockFiles()
    {
        foreach (var fileInfo in _originalModFolder.GetFiles("*", SearchOption.AllDirectories))
        {
            var fileStream = fileInfo.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            _lockedFiles.Add(fileStream);
        }
    }

    // Lock mod folder
    public static ModInstallation Start(DirectoryInfo modFolder, ICharacterModList destinationModList)
    {
        return new ModInstallation(modFolder, destinationModList);
    }

    [MemberNotNull(nameof(ModFolder))]
    public void SetRootModFolder(DirectoryInfo newRootFolder)
    {
        if (newRootFolder.FullName == _shaderFixesFolder?.FullName)
            throw new ArgumentException("The new root folder is the same as the current shader fixes folder");

        if (!newRootFolder.Exists)
            throw new DirectoryNotFoundException($"The folder {newRootFolder.FullName} does not exist");

        ModFolder = new DirectoryInfo(newRootFolder.FullName);
        _jasmConfigFile = _modCrawlerService.GetFirstJasmConfigFileAsync(ModFolder, false);
    }

    public void SetShaderFixesFolder(DirectoryInfo shaderFixesFolder)
    {
        // TODO: Enable later
        return;

        if (shaderFixesFolder.FullName == ModFolder.FullName)
            throw new ArgumentException("The new shader fixes folder is the same as the current root folder");

        if (!shaderFixesFolder.Exists)
            throw new DirectoryNotFoundException($"The folder {shaderFixesFolder.FullName} does not exist");

        _shaderFixesFiles.Clear();
        _shaderFixesFiles.AddRange(shaderFixesFolder.GetFiles("*.txt", SearchOption.TopDirectoryOnly));
        _shaderFixesFolder = shaderFixesFolder;
    }


    public DirectoryInfo? AutoSetModRootFolder()
    {
        var jasmConfigFile = _modCrawlerService.GetFirstJasmConfigFileAsync(_originalModFolder);
        DirectoryInfo? modRootFolder = null;
        if (jasmConfigFile is not null)
        {
            modRootFolder = new DirectoryInfo(jasmConfigFile.DirectoryName!);
        }
        else
        {
            var mergedIniFile = _modCrawlerService.GetMergedIniFile(_originalModFolder);
            if (mergedIniFile is not null)
                modRootFolder = new DirectoryInfo(mergedIniFile.DirectoryName!);
        }

        modRootFolder ??= _originalModFolder.EnumerateDirectories().FirstOrDefault();


        if (modRootFolder is null)
            return null;

        SetRootModFolder(modRootFolder);
        return modRootFolder;
    }

    public DirectoryInfo? AutoSetShaderFixesFolder()
    {
        var shaderFixesFolder = _modCrawlerService.GetShaderFixesFolder(_originalModFolder);
        if (shaderFixesFolder is null)
            return null;

        SetShaderFixesFolder(shaderFixesFolder);
        return shaderFixesFolder;
    }

    public async Task<ModSettings?> TryReadModSettingsAsync()
    {
        if (_jasmConfigFile is null)
            return null;

        await RemoveJasmConfigFileLockAsync().ConfigureAwait(false);
        try
        {
            return await SkinModSettingsManager.ReadSettingsAsync(_jasmConfigFile.FullName);
        }
        catch (Exception)
        {
            // ignored
        }

        LockJasmConfigFile();
        return null;
    }

    public ISkinMod? AnyDuplicateName()
    {
        var skinEntries = _destinationModList.Mods;


        foreach (var skinEntry in skinEntries)
        {
            if (ModFolderHelpers.FolderNameEquals(skinEntry.Mod.Name, ModFolder.Name))
                return skinEntry.Mod;
        }

        return null;
    }

    public async Task<ISkinMod> RenameAndAddAsync(AddModOptions options, ISkinMod dupeMod,
        string dupeModNewFolderName, string? dupeModNewCustomName = null)
    {
        if (dupeModNewFolderName.IsNullOrEmpty() && options.NewModFolderName.IsNullOrEmpty())
            throw new ArgumentException("The new mod folder name and old folder name cannot be null or empty");

        if (ModFolderHelpers.FolderNameEquals(dupeModNewFolderName, options.NewModFolderName!))
            throw new ArgumentException("The new mod folder name and old folder name cannot be the same");

        ReleaseLockedFiles();

        var skinMod = await CreateSkinModWithOptionsAsync(options);
        var newModRenamed = false;
        if (!options.NewModFolderName.IsNullOrEmpty() &&
            !ModFolderHelpers.FolderNameEquals(skinMod.Name, options.NewModFolderName))
        {
            var tmpFolder = App.GetUniqueTmpFolder();
            skinMod = await SkinMod.CreateModAsync(skinMod.CopyTo(tmpFolder.FullName).FullPath).ConfigureAwait(false);
            skinMod.Rename(options.NewModFolderName);
            newModRenamed = true;
        }

        if (!dupeModNewFolderName.IsNullOrEmpty() &&
            !ModFolderHelpers.FolderNameEquals(dupeMod.Name, dupeModNewFolderName))
        {
            _destinationModList.RenameMod(dupeMod, dupeModNewFolderName);
        }

        // Set new custom name for dupe mod
        if (!dupeModNewCustomName.IsNullOrEmpty())
        {
            var dupeModSettings = await dupeMod.Settings.ReadSettingsAsync().ConfigureAwait(false);

            await dupeMod.Settings
                .SaveSettingsAsync(
                    dupeModSettings.DeepCopyWithProperties(customName: NewValue<string?>.Set(dupeModNewCustomName)))
                .ConfigureAwait(false);
        }

        _skinManagerService.AddMod(skinMod, _destinationModList, newModRenamed);
        return skinMod;
    }


    public async Task<ISkinMod> AddAndReplaceAsync(ISkinMod dupeMod, AddModOptions? options = null)
    {
        ReleaseLockedFiles();
        var skinMod = await CreateSkinModWithOptionsAsync(options).ConfigureAwait(false);
        try
        {
            _destinationModList.DeleteModBySkinEntryId(dupeMod.Id);
        }
        catch (DirectoryNotFoundException)
        {
        }

        return _skinManagerService.AddMod(skinMod, _destinationModList);
    }

    public async Task<ISkinMod> AddModAsync(AddModOptions? options = null)
    {
        if (AnyDuplicateName() is not null)
            throw new InvalidOperationException("There is already a mod with the same name");

        ReleaseLockedFiles();
        var skinMod = await CreateSkinModWithOptionsAsync(options);

        return _skinManagerService.AddMod(skinMod, _destinationModList);
    }

    private async Task<ISkinMod> CreateSkinModWithOptionsAsync(AddModOptions? options = null)
    {
        await RemoveJasmConfigFileLockAsync().ConfigureAwait(false);

        var skinMod = await SkinMod.CreateModAsync(ModFolder, true).ConfigureAwait(false);

        if (options is null)
            return skinMod;

        var settings = new ModSettings(
            id: skinMod.Id,
            customName: options.ModName,
            imagePath: options.ModImage,
            author: options.Author,
            modUrl: Uri.TryCreate(options.ModUrl, UriKind.Absolute, out var modUrl) ? modUrl : null,
            description: options.Description,
            dateAdded: DateTime.Now
        );
        await skinMod.Settings.SaveSettingsAsync(settings, new SaveSettingsOptions { DeleteOldImage = false })
            .ConfigureAwait(false);
        return skinMod;
    }

    private async Task RemoveJasmConfigFileLockAsync()
    {
        if (_jasmConfigFile is not null)
        {
            _jasmConfigFile.Refresh();
            var jasmFs = _lockedFiles.FirstOrDefault(file =>
                file.Name.Equals(_jasmConfigFile.FullName, StringComparison.OrdinalIgnoreCase));

            if (jasmFs is not null)
            {
                await jasmFs.DisposeAsync().ConfigureAwait(false);
                _lockedFiles.Remove(jasmFs);
            }
        }
    }

    private void LockJasmConfigFile()
    {
        if (_jasmConfigFile is not null)
        {
            _jasmConfigFile.Refresh();
            if (!_jasmConfigFile.Exists) return;

            var jasmFs = _jasmConfigFile.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            _lockedFiles.Add(jasmFs);
        }
    }

    private void ReleaseLockedFiles()
    {
        foreach (var fileStream in _lockedFiles.ToArray())
        {
            fileStream.Dispose();
            _lockedFiles.Remove(fileStream);
        }

        Log.Debug("Released locked files, {time}", DateTime.Now);
    }

    public void Dispose()
    {
        ReleaseLockedFiles();
    }
}

public record AddModOptions
{
    public string? NewModFolderName { get; set; }
    public string? ModName { get; set; }
    public Uri? ModImage { get; set; }
    public string? ModUrl { get; set; }
    public string? Author { get; set; }
    public string? Description { get; set; }
}