using Windows.Storage;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using CommunityToolkit.Mvvm.Messaging;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.ViewModels.Messages;
using GIMI_ModManager.WinUI.Views;
using Serilog;
using static GIMI_ModManager.WinUI.Services.ModHandling.ModDragAndDropService.DragAndDropFinishedArgs;

namespace GIMI_ModManager.WinUI.Services.ModHandling;

/// <summary>
/// 拖拽安装走到哪一步了。浮窗拿它决定显示「正在解压…」还是「正在安装…」——
/// 实测这两段各占差不多一半时间，只说「在装」等于让用户干等十几秒不知道还要多久。
/// </summary>
public enum ModInstallStage
{
    Extracting,
    Installing,

    /// <summary>
    /// 这一单没装成（认不出角色、解压失败、落盘失败都算）。
    ///
    /// 批量拖拽时浮窗靠它记账 —— 单看返回值分不出成败：静默安装那条路把异常吞在自己的通知里、
    /// 一律返回 null。
    /// </summary>
    Failed
}

public class ModDragAndDropService
{
    private readonly ILogger _logger;
    private readonly ModInstallerService _modInstallerService;
    private readonly IWindowManagerService _windowManagerService;


    private readonly Notifications.NotificationManager _notificationManager;
    private readonly ModNotificationManager _modNotificationManager;

    public event EventHandler<DragAndDropFinishedArgs>? DragAndDropFinished;

    public ModDragAndDropService(ILogger logger, Notifications.NotificationManager notificationManager,
        ModNotificationManager modNotificationManager,
        ModInstallerService modInstallerService, IWindowManagerService windowManagerService)
    {
        _notificationManager = notificationManager;
        _modNotificationManager = modNotificationManager;
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
            // 与浮窗那条路同一套：解压摊在目标那块盘上（同卷时装=改名）、并搬去后台线程
            var scanner = new DragAndDropScanner(modList.AbsModsFolderPath);
            var extractResult = await Task.Run(() => ExtractWithCleanup(scanner, storageItem.Path))
                .ConfigureAwait(false);

            installMonitor = await StartInstallationAsync(extractResult, modList, installSilently: false)
                .ConfigureAwait(false);

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
    /// <param name="installSilently">
    /// true = 不弹安装向导，直接把解压出来那一整个文件夹装进目标角色（装完启用）。
    /// 游戏内浮窗那条拖拽路用的就是它 —— 详见 <see cref="InstallSilentlyAsync"/>。
    /// </param>
    /// <param name="stageProgress">
    /// 装到哪一步了（解压中 / 安装中）。回调**在调用方的线程上下文里**触发（<c>Progress&lt;T&gt;</c>
    /// 会自己回 UI 线程），浮窗拿它显示进度动画。
    /// </param>
    /// <param name="targetFolderHint">
    /// 这单最终落在哪块盘上（角色的 Mod 目录、或整个 Mod 根目录都行）—— 解压会摊到那块盘上，
    /// 装的时候就能靠改名而不是复制。见 <see cref="DragAndDropScanner.ResolveWorkRoot"/>。
    /// </param>
    public async Task<InstallMonitor?> AddDroppedPackageAsync(IStorageItem storageItem,
        Func<string, DragAndDropScanResult, Task<ICharacterModList?>> resolveModList,
        bool installSilently = false,
        IProgress<ModInstallStage>? stageProgress = null,
        string? targetFolderHint = null)
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

        var scanner = new DragAndDropScanner(targetFolderHint);

        // 解压是纯 IO，也是整条路上最贵的一段（实测大包 10s+）——**搬去后台线程**。
        // 留在 UI 线程上就是浮窗连同主窗口一起冻住十几秒：窗口不重绘、进度动画也不会动。
        stageProgress?.Report(ModInstallStage.Extracting);
        // ⚠️ **这里不能加 ConfigureAwait(false)**：它只影响"续体在哪跑"，而搬线程这件事
        // Task.Run 已经做了（解压照样在后台）。续体回到 UI 线程是**必须的** ——
        // 紧接着的 resolveModList 要做 UI 的事（认不出角色时弹候选框，见 CharacterPickerService），
        // 落在别的线程上就是 RPC_E_WRONG_THREAD（0x8001010E）：实机表现是
        // 「拖进来的包认不出来 → 弹框那一步直接报错，什么都没装上」。
        // 顺带一提，这条路上弹框并不常见（认得出角色就走不到），所以这个坑很晚才被踩到。
        var scanResult = await Task.Run(() => ExtractWithCleanup(scanner, file.Path));

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

        if (installSilently == false && TryActivateExistingInstallWindow(modList))
        {
            scanner.CleanupWorkFolder(); // 那个角色的安装窗已经开着，这次的包用不上
            return null;
        }

        return await StartInstallationAsync(scanResult, modList, installSilently, stageProgress);
    }

    /// <summary>
    /// 把解压结果交给安装向导 —— 两条拖拽路径共用。
    ///
    /// <para>
    /// 落盘根由 <see cref="ModPackageRootResolver.ResolveInstallRoot"/> 定：剥掉 <c>JASM_TMP\&lt;guid&gt;</c>
    /// 与包内的纯包装层，停在「自己带 ini 的那一层」，并改叫包名（不含 <c>.exe</c> / <c>.zip</c> 这类容器后缀）。
    /// </para>
    ///
    /// <para>
    /// <b>为什么要剥到 ini 那一层</b>：以前只剥 <c>&lt;guid&gt;</c>，把「exe 名那一层」整个落盘，于是装出来是
    /// <c>DISABLED_xxx.exe\xxx\PebbleRox-…\single_layer_depth.ini</c> 这种三层目录。XXMI 扫不到 mod 根上的 ini，
    /// 实机表现是<b>启动直接崩溃</b>；用户手动把嵌套去掉就正常。
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
    private async Task<InstallMonitor?> StartInstallationAsync(DragAndDropScanResult scanResult,
        ICharacterModList modList, bool installSilently, IProgress<ModInstallStage>? stageProgress = null)
    {
        var extractedRoot = new DirectoryInfo(scanResult.ExtractedFolder.FullPath);

        // 内容一个字节都不动，只剥包装层 + 换外层名字：装出来的目录必须直接摆着 ini
        // （多一层 XXMI 就启动崩溃），名字必须是包名（不能带 .exe）。
        var contentRoot = ModPackageRootResolver.ResolveInstallRoot(extractedRoot);

        _logger.Information("Installing the extracted package as one mod ('{ModRoot}')", contentRoot.Name);

        stageProgress?.Report(ModInstallStage.Installing);

        if (installSilently)
            return await InstallSilentlyAsync(contentRoot, scanResult.TempFolder, modList, stageProgress)
                .ConfigureAwait(false);

        return await _modInstallerService.StartModInstallationAsync(contentRoot.Parent ?? extractedRoot, modList,
            setup: options => options.ModRootFolder = contentRoot).ConfigureAwait(false);
    }

    /// <summary>
    /// 静默装：不建向导窗，直接把这一整个目录当成一个 Mod 装进角色的列表（装完启用）。
    ///
    /// 给「把包拖到游戏内浮窗上」用 —— 浮窗那块地方没有向导的容身之处，为一次拖拽弹一个窗
    /// 也不是拖拽该有的手感。落盘那一手与向导点「添加模组」完全相同
    /// （<c>ModInstallerService.InstallFolderSilentlyAsync</c>），所以目录名、启用行为都跟着那条路走。
    /// </summary>
    /// <param name="workFolderRoot">本次解压独占的临时目录（<c>JASM_TMP\&lt;guid&gt;</c>），装完整个收掉。</param>
    private async Task<InstallMonitor?> InstallSilentlyAsync(DirectoryInfo contentRoot, string workFolderRoot,
        ICharacterModList modList, IProgress<ModInstallStage>? stageProgress = null)
    {
        var characterName = modList.Character.DisplayName;

        try
        {
            var mod = await _modInstallerService.InstallFolderSilentlyAsync(contentRoot, modList)
                .ConfigureAwait(false);

            _notificationManager.ShowNotification("已安装",
                $"「{mod.Name}」已装到 {characterName}", TimeSpan.FromSeconds(6));

            // 与向导那条路同一条通知：角色卡片上的「新增」角标靠它；顺带也把浮窗的列表热更新上
            await _modNotificationManager.AddModNotification(new ModNotification
            {
                ModId = mod.Id,
                CharacterInternalName = modList.Character.InternalName,
                ModCustomName = mod.Name,
                ModFolderName = mod.Name,
                ShowOnOverview = true,
                AttentionType = AttentionType.Added,
                Message = $"「{mod.Name}」已安装"
            }).ConfigureAwait(false);

            // 带上角色：浮窗收到会切到这个角色，用户一眼就看到刚装的东西
            WeakReferenceMessenger.Default.Send(
                new ModInstalledMessage(this, modList.Character.InternalName));

            // 没有向导窗，也就没有 InstallMonitor 可给
            return null;
        }
        catch (Exception e)
        {
            _logger.Error(e, "静默安装失败: {ModRoot}", contentRoot.FullName);
            _notificationManager.ShowNotification("安装失败",
                $"「{contentRoot.Name}」装到 {characterName} 失败：{e.Message}", TimeSpan.FromSeconds(10));

            // 把失败也报出去：异常在这层就被吞了、返回值一律 null，批量装那边只靠这个分得出成败
            stageProgress?.Report(ModInstallStage.Failed);
            return null;
        }
        finally
        {
            CleanupExtractedTemp(contentRoot, workFolderRoot);
        }
    }

    /// <summary>
    /// 解压出来的临时目录用完就清 —— 装的时候是搬/复制进角色目录，这份原件不该留在临时目录里。
    ///
    /// <para>
    /// 内容根现在可能埋在 <c>&lt;工作根&gt;\&lt;guid&gt;\&lt;包名&gt;\…</c> 里好几层（包装层被剥掉了，
    /// 但目录还在原地），所以要从它往上把空壳逐个收掉。只删<b>确实空了</b>的目录，
    /// 而且绝不越过 <paramref name="workFolderRoot"/> 这一层 —— 剥掉的包装层里可能还躺着预览图之类
    /// 没被搬走的散文件，那种目录留着（下一次拖拽也不会复用它，只是别误删别的路径）。
    /// </para>
    /// </summary>
    private void CleanupExtractedTemp(DirectoryInfo contentRoot, string workFolderRoot)
    {
        try
        {
            // 搬走之后这个目录**本来就不存在了**，而 `DirectoryInfo.Exists` 用的是建对象那一刻的
            // 缓存，常常仍然回 true —— 于是 Delete 抛 DirectoryNotFoundException。那不是失败，
            // 正是期望状态：不接住它，后面的「删空包装目录」会被一并跳过，JASM_TMP 就会一直
            // 堆空壳（实测 464 个）。
            try
            {
                if (contentRoot.Exists)
                    contentRoot.Delete(true);
            }
            catch (DirectoryNotFoundException)
            {
            }

            if (string.IsNullOrWhiteSpace(workFolderRoot))
                return;

            var directory = contentRoot.Parent;
            while (directory is not null &&
                   directory.FullName.StartsWith(workFolderRoot, StringComparison.OrdinalIgnoreCase))
            {
                // 用静态 API 而不是 DirectoryInfo 的方法：后者带缓存，搬走之后还会说「存在」
                var path = directory.FullName;
                var parent = directory.Parent;

                if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                    break;

                if (Directory.Exists(path))
                    Directory.Delete(path, false);

                directory = parent;
            }
        }
        catch (Exception e)
        {
            _logger.Warning(e, "清理解压临时目录失败: {Path}", contentRoot.FullName);
        }
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