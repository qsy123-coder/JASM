using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.Core.Entities;
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

    /// <summary>勾选这一行时要执行的动作，由浮窗的 ViewModel 在造行时注入。</summary>
    private Func<OverlayModItemViewModel, Task>? _toggleMod;

    /// <summary>作者为空时界面上把那行藏掉，免得整行多出一个空行。</summary>
    public bool HasAuthor => !string.IsNullOrWhiteSpace(Author);

    private OverlayModItemViewModel(Guid id, string folderName, string name, string author, bool isEnabled,
        Uri imagePath)
    {
        Id = id;
        FolderName = folderName;
        Name = name;
        Author = author;
        _isEnabled = isEnabled;
        ImagePath = imagePath;
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

        if (entry.Mod.Settings.GetSettingsLegacy().TryPickT0(out var settings, out _))
        {
            if (!string.IsNullOrWhiteSpace(settings.CustomName))
                name = settings.CustomName;

            author = settings.Author ?? string.Empty;
            imagePath = settings.ImagePath ?? ModModel.PlaceholderImagePath;
        }

        return new OverlayModItemViewModel(entry.Id, folderName, name, author, entry.IsEnabled, imagePath);
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