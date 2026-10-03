using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.GamesService.Models;
using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.Core.Services.Downloading;
using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.Models;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.ModHandling;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.ViewModels;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModStore;

/// <summary>
/// 一键部署的落地动作：**下载完 → 归档入库 → 拉起安装向导**（PRD Story 2）。
/// 挂在 <see cref="ModDownloadQueue.CompletedHandler"/> 上，队列每跑完一个任务就叫它一次。
///
/// 三件事按顺序做，每一步都有它必须存在的理由：
/// <list type="number">
///   <item><b>入库</b>（<see cref="ModArchiveRepository.CopyAndTrackModArchiveAsync"/>）：
///         商店下的是临时文件，不入库的话「下载前查缓存」永远查不到，
///         而且用户一按取消就把那份文件删了（暂存目录是「取消即清」的语义）。
///         入库同时也把 mod/file id 与 md5 写进文件名，是后面「已装 / 可更新」判定的依据。</item>
///   <item><b>解压</b>：向导吃的是**文件夹**（与「添加模组」选一个文件夹完全一样），不吃压缩包。</item>
///   <item><b>拉起向导</b>：零中间确认（PRD 明确不要二次确认弹窗），打开就交给用户操作。</item>
/// </list>
///
/// <b>不在这里等向导关掉</b>：<see cref="ModDownloadQueue.CompletedHandler"/> 是在队列的工作线程上
/// await 的，在这里等用户把向导点完，后面的下载就会一直卡着 —— 用户开着向导去喝杯水，
/// 队列就停了。所以这里只负责「把向导开起来」，向导关闭之后的收尾（提示 / 安装记录）
/// 交给一条分离的续体（见 <see cref="TrackOutcomeAsync"/>）。
/// </summary>
public sealed class ModStoreDeploymentService(
    ILogger logger,
    ModArchiveRepository archiveRepository,
    ArchiveService archiveService,
    IGameService gameService,
    ISkinManagerService skinManagerService,
    ModInstallerService modInstallerService,
    IWindowManagerService windowManagerService,
    ModStoreInstallIndex installIndex,
    NotificationManager notificationManager)
{
    private readonly ILogger _logger = logger.ForContext<ModStoreDeploymentService>();
    private readonly NotificationManager _notificationManager = notificationManager;

    /// <summary>
    /// 装完（或就地更新完）一个商店 mod、且**安装记录已经落盘**之后发一次（无载荷）。
    ///
    /// 商店页据此把卡片上的「已安装」角标重打一遍：安装向导是**独立窗口**，装完时商店页还停在
    /// 原地（不会再导航一次、也就不会重新取数），少了这个信号角标要等用户换个筛选条件或者按一次
    /// 刷新才出现 —— 看起来就像没装上。
    ///
    /// 事件在**后台续体**上发出（<see cref="TrackOutcomeAsync"/> 跑在向导关闭之后，不在 UI 线程），
    /// 订阅方自己负责切回 UI 线程改绑定源。
    /// </summary>
    public event EventHandler? InstallRecorded;

    /// <summary>
    /// 「这个 mod 现在还装着吗」（PRD Story 4 的已装角标）。判定在
    /// <see cref="ModStoreInstallStatus.IsInstalled"/>，这里只负责把「本地那份 mod 现在在哪」
    /// 喂给它 —— 用户在 JASM 里给 mod 改过名 / 挪过位置时记录里那条路径已经过期，必须问 mod 列表。
    /// </summary>
    public bool IsInstalled(string? modId) =>
        ModStoreInstallStatus.IsInstalled(installIndex.Find(modId),
            id => skinManagerService.GetModById(id)?.FullPath);

    /// <summary>
    /// 这个 mod 的安装记录（没装过时为 null）。给「可更新」判定用 —— 它要比对记录里装的是**哪一份文件**。
    /// </summary>
    public ModStoreInstallRecord? FindRecord(string? modId) => installIndex.Find(modId);

    /// <summary>
    /// 队列的完成回调。**会向上抛**：队列把异常记成 <see cref="ModDownloadItem.FollowUpError"/>
    /// （「文件是好的、但后续动作没成」），显示在下载面板那一行上 —— 成功时静默返回。
    /// </summary>
    public async Task DeployAsync(ModDownloadItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var archive = await IngestAsync(item, cancellationToken).ConfigureAwait(false);

        // 这一步抛了就往上走：下载面板那一行会显示「下好了但没能装」，用户能看到原因。
        if (!await OpenInstallerAsync(archive, item.Request).ConfigureAwait(false))
            throw new InvalidOperationException(
                $"『{ResolveModList(item.Request).Character.DisplayName}』的安装向导已经开着");
    }

    /// <summary>
    /// 归档缓存命中：不下载，直接部署（PRD Story 2 的验收项之一）。
    ///
    /// 与 <see cref="DeployAsync"/> 走的是同一条后半段，区别只在文件从哪来 ——
    /// 缓存里的那份**已经在库里了**，没有入库与清暂存这两步；也正因为不经过队列，
    /// 这里的参数是**请求**而不是任务项。
    /// </summary>
    /// <returns>真的把向导开起来了。<c>false</c> = 该角色已有安装在进行（调用方按需提示）。</returns>
    public Task<bool> DeployCachedAsync(ModDownloadRequest request, ModArchiveHandle archive)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(archive);

        return OpenInstallerAsync(archive, request);
    }

    /// <summary>
    /// 「这份文件本地已经有归档了吗」—— 下载**之前**问一次，命中就不必再从网上拉一遍
    /// （PRD Story 2 的验收项）。
    ///
    /// 认的是 md5 而不是 mod / 文件 id：作者换个文件名、重压一遍就会在 GameBanana 上
    /// 变成一个新的文件 id，而内容没变的话 md5 就没变 —— 那正是「装出来的东西一模一样」的意思。
    /// 反过来，**没有 md5 就一律不命中**：少了这个凭据，任何「看起来像」的判断都可能是错的，
    /// 而装错版本比多下一次糟得多。
    /// </summary>
    /// <returns>命中的归档；没有（或上游没给 md5）时 null。</returns>
    public async Task<ModArchiveHandle?> TryGetCachedArchiveAsync(ModDownloadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ExpectedMd5))
            return null;

        // FirstOrDefaultAsync 沿途会跳过已经不存在的文件，所以这里不必再查一次 Exists。
        // 比较方式与 GameBananaCoreService.GetLocalModArchiveByMd5HashAsync 保持一致 ——
        // 两处对「这份文件下过没有」必须给出同一个答案。
        var archive = await archiveRepository
            .FirstOrDefaultAsync(handle => handle.MD5Hash == request.ExpectedMd5, cancellationToken)
            .ConfigureAwait(false);

        if (archive is not null)
            _logger.Information("Archive cache hit for {Key}, download can be skipped", request.Key);

        return archive;
    }

    // ─── 下载 → 归档 ───────────────────────────────────────────

    /// <summary>
    /// 把暂存目录里下好的那份收进归档库，并**删掉暂存副本**。
    ///
    /// 删的理由：不删的话每装一个 mod 就在 <c>%LOCALAPPDATA%</c> 下多留一份同样大小的文件。
    /// 归档库那份才是长久的（缓存命中查的就是它），暂存目录只是「下载中的落脚点」。
    /// 入库失败时不删 —— 那时文件还是唯一的副本。
    /// </summary>
    private async Task<ModArchiveHandle> IngestAsync(ModDownloadItem item, CancellationToken cancellationToken)
    {
        var downloaded = new FileInfo(item.DestinationPath);
        if (!downloaded.Exists)
            throw new FileNotFoundException(
                $"下载的文件不在暂存目录里：{item.FileName}（可能刚被取消或清理）", item.DestinationPath);

        var identifier = new GbModFileIdentifier(new GbModId(item.Key.ModId), new GbModFileId(item.Key.ModFileId));

        var archive = await archiveRepository.CopyAndTrackModArchiveAsync(downloaded.FullName, identifier,
            cancellationToken).ConfigureAwait(false);

        try
        {
            var stagingFolder = downloaded.Directory;
            if (stagingFolder is not null && stagingFolder.Exists)
                stagingFolder.Delete(recursive: true);
        }
        catch (Exception ex)
        {
            // 删不掉只是占点磁盘，不影响安装 —— 下次同一个文件入队时会覆盖同一路径。
            _logger.Warning(ex, "Could not clean the staging folder for {Key}", item.Key);
        }

        return archive;
    }

    // ─── 归档 → 安装向导 ───────────────────────────────────────

    /// <summary>
    /// 开安装向导。返回 <c>false</c> = 这个角色已经有一个安装在进行，这次没开（并已提示用户）。
    /// </summary>
    private async Task<bool> OpenInstallerAsync(ModArchiveHandle archive, ModDownloadRequest request)
    {
        var modList = ResolveModList(request);

        // 与拖放同一条规矩：同一个角色同时只能开一个安装向导（JASM 本身不支持给一个角色
        // 并行装两个 mod，向导也会互相打架）。
        if (windowManagerService.GetWindow(modList) is { } existing)
        {
            _notificationManager.ShowNotification("Mod 商店",
                $"『{modList.Character.DisplayName}』还有一个安装没完成，先把它结束掉。",
                TimeSpan.FromSeconds(8));

            App.MainWindow.DispatcherQueue.TryEnqueue(() => existing.Activate());
            return false;
        }

        var installerRoot = PrepareInstallerFolder(archive);

        _logger.Information("Deploying store mod {Key} into character {Character}",
            request.Key, modList.Character.InternalName);

        // 这个 mod 之前从商店装过吗（同一个角色下）？装过就让向导**就地更新**那份，
        // 而不是在同一个角色下再塞一份 —— 靠安装记录里的本地 mod id，与「模组更新」那条路
        // 给 InstallOptions.ExistingModIdToUpdate 的是同一个东西。
        //
        // 角色要对得上才算：用户完全可能故意把同一个 mod 放在两个角色下各一份，
        // 那时「已装」指的是另一份，不该把它顶掉。记录里的那份要是已经被用户删了，
        // 向导那边 GetModById 会返回 null 并自动退化成「新增」，不用我们操心。
        var modToUpdate = ResolveModToUpdate(request, modList);

        // 向导关掉之后要写安装记录，而它的关闭事件只带一个「成功」、不带「装了什么」。
        // 所以先把这一刻已有的 mod id 记下来：之后多出来的那个就是刚装进去的（见 RecordInstallAsync）。
        var modsBefore = modList.Mods.Select(entry => entry.Id).ToHashSet();

        var monitor = await modInstallerService.StartModInstallationAsync(installerRoot, modList,
            setup: options =>
            {
                // 与「从 GameBanana 页面安装」一致地把页面地址记进 mod 设置里：
                // JASM 的更新检查就是靠它把本地 mod 认回 GameBanana 条目的。
                options.ModUrl = request.ModPageUrl;
                options.ExistingModIdToUpdate = modToUpdate;
            }).ConfigureAwait(false);

        TrackOutcomeAsync(monitor, request, modList, modsBefore, modToUpdate);
        return true;
    }

    /// <summary>
    /// 该就地更新哪一份 mod；这次是新增（或者说不清）时返回 null。
    /// </summary>
    private Guid? ResolveModToUpdate(ModDownloadRequest request, ICharacterModList modList)
    {
        var record = installIndex.Find(request.Key.ModId);

        if (record is null)
            return null;

        if (!string.Equals(record.Character, modList.Character.InternalName, StringComparison.OrdinalIgnoreCase))
            return null;

        if (record.LocalModId is not { } localModId)
            return null;

        _logger.Information("Store mod {Key} is already installed as {LocalModId}, updating it in place",
            request.Key, localModId);

        return localModId;
    }

    /// <summary>
    /// 把归档解到临时目录，再把**解出来的那一层**改名搬进一个空目录交给向导。
    ///
    /// 为什么要这一趟（与 <c>ModPageVM.StartInstall</c> 完全一样，不是多余的搬运）：
    /// 向导把传进去的文件夹当成压缩包的根，里面的每一层都是「可选的 mod」；而归档在入库时
    /// 文件名被加了 <c>_!!_</c> 后缀（mod 名 / mod id / 文件 id / md5），直接把解出来的那层
    /// 交过去，用户在向导里看到的就是一串带哈希的名字。
    /// </summary>
    private DirectoryInfo PrepareInstallerFolder(ModArchiveHandle archive)
    {
        var extracted = archiveService.ExtractArchive(archive.FullName, App.GetUniqueTmpFolder().FullName);

        var sections = Path.GetFileName(extracted.Name).Split(ModArchiveRepository.Separator);
        if (sections.Length != 4)
            throw new InvalidArchiveNameFormatException();

        var modFolderName = sections[0];
        var modFolderExtension = Path.GetExtension(extracted.Name);

        var installerRoot = Directory.CreateDirectory(Path.Combine(extracted.Parent!.FullName, "ArchiveRoot"));
        extracted.MoveTo(Path.Combine(installerRoot.FullName, $"{modFolderName}{modFolderExtension}"));

        return installerRoot;
    }

    /// <summary>
    /// 决定这个 mod 装到哪个角色下面。
    ///
    /// 角色名来自 GameBanana 的分类（商店里那个「角色」标签）。对不上本地任何一个角色时
    /// 落 <c>Others</c>：这个 mod 已经下好了，让用户自己在「其他」里把它拖到正确的角色下
    /// 比直接失败好 —— 但也**不能猜**一个最像的角色，那会把 mod 悄悄塞进别人下面。
    /// </summary>
    private ICharacterModList ResolveModList(ModDownloadRequest request)
    {
        var characters = gameService.GetAllModdableObjectsAsCategory<ICharacter>(GetOnly.Both);

        var candidates = characters
            .Select(character => new ModStoreCharacterCandidate(character.InternalName, character.DisplayName,
                character.Keys.ToArray()))
            .ToArray();

        var index = ModStoreTargetCharacter.ResolveIndex(request.Character, candidates);

        if (index >= 0)
            return skinManagerService.GetCharacterModList(characters[index]);

        if (!string.IsNullOrWhiteSpace(request.Character))
            _logger.Information("No local character matches '{Character}', falling back to Others",
                request.Character);

        var others = gameService.GetModdableObjectByIdentifier(
            new InternalName(gameService.OtherCharacterInternalName), GetOnly.Both);

        if (others is null)
            throw new InvalidOperationException("找不到『Others』这个角色，游戏配置可能还没初始化完");

        return skinManagerService.GetCharacterModList(others);
    }

    // ─── 向导关闭之后 ──────────────────────────────────────────

    /// <summary>
    /// 等向导关闭，然后提示结果 + 写安装记录。**故意不 await**：调用方在队列的工作线程上，
    /// 在那里等用户点完向导会把后面的下载全堵死（见类注释）。
    ///
    /// 安装记录（PRD Phase 1 第 8 项）只能挂在这里 —— 只有到这一步才知道用户到底装成了没有：
    /// 向导关掉之前用户点取消 = 什么都没发生，那时写记录等于撒谎。
    /// </summary>
    private async void TrackOutcomeAsync(InstallMonitor monitor, ModDownloadRequest request,
        ICharacterModList modList, IReadOnlySet<Guid> modsBefore, Guid? modToUpdate)
    {
        var displayName = request.ModName ?? request.FileName;

        try
        {
            using (monitor)
            {
                var result = await monitor.WaitForCloseAsync().ConfigureAwait(false);

                switch (result.CloseReason)
                {
                    case CloseRequestedArgs.CloseReasons.Success:
                        _logger.Information("Store install finished for {Key}", request.Key);
                        _notificationManager.ShowNotification("Mod 商店",
                            $"『{displayName}』安装完成。", TimeSpan.FromSeconds(5));

                        await RecordInstallAsync(request, modList, modsBefore, modToUpdate).ConfigureAwait(false);
                        break;
                    case CloseRequestedArgs.CloseReasons.Error:
                        _logger.Error(result.Exception, "Store install failed for {Key}", request.Key);
                        _notificationManager.ShowNotification("Mod 商店",
                            $"安装『{displayName}』时出错，详情见日志。", TimeSpan.FromSeconds(8));
                        break;
                    default:
                        // 用户在向导里点了取消：什么都没发生，不用打扰他。
                        _logger.Information("Store install canceled by the user for {Key}", request.Key);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            // async void：漏出去就是进程级未处理异常。向导关掉之后的收尾失败不影响已装好的 mod。
            _logger.Error(ex, "Post-install handling failed for {Key}", request.Key);
        }
    }

    /// <summary>
    /// 写一条本地安装记录（PRD 第 8 项），让商店卡片能说「已安装 / 可更新」。
    ///
    /// 「装到哪了」靠**前后对比这个角色的 mod 列表**拿：向导的关闭事件只带一个 Success、
    /// 不带装了什么（向导不归商店改），而 mod 的 Guid 是现成的 —— 不用去读每个 mod 的设置
    /// （那是一条 mod 一次异步 I/O，而我们只要刚装进去的那一个）。
    ///
    /// **多出来恰好一个才是我们要的**：0 个 = 没装成（用户在向导里改了目标、或者这次是
    /// 就地更新 —— 更新不新增 mod），多个 = 这份归档里含多个 mod；后两种都记不了 ——
    /// 记成「一个 mod 的记录」会让第 8 项的更新判定对着一个错的 mod 报「可更新」。
    /// </summary>
    private async Task RecordInstallAsync(ModDownloadRequest request, ICharacterModList modList,
        IReadOnlySet<Guid> modsBefore, Guid? modToUpdate)
    {
        var added = modList.Mods
            .Where(entry => !modsBefore.Contains(entry.Id))
            .Select(entry => entry.Mod)
            .ToArray();

        ISkinMod? installed;
        switch (added.Length)
        {
            case 1:
                installed = added[0];
                break;

            case 0 when modToUpdate is { } updatedId:
                // 就地更新：没有新 mod，被顶掉的那个（id 没变）就是它 —— 路径也还是老路径。
                installed = modList.Mods.FirstOrDefault(entry => entry.Id == updatedId)?.Mod;

                if (installed is null)
                {
                    _logger.Warning("Store mod {Key} was updated in place but {LocalModId} is gone, " +
                                    "no install record written", request.Key, updatedId);
                    return;
                }

                break;

            default:
                _logger.Warning("Store mod {Key} reported a successful install but {Count} new mods appeared, " +
                                "no install record written", request.Key, added.Length);
                return;
        }

        _logger.Information("Recording store install of {Key} at {ModName}", request.Key, installed.Name);

        await installIndex.UpsertAsync(new ModStoreInstallRecord(
            request.Key.ModId,
            request.Key.ModFileId,
            request.ExpectedMd5,
            request.Version,
            modList.Character.InternalName,
            request.ModPageUrl?.ToString(),
            installed.FullPath,
            installed.Id,
            DateTimeOffset.Now)).ConfigureAwait(false);

        // 记录已经在索引里了才通知：订阅方（商店页）收到就去索引里查这次装了什么。
        InstallRecorded?.Invoke(this, EventArgs.Empty);
    }
}