using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.Core.Entities;
using GIMI_ModManager.Core.Entities.Mods.Helpers;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Models;

namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>
/// 浮窗列表里的一行。
///
/// 字段映射**逐字对齐主窗口画廊**（<c>ModModel.FromMod</c> + <c>WithModSettings</c>）：
/// 同一个 Mod 在两个界面上必须显示成同一个名字、同一个作者、同一张图，否则用户会以为自己看错了。
/// </summary>
internal sealed partial class OverlayModItemViewModel : ObservableObject, IOverlayModEntry
{
    /// <summary>Mod 的稳定标识。勾选时拿它去 <c>ICharacterModList.ToggleMod</c>。</summary>
    public Guid Id { get; }

    /// <summary>磁盘上的文件夹名（**带 <c>DISABLED_</c> 前缀**）。只给搜索用，不显示。</summary>
    public string FolderName { get; }

    /// <summary>显示名：自定义名优先，否则是去掉 <c>DISABLED_</c> 前缀的文件夹名。</summary>
    public string Name { get; }

    public string Author { get; }

    /// <summary>缩略图。没设过图时是占位图（与画廊同一张）。</summary>
    public Uri ImagePath { get; }

    /// <summary>
    /// 勾选状态。**可变**（勾一下要立刻反映在界面上），所以是 ObservableProperty 而不是只读属性。
    /// </summary>
    [ObservableProperty] private bool _isEnabled;

    /// <summary>
    /// 这一行是不是**键盘**选中的那一行，界面据此画高亮。
    ///
    /// 浮窗永不被激活（<c>WS_EX_NOACTIVATE</c>），拿不到键盘焦点，所以键盘操作只能走全局热键
    /// （见 <c>OverlayHotkeyRegistrar</c>）—— 「选中」这件事因此只能由 ViewModel 记：
    /// 列表那边是 <c>SelectionMode="None"</c>，控件自己的选中态压根不存在。
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// 这一行是不是被**右键标成了 NSFW**，界面据此画那个红标。
    ///
    /// 它只是浮窗自己那份名单上的一个记号（<c>OverlayNsfwMarks</c>）：不改文件夹名、
    /// 也不碰 Mod 的设置文件，所以标上 / 取消都不会影响游戏里的表现。
    /// </summary>
    [ObservableProperty] private bool _isMarked;

    /// <summary>
    /// 这一行是不是被**鼠标中键**标成了「已看过」。
    ///
    /// 与 <see cref="IsMarked"/>（右键的 NSFW 标记）互相独立：两件事可以同时成立。
    /// </summary>
    [ObservableProperty] private bool _isSeen;

    /// <summary>
    /// 单选模式下、这一行是当前勾选且后面还有一行时，行尾显示「下一个」按钮。
    /// 由浮窗的 ViewModel 统一刷新（它才知道列表顺序与当前模式）。
    /// </summary>
    [ObservableProperty] private bool _showNextButton;

    /// <summary>
    /// 这一行下方是否显示按键映射面板。**只有当前唯一勾选的那一行**为 true ——
    /// 面板跟着行走，不是浮窗底部那块全局面板。
    /// </summary>
    [ObservableProperty] private bool _isKeyPanelVisible;

    /// <summary>这一行下方的按键面板是否展开。用户点标题行折叠 / 展开。</summary>
    [ObservableProperty] private bool _isKeyPanelExpanded;

    [ObservableProperty] private bool _isKeyBindingLoading;

    /// <summary>面板里的状态说明（无绑定 / 读取失败）。<c>null</c> = 不显示。</summary>
    [ObservableProperty] private string? _keyPanelEmptyMessage;

    /// <summary>这一行 Mod 的 ini 按键绑定（按文件分组）。</summary>
    public ObservableCollection<ModIniKeyBindingGroup> KeyBindingGroups { get; } = new();

    /// <summary>
    /// 是否已经为这一行解析过按键绑定。**空结果也算解析过** ——
    /// 否则每次刷新都会为一个"确实没有按键"的 Mod 重新扫一遍盘。
    /// </summary>
    public bool KeyBindingsLoaded { get; set; }

    /// <summary>折叠箭头：展开时朝上、收起时朝下。</summary>
    public string KeyPanelChevronGlyph => IsKeyPanelExpanded ? "" : "";

    partial void OnIsKeyPanelExpandedChanged(bool value)
        => OnPropertyChanged(nameof(KeyPanelChevronGlyph));

    /// <summary>
    /// 设为「当前唯一勾选行」或取消。**每次从隐藏变为显示都回到折叠态**：
    /// 面板是辅助信息，不能自己展开把列表顶走；展开只能由用户点标题行触发
    /// （安装完成重建列表时新 Mod 会成为唯一勾选行，同样不该自动弹开）。
    /// </summary>
    public void SetKeyPanelVisible(bool visible)
    {
        if (IsKeyPanelVisible == visible)
            return;

        IsKeyPanelVisible = visible;

        if (visible)
            IsKeyPanelExpanded = false;
    }

    /// <summary>点面板标题行：折叠 / 展开。</summary>
    public void ToggleKeyPanel() => IsKeyPanelExpanded = !IsKeyPanelExpanded;

    /// <summary>清空已解析的绑定（重新解析前用）。</summary>
    public void ClearKeyBindings()
    {
        KeyBindingGroups.Clear();
        KeyBindingsLoaded = false;
        OnPropertyChanged(nameof(HasKeyBindings));
    }

    public bool HasKeyBindings => KeyBindingGroups.Count > 0;

    /// <summary>绑定集合整体换过之后通知界面（集合本身不负责通知 Count 依赖属性）。</summary>
    public void NotifyKeyBindingsChanged() => OnPropertyChanged(nameof(HasKeyBindings));

    /// <summary>勾选这一行时要执行的动作，由浮窗的 ViewModel 在造行时注入。</summary>
    private Func<OverlayModItemViewModel, Task>? _toggleMod;

    /// <summary>作者为空时界面上把那行藏掉，免得整行多出一个空行。</summary>
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);

    private OverlayModItemViewModel(Guid id, string folderName, string name, string author, bool isEnabled,
        Uri imagePath, bool isSeen)
    {
        Id = id;
        FolderName = folderName;
        Name = name;
        Author = author;
        _isEnabled = isEnabled;
        ImagePath = imagePath;
        _isSeen = isSeen;
    }

    /// <summary>
    /// 从 <c>ISkinManagerService</c> 给出的条目造一行。
    ///
    /// 只**读**已经加载好的 Mod 设置，不主动去加载：加载是每个 Mod 一次磁盘往返，
    /// 一个角色几十个 Mod，足以让浮窗打开变成卡顿。代价是从没在主窗口里浏览过的角色，
    /// 自定义名 / 自定义图会退化成文件夹名与占位图 —— 与画廊在设置尚未加载时的表现一致。
    /// </summary>
    public static OverlayModItemViewModel FromEntry(CharacterSkinEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var folderName = entry.Mod.Name;
        var name = ModFolderHelpers.GetFolderNameWithoutDisabledPrefix(folderName);
        var author = string.Empty;
        var imagePath = ModModel.PlaceholderImagePath;
        var isSeen = false;

        if (entry.Mod.Settings.GetSettingsLegacy().TryPickT0(out var settings, out _))
        {
            if (!string.IsNullOrWhiteSpace(settings.CustomName))
                name = settings.CustomName;

            author = settings.Author ?? string.Empty;
            imagePath = settings.ImagePath ?? ModModel.PlaceholderImagePath;
            isSeen = settings.SeenAt is not null;
        }

        return new OverlayModItemViewModel(entry.Id, folderName, name, author, entry.IsEnabled, imagePath,
            isSeen);
    }

    /// <summary>
    /// 挂上"勾了这一行要做什么"的回调。造行时由浮窗的 ViewModel 注入 ——
    /// 行自己不该知道 <c>ISkinManagerService</c>，它只负责把点击转出去。
    /// </summary>
    public OverlayModItemViewModel WithToggleHandler(Func<OverlayModItemViewModel, Task> toggleMod)
    {
        _toggleMod = toggleMod;
        return this;
    }

    /// <summary>
    /// 勾选框的点击。每个行各自持有一个命令实例，而生成的 <c>AsyncRelayCommand</c> 默认**不允许并发**，
    /// 于是"同一个 Mod 连点两下"天然被挡住，不同行之间又互不阻塞 —— 正是想要的行为，不必再加一把全局锁。
    /// </summary>
    [RelayCommand]
    private Task ToggleMod() => _toggleMod?.Invoke(this) ?? Task.CompletedTask;

    /// <summary>
    /// 把真实状态重新推给界面。
    ///
    /// 勾选框的 <c>IsChecked</c> 是**单向**绑到 <see cref="IsEnabled"/> 的，用户点一下时控件自己先翻了过去；
    /// 若这次切换其实失败了，<see cref="IsEnabled"/> 的值没有变、也就不会发通知，界面会停在一个假的勾上。
    /// 切换失败时调一下这个方法，把真实值重新压回去。
    /// </summary>
    public void RefreshEnabledState() => OnPropertyChanged(nameof(IsEnabled));

    public override string ToString() => "OverlayModItem: " + Name + " (" + Id + ")";
}