using System.Collections.ObjectModel;
using Windows.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services;
using GIMI_ModManager.WinUI.Services.ModHandling;
using GIMI_ModManager.WinUI.Services.Overlay;
using GIMI_ModManager.WinUI.ViewModels.CharacterDetailsViewModels;
using GIMI_ModManager.WinUI.ViewModels.Messages;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>
/// 游戏内浮窗的 ViewModel：选角色 → 搜 Mod → 勾选（勾了即刷新）。
///
/// **只从 UI 线程调用**（浮窗的点击、以及 <c>OverlayWindowService</c> 的启动/关闭），
/// 所以内部刻意不写 <c>ConfigureAwait(false)</c>：属性直接绑到界面，续体留在 UI 线程最省事。
/// 唯一搬去后台的是动盘那一步（文件夹改名），它不影响这段约定。
/// </summary>
internal sealed partial class OverlayViewModel : ObservableRecipient, IRecipient<ModChangedMessage>,
    IRecipient<ModInstalledMessage>
{
    /// <summary>下拉右侧模式控件里的序号。取值与 <c>Segmented</c> 里两个 <c>SegmentedItem</c> 的先后一致。</summary>
    private const int SingleSelectModeIndex = 0;

    private const int MultiSelectModeIndex = 1;

    private readonly ISkinManagerService _skinManagerService;
    private readonly IGameService _gameService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ModDragAndDropService _dragAndDropService;
    private readonly ILogger _logger;

    /// <summary>当前选中角色的全部 Mod（未过滤）。搜一个词不该重新去读盘，所以过滤只在这个快照上做。</summary>
    private List<OverlayModItemViewModel> _allMods = new();

    /// <summary>浮窗自己的设置。窗口的位置也由这里统一落盘 —— 两个写入者各存一份会互相覆盖。</summary>
    public OverlaySettings Settings { get; private set; } = new();

    /// <summary>「勾选即刷新」的执行体。由构造它的那一层复用，浮窗与设置页看到的是同一次刷新的状态。</summary>
    public OverlayRefreshCoordinator RefreshCoordinator { get; }

    /// <summary>
    /// 可选的角色：**只列有 Mod 的**。浮窗是用来换皮肤的，没有 Mod 的角色在列表里纯属噪音。
    /// 装的是 <see cref="OverlayCharacterItem" /> 而不是角色本身 —— 下拉要在角色名左边显示头像，
    /// 而头像的兜底（<c>ImageUri</c> 可空）在包装里做了。
    /// </summary>
    public ObservableCollection<OverlayCharacterItem> Characters { get; } = new();

    /// <summary>当前显示（已过滤 + 已启用前移）的 Mod 行。</summary>
    public ObservableCollection<OverlayModItemViewModel> Mods { get; } = new();

    [ObservableProperty] private OverlayCharacterItem? _selectedCharacter;
    [ObservableProperty] private string _searchText = string.Empty;

    /// <summary>列表空掉时给一句话说明为什么空 —— 「没有 Mod」和「搜不到」是两件事，不能共用一句。</summary>
    [ObservableProperty] private string? _emptyMessage;

    /// <summary>
    /// 列表是不是空的。<see cref="EmptyMessage"/> 本身是个字符串、绑不了 <c>Visibility</c>，
    /// 所以再给界面一个布尔值去控制那段提示的显隐。
    /// </summary>
    [ObservableProperty] private bool _isListEmpty;

    /// <summary>勾选失败时的提示。刷新那边的成败由 <see cref="RefreshCoordinator"/> 负责，这里只管动盘失败。</summary>
    [ObservableProperty] private string? _errorMessage;

    /// <summary>
    /// 0 = 单选（默认），1 = 多选。界面直接绑 <c>Segmented.SelectedIndex</c>，所以这里用序号而不是布尔 ——
    /// 界面的"第几个"与语义的"哪种模式"只在这一个地方换算，别处一律看 <see cref="IsMultiSelectMode"/>。
    /// </summary>
    [ObservableProperty] private int _modeIndex = SingleSelectModeIndex;

    /// <summary>
    /// 多选模式下攒着还没刷的改动。刷新按钮旁边据此亮个点 —— 多选模式把"什么时候刷"交给用户，
    /// 就得有人提醒他还欠一次刷新。
    /// 单选模式下恒为 false：勾完就刷掉了。
    /// </summary>
    [ObservableProperty] private bool _hasPendingChanges;

    /// <summary>是不是多选模式。策略判断都用它；界面不绑它，所以不必替它发通知。</summary>
    public bool IsMultiSelectMode => ModeIndex == MultiSelectModeIndex;

    /// <summary>
    /// 键盘当前选中的那一行。
    ///
    /// **浮窗永远拿不到键盘焦点**（<c>WS_EX_NOACTIVATE</c> + 从不 <c>Activate()</c>），键盘操作因此只能走
    /// 全局热键，「选中」也就不可能交给列表控件 —— 它是 <c>SelectionMode="None"</c>，没有选中态可言。
    /// 列表为空 / 搜索没结果时是 <c>null</c>；过滤过之后由 <see cref="ApplyFilter"/> 重新定位。
    /// </summary>
    [ObservableProperty] private OverlayModItemViewModel? _selectedMod;

    /// <summary>
    /// 正在装一个拖进来的包（解压 → 落盘）。界面据此显示进度动画。
    ///
    /// 为什么要有个标志位：实测大包一次要十几秒（解压一半、复制落盘一半），而这段时间浮窗
    /// 什么都不显示 —— 用户只会以为拖进去没反应。动画本身也是给这段"什么都没发生"的时间兜底。
    /// </summary>
    [ObservableProperty] private bool _isInstalling;

    /// <summary>进度行那行字：「正在解压 X…」/「正在安装…」/「已安装：X」。</summary>
    [ObservableProperty] private string? _installStatusText;

    /// <summary>刚装好（绿勾）。与 <see cref="IsInstalling"/> 互斥：转圈变对勾就是这个信号。</summary>
    [ObservableProperty] private bool _showInstallSuccess;

    /// <summary>
    /// 整行进度区的显隐：转圈中、或刚装好（绿勾）时都在，其余时候整行塌掉不占地方。
    /// 单独给一个布尔是给界面用的 —— <c>x:Bind</c> 绑不了「或」，两个状态各自要不要显示
    /// 又已经分别绑在转圈和绿勾上了。
    /// </summary>
    public bool ShowInstallStatus => IsInstalling || ShowInstallSuccess;

    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(ShowInstallStatus));

    partial void OnShowInstallSuccessChanged(bool value) => OnPropertyChanged(nameof(ShowInstallStatus));

    /// <summary>
    /// 状态行的「代际」。装完那句话要停留几秒再消失，而期间用户可能又拖进来一个 ——
    /// 递增它，过期的那个延时清理就认得出自己已经不作数了（否则会把新装的进度行抹掉）。
    /// </summary>
    private int _installStatusEpoch;

    public OverlayViewModel(ISkinManagerService skinManagerService, IGameService gameService,
        OverlayRefreshCoordinator refreshCoordinator, ILocalSettingsService localSettingsService,
        ModDragAndDropService dragAndDropService, ILogger logger)
    {
        _skinManagerService = skinManagerService;
        _gameService = gameService;
        RefreshCoordinator = refreshCoordinator;
        _localSettingsService = localSettingsService;
        _dragAndDropService = dragAndDropService;
        _logger = logger.ForContext<OverlayViewModel>();
    }

    /// <summary>
    /// 把拖进浮窗的包装到**当前选中的角色**上，与主窗口卡片那条拖拽路走同一个服务。
    ///
    /// 复用而不是另写一套：整包装、向导、失败通知、以及装完的热更新都在那条路上，
    /// 浮窗这边唯一多知道的一件事是「装给谁」—— 它本来就只对着一个角色。
    /// </summary>
    public async Task DropModPackageAsync(IReadOnlyList<IStorageItem> storageItems)
    {
        if (storageItems.Count == 0)
            return;

        // 一次只跑一批：两批会抢同一个临时目录，而第二批拖进来的时候用户多半只是急了 ——
        // 与其并行装出两个半成品，不如让这一批跑完（进度行一直在那儿，他知道还在装）。
        if (IsInstalling)
            return;

        // 这一批只接**文件**（自解压 exe / zip 那些包）。文件夹没法静默判断「哪一层才是 Mod」，
        // 别猜：混着拖进来就整单不收 —— 装一半再报错比一开始就说清楚更糟。
        var files = new List<StorageFile>();
        var folderCount = 0;
        foreach (var item in storageItems)
        {
            if (item is StorageFile file)
                files.Add(file);
            else
                folderCount++;
        }

        if (files.Count == 0 || folderCount > 0)
        {
            ErrorMessage = "浮窗只接包（自解压 exe / zip）。文件夹请拖到主窗口的角色卡片上。";
            return;
        }

        // 兜底角色 = 浮窗当前选中的那个。**可以没有**：角色列表只列「已经有 Mod 的角色」，
        // 一个 Mod 都没有时列表是空的 —— 而那正是最需要靠拖拽装进第一个 Mod 的场景。
        var fallbackModList = SelectedCharacter is { } selected ? ResolveModList(selected.Character) : null;

        var epoch = ++_installStatusEpoch;
        ShowInstallSuccess = false;
        IsInstalling = true;
        InstallStatusText = files.Count > 1 ? $"准备安装 {files.Count} 个包…" : "准备中…";

        var failed = 0;

        // 逐个装、**串行**：解压临时目录、安装通知、列表热更新这几处都假定「同一时刻只有一单在跑」。
        // 一个包失败不影响后面的 —— 批量拖十几个的时候，最糟的结果是有一个坏包把整批带停。
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                var counter = files.Count > 1 ? $"（{i + 1}/{files.Count}）" : string.Empty;

                // 进度回调走 Progress<T>：它会把回调派回**构造它的那个线程**（UI），
                // 于是这里可以直接写绑定到界面的属性，不必自己 Enqueue。
                // 拖拽那条路把失败吞在自己的通知里、只通过 stageProgress 报一声 Failed —— 在这里记账。
                var stage = new Progress<ModInstallStage>(value =>
                {
                    if (value == ModInstallStage.Failed)
                    {
                        failed++;
                        InstallStatusText = $"「{file.Name}」失败，继续下一个…";
                        return;
                    }

                    InstallStatusText = value == ModInstallStage.Extracting
                        ? $"正在解压 {file.Name}{counter}…"
                        : $"正在安装{counter}…";
                });

                try
                {
                    await _dragAndDropService.AddDroppedPackageAsync(file,
                        (fileName, scanResult) => ResolveTargetModListAsync(fileName, scanResult),
                        installSilently: true,
                        stageProgress: stage,
                        // 解压摊到 Mod 根目录所在那块盘上 —— 装的时候才是「改名」而不是「再复制一遍」
                        targetFolderHint: fallbackModList?.AbsModsFolderPath ?? _skinManagerService.ActiveModsFolderPath);
                }
                catch (Exception e)
                {
                    // 同一单里更早的失败（认角色、解压）会从这里冒出来；通知那条路由拖拽服务负责
                    failed++;
                    _logger.Warning(e, "[浮窗] 批量安装中这一单失败: {File}", file.Name);
                }
            }
        }
        finally
        {
            IsInstalling = false;

            // 成功的收尾语由「刚装好一个 Mod」那条消息去写（见 Receive）。这里只在**批量**时装完给一句汇总：
            // 单个包的成败主窗口的通知已经说得很清楚了，批量才需要「一共几个、成了几个」。
            if (files.Count > 1)
            {
                var ok = files.Count - failed;
                InstallStatusText = failed == 0
                    ? $"已安装 {ok} 个"
                    : $"装好 {ok} 个，{failed} 个失败（详见主窗口通知）";

                if (failed == 0)
                    ShowInstallSuccess = true;

                ErrorMessage = failed > 0 ? "有包没装上，详情见主窗口的通知。" : null;
            }
            else if (!ShowInstallSuccess)
            {
                InstallStatusText = null;
            }

            _ = ClearInstallStatusLaterAsync(epoch);
        }
    }

    /// <summary>
    /// 过几秒把「已安装」那句收掉。带的 <paramref name="epoch"/> 用来判断这次延时还作不作数：
    /// 期间又拖进来一个（或浮窗重建了）就别去动新那一条。
    /// </summary>
    private async Task ClearInstallStatusLaterAsync(int epoch)
    {
        try
        {
            // 不用 ConfigureAwait(false)：续体要回 UI 线程才能动绑定到界面的属性
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            return;
        }

        if (epoch != _installStatusEpoch)
            return;

        ShowInstallSuccess = false;
        InstallStatusText = null;
    }

    /// <summary>
    /// 装给谁：先**认包**（与主窗口那条拖拽路同一套判据）；**认不出来就装到「其它角色」分类**。
    ///
    /// 认不出来时不再退回「浮窗当前选中的角色」—— 那条判据只有「用户正看着谁」这一条依据，
    /// 实测把「女主-油亮黑丝」「女漂-小恶魔」「心-暗夜雌狐」这些认不出的包塞进了爱弥斯、弗洛洛
    /// 之类毫不相干的角色目录里。认不出时的正确去向是「其它角色」：用户在那儿一眼找得到，
    /// 也不会污染别的角色的 Mod 列表。
    /// </summary>
    /// <remarks>
    /// 认角色这一步必须在**解压之后**做，所以它发生在拖拽落下之后的回调里，而不是 DragOver 里 ——
    /// 也就意味着拖拽经过时不能拿「有没有选中角色」去拒绝（见 <c>OverlayWindow.RootGrid_OnDragOver</c>）。
    /// </remarks>
    private async Task<ICharacterModList?> ResolveTargetModListAsync(string fileName,
        DragAndDropScanResult scanResult)
    {
        if (ResolveDroppedCharacter(fileName, scanResult) is { } detected)
            return detected;

        if (await ResolveOthersModListAsync().ConfigureAwait(false) is { } others)
        {
            _logger.Information("[浮窗] 拖入的包 '{FileName}' 认不出角色，装到「其它角色」分类", fileName);
            return others;
        }

        // 连「其它角色」都拿不到（理论上不会）—— 只留一句话，别把包悄悄丢了
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            ErrorMessage = "认不出这是哪个角色的 Mod，也没法落到「其它角色」分类。请把包拖到主窗口的角色卡片上。");

        return null;
    }

    /// <summary>
    /// 「其它角色」分类的 Mod 列表；**这个分类还没有列表就现建一个**。
    ///
    /// 浮窗的角色列表只列「已经有 Mod 的分类」，而认不出的包常常正是第一个落到「其它角色」里的东西 ——
    /// 不现建的话这条路第一次就断在这儿。
    /// </summary>
    private async Task<ICharacterModList?> ResolveOthersModListAsync()
    {
        var internalName = _gameService.OtherCharacterInternalName;

        if (_skinManagerService.GetCharacterModListOrDefault(internalName) is { } existing)
            return existing;

        var others = _gameService.GetAllModdableObjectsAsCategory<ICharacter>()
            .FirstOrDefault(character => character.InternalNameEquals(internalName));

        if (others is null)
            return null;

        await _skinManagerService.EnableModListAsync(others).ConfigureAwait(false);

        return _skinManagerService.GetCharacterModListOrDefault(internalName);
    }

    /// <summary>
    /// 从「被拖入的文件名 + 解压结构」认角色；**认不准就返回 <c>null</c>**。
    ///
    /// 判据与主窗口那条拖拽路完全同一套（同 <see cref="CharacterNameMatcher"/>、同样的线索与伪角色排除），
    /// 唯一不同的是**认得不准时不弹选择框**：浮窗上没有问「是哪个角色」的地方，它的定位就是不打岔。
    /// 认不出来时调用方退回「当前选中的角色」—— 你正看着谁就装给谁，总比弹个框强。
    /// </summary>
    private ICharacterModList? ResolveDroppedCharacter(string fileName, DragAndDropScanResult scanResult)
    {
        var ranked = CharacterNameMatcher.Rank(
            _gameService.GetAllModdableObjectsAsCategory<ICharacter>(),
            CluesForCharacterMatch(fileName, scanResult),
            [
                _gameService.OtherCharacterInternalName,
                _gameService.GlidersCharacterInternalName,
                _gameService.WeaponsCharacterInternalName
            ]);

        if (!CharacterNameMatcher.IsConfident(ranked))
        {
            _logger.Information("[浮窗] 拖入的包 '{FileName}' 认不出角色（{Count} 个候选），改装给当前选中的角色",
                fileName, ranked.Count);
            return null;
        }

        _logger.Information("[浮窗] 拖入的包 '{FileName}' 认出角色 {Character}", fileName, ranked[0].InternalName);

        return _skinManagerService.CharacterModLists
            .FirstOrDefault(list => list.Character.InternalNameEquals(ranked[0].Character));
    }

    /// <summary>
    /// 认角色用的线索：原文件名 + 包内顶层目录名。
    /// <b>与 <c>CharactersViewModel.CluesForCharacterMatch</c> 是同一套判据</b> ——
    /// 那边还连着「认不出就弹选择框」的流程，暂时没合并；判据要改请两处一起改。
    /// </summary>
    private static IEnumerable<string> CluesForCharacterMatch(string fileName, DragAndDropScanResult scanResult)
    {
        yield return fileName;

        foreach (var directory in Directory.EnumerateDirectories(scanResult.ExtractedFolder.FullPath))
            yield return Path.GetFileName(directory);
    }

    /// <summary>
    /// 读设置、列出角色、把上次选中的角色选回来。浮窗显示之前调一次。
    /// </summary>
    public async Task InitializeAsync()
    {
        Settings = await _localSettingsService
            .ReadOrCreateSettingAsync<OverlaySettings>(OverlaySettings.Key, SettingScope.App);

        // 把存下来的模式读进界面。页面级绑定读的是 ModeIndex 的当前值，
        // 而本方法一定在窗口显示之前跑完，所以在这一步赋值是够的。
        ModeIndex = Settings.MultiSelectMode ? MultiSelectModeIndex : SingleSelectModeIndex;

        RefreshCharacters();
    }

    /// <summary>
    /// 按**当前**的 Mod 列表重建角色列表（顺带修好空态与选中）。浮窗**每次被唤出**时都会调一次
    /// （见 <c>OverlayWindowService.ShowOverlay</c>）。
    ///
    /// 为什么不能只在启动时建一次：这里建的是「那一刻的快照」，而 mod 扫描
    /// （<c>SkinManagerService.ScanForModsAsync</c>）是启动流程里另一条分支。快照若早于它完成，
    /// 列表会**永久**空着 —— 空态那条分支连订阅都不建、热键唤出以前也不重建，用户只能重启 JASM
    /// 才能恢复。浮窗本来就是随时按热键唤出的，每次唤出重新对一遍才是对的。
    /// </summary>
    public void RefreshCharacters(string? preferCharacterInternalName = null)
    {
        // 订阅主窗口发出的变化（画廊、预设、随机化、以及装完 Mod 的通知）—— 两个界面不该各显示一套状态。
        // 放在**空态判断之前**：列表空着的时候更要收得到「刚装了 Mod」这条消息，否则空态就是个死局。
        IsActive = true;

        // 重建会 Clear 集合，ComboBox 跟着把 SelectedCharacter 置回 null —— 先把「原来选的是谁」记下来。
        // 装了新 Mod 的那种刷新，优先选**刚装的那个角色**（见 Receive 的说明）。
        var previous = preferCharacterInternalName
                       ?? SelectedCharacter?.Character.InternalName
                       ?? Settings.LastSelectedCharacter;

        BuildCharacterList();

        if (Characters.Count == 0)
        {
            EmptyMessage = "没有找到任何 Mod。先在 JASM 主窗口里导入 Mod 吧。";
            IsListEmpty = true;
            return;
        }

        // 之前可能是空态，这次拿到数据了 —— 空态标记要一起清掉，否则那段提示会一直盖在上面
        EmptyMessage = null;
        IsListEmpty = false;

        // 上次那个角色可能已经被删掉/改名了（换了游戏、清了 Mod 文件夹），退化成第一个而不是报错
        SelectedCharacter =
            Characters.FirstOrDefault(c => c.Character.InternalNameEquals(previous))
            ?? Characters[0];

        // 但上面这句会被**下一轮**的消息盖掉：角色下拉的 SelectedItem 是 TwoWay，集合被 Clear 之后
        // ComboBox 会把 null 写回 SelectedCharacter，而那个回写发生在本方法返回之后。
        // 实机症状：浮窗里明明列着角色，拖拽时却判「没选中角色」→ 光标显示禁用、拖不进去。
        // 所以下一轮再确认一次：那时 ComboBox 已经处理完集合变化。
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (SelectedCharacter is null && Characters.Count > 0)
                SelectedCharacter = Characters[0];
        });
    }

    /// <summary>把浮窗被拖到的位置记下来。窗口在拖动结束与关闭时各调一次。</summary>
    public async Task RememberWindowPositionAsync(int x, int y)
    {
        Settings.XPosition = x;
        Settings.YPosition = y;
        await SaveSettingsAsync();
    }

    private async Task SaveSettingsAsync()
    {
        try
        {
            await _localSettingsService.SaveSettingAsync(OverlaySettings.Key, Settings, SettingScope.App);
        }
        catch (Exception e)
        {
            // 存不下位置只是下次开在别处，不值得打断用户；但日志要留，否则"位置老是记不住"查起来毫无线索
            _logger.Warning(e, "[浮窗] 保存浮窗设置失败");
        }
    }

    private void BuildCharacterList()
    {
        Characters.Clear();

        // 只取有 Mod 的：关掉某个角色（DisableModListAsync）会把它从 CharacterModLists 里摘掉，这里也就跟着消失
        var characters = _skinManagerService.CharacterModLists
            .Where(list => list.Mods.Count > 0)
            .Select(list => list.Character)
            .OrderBy(character => character.DisplayName, StringComparer.CurrentCultureIgnoreCase);

        foreach (var character in characters)
            Characters.Add(new OverlayCharacterItem(character));
    }

    partial void OnSelectedCharacterChanged(OverlayCharacterItem? value)
    {
        LoadMods(value?.Character);

        if (value is null) return;

        Settings.LastSelectedCharacter = value.Character.InternalName;

        // 不 await：这里只该更新一下内存里的字段，落盘慢一点无所谓，不能卡住切换角色
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        SaveSettingsAsync();
#pragma warning restore CS4014
    }

    partial void OnModeIndexChanged(int value)
    {
        Settings.MultiSelectMode = value == MultiSelectModeIndex;

        // 刻意**不**在这里做两件事：
        //   1. 不把当前角色已勾的 Mod 收成一件 —— 独占只在"勾选"这个动作上发生，
        //      否则用户拨一下模式开关，磁盘上就有一堆文件夹被改名；
        //   2. 不清 HasPendingChanges —— 攒下的改动是真的没刷过，拨开关不会让它们生效。
        // 于是从多选切回单选时，那个待刷新提示会继续亮着，直到用户点一次「刷新」。

        // 不 await：这里只该把模式记下来，落盘慢一点无所谓，不能卡住拨开关
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        SaveSettingsAsync();
#pragma warning restore CS4014
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>
    /// 选中行一换就把高亮标记同步过去。
    ///
    /// 放在这里而不是"赋值处顺手写一行"：这样任何一条改 <see cref="SelectedMod"/> 的路径
    /// （热键、过滤后重定位、将来别的入口）都不可能留下两个高亮或零个高亮。
    /// </summary>
    partial void OnSelectedModChanged(OverlayModItemViewModel? oldValue, OverlayModItemViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;

        if (newValue is not null)
            newValue.IsSelected = true;
    }

    private void LoadMods(IModdableObject? character)
    {
        _allMods = character is null
            ? new List<OverlayModItemViewModel>()
            : ResolveModList(character)?.Mods
                .Select(entry => OverlayModItemViewModel.FromEntry(entry).WithToggleHandler(ToggleModAsync))
                .ToList()
            ?? new List<OverlayModItemViewModel>();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var filtered = OverlayModFilter.Apply(_allMods, SearchText);

        Mods.Clear();
        foreach (var mod in filtered)
            Mods.Add(mod);

        IsListEmpty = Mods.Count == 0;

        // 过滤会整体重建 Mods（行对象是复用的，但成员与顺序都变了），选中项必须重新定位：
        // 还在列表里就不动它，不在就退到第一行 —— 否则键盘「回车切换」会作用到一行已经
        // 从界面上消失的 Mod 上，用户看到的是「按了回车，不知改了哪一件」。
        if (SelectedMod is null || !Mods.Contains(SelectedMod))
            SelectedMod = Mods.Count > 0 ? Mods[0] : null;

        // 先判搜索："搜不到" 和 "这个角色没有 Mod" 是两件事，用户要据此决定是改搜索词还是去装 Mod
        EmptyMessage = Mods.Count > 0
            ? null
            : !string.IsNullOrWhiteSpace(SearchText)
                ? $"没有匹配「{SearchText}」的 Mod。"
                : SelectedCharacter is null
                    ? null
                    : "这个角色还没有 Mod。";
    }

    /// <summary>
    /// 勾选 / 取消勾选一行：动盘改名，通知主窗口，再让游戏里刷新。
    ///
    /// 行 VM 那边每个行自己的命令带 "运行中不重入" 的语义，所以这里不必再防同一个 Mod 连点；
    /// 不同行之间是各自独立的命令实例，互不阻塞（用户连勾几个 Mod 不该被排队）。
    /// </summary>
    private async Task ToggleModAsync(OverlayModItemViewModel item)
    {
        ErrorMessage = null;

        var modList = ResolveModList(SelectedCharacter?.Character);
        var entry = modList?.Mods.FirstOrDefault(m => m.Id == item.Id);

        if (modList is null || entry is null)
        {
            _logger.Warning("[浮窗] 勾选失败：找不到 {Mod} 所属的 Mod 列表", item.Name);
            ErrorMessage = $"「{item.Name}」已经不在这个角色的 Mod 列表里了，重新打开浮窗试试。";
            item.RefreshEnabledState();
            return;
        }

        try
        {
            // 改文件夹名（加/去 DISABLED_ 前缀）是磁盘操作，别放 UI 线程上
            item.IsEnabled = await Task.Run(() => modList.ToggleMod(item.Id));

            // 单选模式的独占：勾上新的，就把同角色其它还开着的收起来。
            // 取消勾选不动别人 —— 用户取消时想要的是"这件不要了"，不是"随便给我换一件"。
            if (item.IsEnabled && !IsMultiSelectMode)
                await DisableOtherModsAsync(modList, item);
        }
        catch (Exception e)
        {
            _logger.Error(e, "[浮窗] 勾选 Mod 失败: {Mod}", item.Name);
            ErrorMessage = $"「{item.Name}」没能切换成功，详情见日志。";
            item.RefreshEnabledState();
            return;
        }

        // 让主窗口把它那边的同一个 Mod 也更新掉（画廊 / 详情页 / 预设面板都在收这条消息）
        Messenger.Send(new ModChangedMessage(this, entry, null));

        if (IsMultiSelectMode)
        {
            // 多选：攒着，等用户点「刷新」。这里不刷是有意的 —— 他多半还要再勾几个，
            // 而每刷一次都要让游戏重载一遍 Mod 池，代价不小。
            HasPendingChanges = true;
            return;
        }

        // 单选：勾了就让游戏里立刻生效。合并与失败文案都交给协调器，这里不重复触发。
        // await 的是整段刷新（含送键闸门那段等待与补发），所以**这一行**的命令在刷新跑完前不重入
        // （行 VM 那边的命令语义），点它没反应；别的行是各自独立的命令实例，不受影响 ——
        // 连点同一个角色的不同 Mod 正是靠这一点才点得动。
        await RefreshCoordinator.RequestRefreshAsync();
        HasPendingChanges = false;
    }

    /// <summary>
    /// 单选模式的独占收尾：把同角色其它还开着的 Mod 关掉，只留刚勾上的那件。
    ///
    /// 一次勾选因此可能连带改掉 N 个文件夹名 —— 这正是「一次只开一件皮肤」的含义，不是漏了副作用。
    /// 用 <c>DisableMod</c> 的显式接口而不是再调一次 <c>ToggleMod</c>：不靠"我记的状态"去猜该翻成哪边。
    /// 关不掉的继续处理下一个，并把是哪件没关掉写进状态行 —— 静默的半成功比整体失败更难查。
    /// </summary>
    private async Task DisableOtherModsAsync(ICharacterModList modList, OverlayModItemViewModel justEnabled)
    {
        var others = _allMods
            .Where(row => row.Id != justEnabled.Id && row.IsEnabled)
            .ToList();

        if (others.Count == 0)
            return;

        var failed = new List<string>();

        foreach (var other in others)
        {
            // 行上的对象与 Mod 列表里的条目是两份：改盘要条目，发变更消息也要条目
            var entry = modList.Mods.FirstOrDefault(m => m.Id == other.Id);
            if (entry is null)
                continue;

            try
            {
                await Task.Run(() => modList.DisableMod(other.Id));
                other.IsEnabled = false;
                Messenger.Send(new ModChangedMessage(this, entry, null));
            }
            catch (Exception e)
            {
                // 失败的行保持原样（勾还在）：界面上显示的就是磁盘上的真实状态
                _logger.Error(e, "[浮窗] 单选模式：关掉 {Mod} 失败", other.Name);
                failed.Add(other.Name);
            }
        }

        if (failed.Count > 0)
            ErrorMessage = $"单选模式一次只留一件，但「{string.Join("」「", failed)}」没能关掉，详情见日志。";
    }

    /// <summary>
    /// 手动刷新。多选模式下这是必经的一步；单选模式下也留着 —— 刷新失败之后再点一次就是重试，
    /// 免得用户为了重试还得去动某个 Mod 的勾。
    ///
    /// 刷新期间按钮会被 <see cref="OverlayRefreshCoordinator.CanRequestRefresh"/> 灰掉；即便还是被点进来，
    /// 协调器也只会把它合并进正在跑的那一次（不会并发），所以这里不需要自己防抖。
    /// </summary>
    [RelayCommand]
    private async Task RefreshNowAsync()
    {
        await RefreshCoordinator.RequestRefreshAsync();
        HasPendingChanges = false;
    }

    /// <summary>
    /// 键盘热键：把选中行上移 / 下移 <paramref name="delta"/> 行（<c>-1</c> / <c>+1</c>）。
    ///
    /// 到边界**停住不回绕**：回绕会让"按住上键"变成"跳到列表最后一行"，而游戏里用户看不到指针，
    /// 选中的是哪一行全靠那一条高亮 —— 位置突然跳到另一端会让人以为自己按错了。
    /// 还没有选中时（列表刚被过滤过）往下落在第一行、往上落在最后一行：
    /// 用户的意图是"开始选"，不是"什么都没发生"。
    /// </summary>
    internal void MoveSelection(int delta)
    {
        if (Mods.Count == 0)
            return;

        // 选中项不在当前列表里时 IndexOf 会给 -1，与"还没选中"同样处理
        var current = SelectedMod is null ? -1 : Mods.IndexOf(SelectedMod);

        SelectedMod = current < 0
            ? Mods[delta >= 0 ? 0 : Mods.Count - 1]
            : Mods[Math.Clamp(current + delta, 0, Mods.Count - 1)];
    }

    /// <summary>
    /// 键盘热键「切换选中行」：与鼠标点勾选框**走同一条路**（<see cref="ToggleModAsync"/> ——
    /// 动盘改名、通知主窗口、单选模式下顺带刷新游戏）。
    ///
    /// 做成命令而不是普通方法，图的是生成器给的那份"运行中不重入"：连按回车不该让同一件 Mod
    /// 的文件夹名被改两次（点勾选框那条路本来就是靠命令的这一性质，见行 VM 的说明）。
    /// 选中的行已经不在列表里（刚被搜索词过滤掉）时什么都不做。
    /// </summary>
    [RelayCommand]
    private async Task ToggleSelectedAsync()
    {
        if (SelectedMod is { } item && Mods.Contains(item))
            await ToggleModAsync(item);
    }

    /// <summary>
    /// 主窗口改了某个 Mod 的状态（勾选、应用预设、随机化），浮窗跟着变 ——
    /// 否则同一个 Mod 在两个界面上会显示成两个状态。
    /// </summary>
    public void Receive(ModChangedMessage message)
    {
        if (ReferenceEquals(message.sender, this))
            return;

        var item = _allMods.FirstOrDefault(m => m.Id == message.SkinEntry.Id);
        if (item is null)
            return;

        item.IsEnabled = message.SkinEntry.IsEnabled;
    }

    /// <summary>
    /// 刚装好一个 Mod：立刻把列表对一遍，并**切到刚装的那个角色**。
    ///
    /// 只重建不切换的话，用户拖给 A 角色的 Mod 装完，浮窗还停在 B 角色上 —— 看上去像「没装上」
    /// （实机反馈：「应该跳转到对应角色下」）。
    ///
    /// 消息可能来自任何线程（安装收尾那几个 Task.Run 的续体），而本类的集合直接绑在界面上 ——
    /// 必须回 UI 线程再动。
    /// </summary>
    public void Receive(ModInstalledMessage message) =>
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            // 拖拽安装收尾的那一下：进度行从转圈换成绿勾。
            // 只认拖拽服务发来的 —— 安装向导装完也会发这条消息（浮窗的列表照样要跟着更新），
            // 但那次用户没在浮窗上拖过东西，突然冒一句「已安装」反而莫名其妙。
            if (ReferenceEquals(message.Sender, _dragAndDropService))
            {
                ShowInstallSuccess = true;
                InstallStatusText = "已安装";
            }

            RefreshCharacters(message.CharacterInternalName);
        });

    /// <summary>
    /// 拿当前的角色 Mod 列表。每次现取而不是把 <c>ICharacterModList</c> 缓存在字段里：
    /// 关掉一个角色会让它的列表被 Dispose 并摘出集合，缓存下来的引用就成了一把悬空的枪。
    /// </summary>
    private ICharacterModList? ResolveModList(IModdableObject? character) =>
        character is null ? null : _skinManagerService.GetCharacterModListOrDefault(character.InternalName);
}