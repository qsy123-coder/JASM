using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services;
using GIMI_ModManager.WinUI.Services.Overlay;
using GIMI_ModManager.WinUI.ViewModels.CharacterDetailsViewModels;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>
/// 游戏内浮窗的 ViewModel：选角色 → 搜 Mod → 勾选（勾了即刷新）。
///
/// **只从 UI 线程调用**（浮窗的点击、以及 <c>OverlayWindowService</c> 的启动/关闭），
/// 所以内部刻意不写 <c>ConfigureAwait(false)</c>：属性直接绑到界面，续体留在 UI 线程最省事。
/// 唯一搬去后台的是动盘那一步（文件夹改名），它不影响这段约定。
/// </summary>
internal sealed partial class OverlayViewModel : ObservableRecipient, IRecipient<ModChangedMessage>
{
    /// <summary>下拉右侧模式控件里的序号。取值与 <c>Segmented</c> 里两个 <c>SegmentedItem</c> 的先后一致。</summary>
    private const int SingleSelectModeIndex = 0;

    private const int MultiSelectModeIndex = 1;

    private readonly ISkinManagerService _skinManagerService;
    private readonly ILocalSettingsService _localSettingsService;
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

    public OverlayViewModel(ISkinManagerService skinManagerService, OverlayRefreshCoordinator refreshCoordinator,
        ILocalSettingsService localSettingsService, ILogger logger)
    {
        _skinManagerService = skinManagerService;
        RefreshCoordinator = refreshCoordinator;
        _localSettingsService = localSettingsService;
        _logger = logger.ForContext<OverlayViewModel>();
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

        BuildCharacterList();

        if (Characters.Count == 0)
        {
            EmptyMessage = "没有找到任何 Mod。先在 JASM 主窗口里导入 Mod 吧。";
            IsListEmpty = true;
            return;
        }

        // 上次那个角色可能已经被删掉/改名了（换了游戏、清了 Mod 文件夹），退化成第一个而不是报错
        SelectedCharacter =
            Characters.FirstOrDefault(c => c.Character.InternalNameEquals(Settings.LastSelectedCharacter))
            ?? Characters[0];

        // 订阅主窗口发出的变化（画廊、预设、随机化都会发），这样两个界面不会各显示一套状态
        IsActive = true;
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

        // 单选：勾了就让游戏里立刻生效。合并与失败文案都交给协调器，这里不等它跑完也不重复触发
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
    /// 拿当前的角色 Mod 列表。每次现取而不是把 <c>ICharacterModList</c> 缓存在字段里：
    /// 关掉一个角色会让它的列表被 Dispose 并摘出集合，缓存下来的引用就成了一把悬空的枪。
    /// </summary>
    private ICharacterModList? ResolveModList(IModdableObject? character) =>
        character is null ? null : _skinManagerService.GetCharacterModListOrDefault(character.InternalName);
}