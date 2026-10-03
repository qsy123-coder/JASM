using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Views;
using Serilog;
using static GIMI_ModManager.WinUI.Services.ModHandling.ModDragAndDropService.DragAndDropFinishedArgs;

namespace GIMI_ModManager.WinUI.Services.ModHandling;

public class ModDragAndDropService
{
    private readonly ILogger _logger;
    private readonly ModInstallerService _modInstallerService;
    private readonly IWindowManagerService _windowManagerService;


    private readonly Notifications.NotificationManager _notificationManager;

    public event EventHandler<DragAndDropFinishedArgs>? DragAndDropFinished;

    public ModDragAndDropService(ILogger logger, Notifications.NotificationManager notificationManager,
        ModInstallerService modInstallerService, IWindowManagerService windowManagerService)
    {
        _notificationManager = notificationManager;
        _modInstallerService = modInstallerService;
        _windowManagerService = windowManagerService;
        _logger = logger.ForContext<ModDragAndDropService>();
    }

    // Drag and drop directly from 7zip is REALLY STRANGE, I don't know why 7zip 'usually' deletes the files before we can copy them
    // Sometimes only a few folders are copied, sometimes only a single file is copied, but usually 7zip removes them and the app just crashes
    // This code is a mess, but it works.
    public async Task<InstallMonitor?> AddStorageItemFoldersAsync(
        ICharacterModList modList, IReadOnlyList<IStorageItem>? storageItems)
    {
        if (storageItems is null || !storageItems.Any())
        {
            _logger.Warning("Drag and drop files called with null/0 storage items.");
            return null;
        }


        if (storageItems.Count > 1)
        {
            _notificationManager.ShowNotification(
                "Drag and drop called with more than one storage item, this is currently not supported", "",
                TimeSpan.FromSeconds(5));
            return null;
        }

        if (TryActivateExistingInstallWindow(modList))
            return null;

        var storageItem = storageItems.FirstOrDefault();

        InstallMonitor? installMonitor;
        if (storageItem is StorageFile)
        {
            var scanner = new DragAndDropScanner();
            var extractResult = ExtractWithCleanup(scanner, storageItem.Path);

            installMonitor = await StartInstallationAsync(extractResult, modList);

            return installMonitor;
        }

        if (storageItem is not StorageFolder sourceFolder)
        {
            _logger.Information("Unknown storage item type from drop: {StorageItemType}", storageItem.GetType());
            return null;
        }

        var destDirectoryInfo = App.GetUniqueTmpFolder();
        destDirectoryInfo.Create();
        destDirectoryInfo = new DirectoryInfo(Path.Combine(destDirectoryInfo.FullName, storageItem.Name));


        _logger.Debug("Source destination folder for drag and drop: {Source}", sourceFolder.Path);
        _logger.Debug("Copying folder {FolderName} to {DestinationFolder}", sourceFolder.Path,
            destDirectoryInfo.FullName);


        var sourceFolderPath = sourceFolder.Path;


        if (sourceFolderPath is null)
        {
            _logger.Warning("Source folder path is null, skipping.");
            return null;
        }

        var tmpFolder = Path.GetTempPath();

        Action<StorageFolder, StorageFolder> recursiveCopy = null!;

        if (sourceFolderPath.Contains(tmpFolder)) // Is 7zip
        {
            destDirectoryInfo = new DirectoryInfo(Path.Combine(destDirectoryInfo.FullName, sourceFolder.Name));
            recursiveCopy = RecursiveCopy7z;
        }
        else
        {
            destDirectoryInfo = new DirectoryInfo(Path.Combine(destDirectoryInfo.FullName, sourceFolder.Name));
            recursiveCopy = RecursiveCopy;
        }

        destDirectoryInfo.Create();

        try
        {
            recursiveCopy.Invoke(sourceFolder,
                await StorageFolder.GetFolderFromPathAsync(destDirectoryInfo.FullName));
        }
        catch (Exception)
        {
            Directory.Delete(destDirectoryInfo.FullName);
            throw;
        }

        installMonitor = await _modInstallerService.StartModInstallationAsync(destDirectoryInfo.Parent!, modList)
            .ConfigureAwait(false);
        DragAndDropFinished?.Invoke(this, new DragAndDropFinishedArgs(new List<ExtractPaths>()));
        return installMonitor;
    }

    /// <summary>
    /// 「拖到检测区」那条路：把包解压（含密码）→ 交给 <paramref name="resolveModList"/> 认角色 → 装。
    ///
    /// <para>
    /// <b>角色识别不在这里</b>：那要用游戏数据（角色名单、包内目录名比对），是 ViewModel 那边的事。
    /// 这个方法只管「包」的那一半 —— 解压、临时目录的生与死、最后交给安装向导，
    /// 也就是 <see cref="AddStorageItemFoldersAsync"/> 用的同一套（同一个「同角色已有安装窗」守卫）。
    /// </para>
    ///
    /// <para>
    /// 临时目录的清理都收在这个方法里：装成功时<b>不能</b>删（安装向导还在异步读它），
    /// 其余每一条出路都要删干净，否则用户的 <c>%TEMP%</c> 会攒下一堆解压出来的 Mod。
    /// </para>
    /// </summary>
    /// <param name="storageItem">用户拖进来的东西。只认单个文件 —— 文件夹请拖到具体角色的卡片上。</param>
    /// <param name="resolveModList">
    /// 认角色：入参是原文件名与解压结果，返回要装进哪个角色的 mod 列表。
    /// 返回 <c>null</c> = 认不出来 / 用户没选（调用方自己负责给用户说法，这里不再提示）。
    /// </param>
    public async Task<InstallMonitor?> AddDroppedPackageAsync(IStorageItem storageItem,
        Func<string, DragAndDropScanResult, Task<ICharacterModList?>> resolveModList)
    {
        if (storageItem is not StorageFile file)
        {
            _logger.Information("Auto detect drop only handles files, got {StorageItemType}",
                storageItem.GetType());
            _notificationManager.ShowNotification(
                "Only archive files can be dropped here",
                $"Drop the folder onto the character it belongs to instead",
                TimeSpan.FromSeconds(8));
            return null;
        }

        var scanner = new DragAndDropScanner();
        var scanResult = ExtractWithCleanup(scanner, file.Path);

        ICharacterModList? modList;
        try
        {
            modList = await resolveModList(file.Name, scanResult);
        }
        catch
        {
            scanner.CleanupWorkFolder(); // 认角色的过程中炸了，别把已经解压出来的东西留在 %TEMP%
            throw;
        }

        if (modList is null) // 认不出角色 / 用户在候选框里取消了
        {
            scanner.CleanupWorkFolder();
            return null;
        }

        if (TryActivateExistingInstallWindow(modList))
        {
            scanner.CleanupWorkFolder(); // 那个角色的安装窗已经开着，这次的包用不上
            return null;
        }

        return await StartInstallationAsync(scanResult, modList);
    }

    /// <summary>
    /// 把解压结果交给安装向导 —— 两条拖拽路径共用。
    ///
    /// <para>
    /// 先把 <c>JASM_TMP\&lt;guid&gt;</c> 这层包装剥掉，再看包内容根<b>自己带不带 ini</b>：
    /// 带的（多合一包）就<b>钦定根为 mod 根</b>，整个包当一个 Mod 装 —— 否则向导会去取
    /// 树里第一个 <c>mod.ini</c>（多半是包里某个子目录），用户只装到包的一个碎片，
    /// 而按键切换那些逻辑还留在没被装进去的根 ini 里。
    /// </para>
    ///
    /// <para>
    /// 不带的（一堆互不相干的 Mod 打成包）维持原样，仍然交给向导自己的启发式去猜 —— 那条路走了很久，
    /// 没有明确证据不该动。判定见 <see cref="ModPackageRootResolver"/>。
    /// </para>
    ///
    /// <para>
    /// <b>树根给内容根的父目录、钦定的根给内容根自己</b>，这不是绕远路：向导认 mod 根靠
    /// 「在树里按路径选中一项」（<c>ModInstallerVM</c> 的 <c>RootFolder.GetByPath</c> → <c>SetRootFolderAsync</c>），
    /// 而 <c>GetByPath</c> 只在<b>子节点</b>里找，树根本身（<c>RootFolder</c> 类）不是 <c>FileSystemItem</c>、
    /// 选中不了。把内容根直接当树根就会选不中 ⇒ <c>LastSelectedRootFolder</c> 一直为空 ⇒
    /// 「添加模组」按钮（它的 CanExecute 刷新只挂在 <c>SetRootFolderAsync</c> 里）**永远是灰的**。
    /// 父目录同时让选中项露在树的第一层，用户一眼能看到选中了哪个文件夹。
    /// </para>
    /// </summary>
    private Task<InstallMonitor> StartInstallationAsync(DragAndDropScanResult scanResult,
        ICharacterModList modList)
    {
        var extractedRoot = new DirectoryInfo(scanResult.ExtractedFolder.FullPath);
        var contentRoot = ModPackageRootResolver.ResolveContentRoot(extractedRoot);

        if (!ModPackageRootResolver.LooksLikeSelfContainedModRoot(contentRoot))
            return _modInstallerService.StartModInstallationAsync(extractedRoot, modList);

        _logger.Information("The package is a single mod root ('{ModRoot}'), installing it as a whole",
            contentRoot.Name);

        return _modInstallerService.StartModInstallationAsync(contentRoot.Parent ?? extractedRoot, modList,
            setup: options => options.ModRootFolder = contentRoot);
    }

    /// <summary>
    /// 解压（加密包直接套内置的 <see cref="ModArchivePassword.Default"/>，<b>不再问用户</b>），
    /// <b>失败时把临时目录收拾掉</b>。
    ///
    /// <para>
    /// 卡片路径与检测区路径共用这一份：清理这件事不能让哪条路忘了写 ——
    /// 忘一条，用户的 <c>%TEMP%\JASM_TMP</c> 就会攒下一堆解压出来的 Mod。
    /// 密码只在内存里过一道，不进日志、不进异常、不进通知（取命令行那条日志由
    /// <c>DragAndDropScanner.RedactCommand</c> 把 <c>-p</c> 打码）。
    /// </para>
    ///
    /// <para>
    /// 没有「问到成功为止」的循环了：密码不对就抛
    /// <see cref="GIMI_ModManager.Core.Helpers.ArchiveExtractionException"/>（<c>WrongPassword</c>），
    /// 由调用方按 reason 给用户「自己解压好再拖文件夹」的出路（见
    /// <c>CharactersViewModel.ShowExtractionFailureNotification</c>）。
    /// </para>
    /// </summary>
    private DragAndDropScanResult ExtractWithCleanup(DragAndDropScanner scanner, string path)
    {
        try
        {
            return scanner.ScanAndGetContents(path, ModArchivePassword.Default);
        }
        catch
        {
            scanner.CleanupWorkFolder(); // 解压失败：半截内容也别留在 %TEMP%
            throw;
        }
    }

    /// <summary>
    /// 同一个角色已经有一个安装窗开着的话：提示 + 把那个窗拉到前面，返回 <c>true</c>。
    /// 卡片路径与检测区路径共用 —— 这两条路都不该在同一个角色上并行开两个安装窗。
    /// </summary>
    private bool TryActivateExistingInstallWindow(ICharacterModList modList)
    {
        if (_windowManagerService.GetWindow(modList) is not { } window)
            return false;

        _notificationManager.ShowNotification(
            $"Please finish adding the mod for '{modList.Character.DisplayName}' first",
            $"JASM does not support multiple mod installs for the same character",
            TimeSpan.FromSeconds(8));

        PInvoke.PlaySound("SystemAsterisk", null,
            SND_FLAGS.SND_ASYNC | SND_FLAGS.SND_ALIAS | SND_FLAGS.SND_NODEFAULT);

        App.MainWindow.DispatcherQueue.TryEnqueue(() => window.Activate());
        return true;
    }

    // ReSharper disable once InconsistentNaming
    private void RecursiveCopy7z(StorageFolder sourceFolder, StorageFolder destinationFolder)
    {
        var tmpFolder = Path.GetTempPath();
        var parentDir = new DirectoryInfo(Path.GetDirectoryName(sourceFolder.Path)!);
        parentDir.MoveTo(Path.Combine(tmpFolder, "JASM_TMP", Guid.NewGuid().ToString("N")));

        var modDir = parentDir.EnumerateDirectories().FirstOrDefault();

        if (modDir is null)
        {
            throw new DirectoryNotFoundException("No valid mod folder found in archive. Loose files are ignored");
        }

        RecursiveCopy(StorageFolder.GetFolderFromPathAsync(modDir.FullName).GetAwaiter().GetResult(),
            destinationFolder);
    }

    private void RecursiveCopy(StorageFolder sourceFolder, StorageFolder destinationFolder)
    {
        if (sourceFolder == null || destinationFolder == null)
            throw new ArgumentNullException("Source and destination folders cannot be null.");

        var sourceDir = new DirectoryInfo(sourceFolder.Path);

        // Copy files
        foreach (var file in sourceDir.GetFiles())
        {
            _logger.Debug("Copying file {FileName} to {DestinationFolder}", file.FullName, destinationFolder.Path);
            if (!File.Exists(file.FullName))
            {
                _logger.Warning("File {FileName} does not exist.", file.FullName);
                continue;
            }

            file.CopyTo(Path.Combine(destinationFolder.Path, file.Name), true);
        }
        // Recursively copy subfolders

        foreach (var subFolder in sourceDir.GetDirectories())
        {
            _logger.Debug("Copying subfolder {SubFolderName} to {DestinationFolder}", subFolder.FullName,
                destinationFolder.Path);
            if (!Directory.Exists(subFolder.FullName))
            {
                _logger.Warning("Subfolder {SubFolderName} does not exist.", subFolder.FullName);
                continue;
            }

            var newSubFolder = new DirectoryInfo(Path.Combine(destinationFolder.Path, subFolder.Name));
            newSubFolder.Create();
            RecursiveCopy(StorageFolder.GetFolderFromPathAsync(subFolder.FullName).GetAwaiter().GetResult(),
                StorageFolder.GetFolderFromPathAsync(newSubFolder.FullName).GetAwaiter().GetResult());
        }
    }


    public async Task AddModFromUrlAsync(ICharacterModList modList, Uri uri)
    {
        var windowKey = $"ModPage_{modList.Character.InternalName}";
        if (_windowManagerService.GetWindow(windowKey) is { } window)
        {
            PInvoke.PlaySound("SystemAsterisk", null,
                SND_FLAGS.SND_ASYNC | SND_FLAGS.SND_ALIAS | SND_FLAGS.SND_NODEFAULT);

            App.MainWindow.DispatcherQueue.TryEnqueue(() => window.Activate());
            return;
        }


        var modWindow = new GbModPageWindow(uri, modList.Character);
        _windowManagerService.CreateWindow(modWindow, identifier: windowKey);
        await Task.Delay(100);
        modWindow.BringToFront();
    }

    public class DragAndDropFinishedArgs : EventArgs
    {
        public DragAndDropFinishedArgs(IReadOnlyCollection<ExtractPaths> extractResults)
        {
            ExtractResults = extractResults;
        }

        public IReadOnlyCollection<ExtractPaths> ExtractResults { get; }

        public record ExtractPaths
        {
            public ExtractPaths(string sourcePath, string extractedFolderPath)
            {
                SourcePath = sourcePath;
                ExtractedFolderPath = Path.EndsInDirectorySeparator(extractedFolderPath)
                    ? extractedFolderPath
                    : extractedFolderPath + Path.DirectorySeparatorChar;
            }

            public string SourcePath { get; init; }
            public string ExtractedFolderPath { get; init; }

            public void Deconstruct(out string SourcePath, out string ExtractedFolderPath)
            {
                SourcePath = this.SourcePath;
                ExtractedFolderPath = this.ExtractedFolderPath;
            }
        }
    }
}