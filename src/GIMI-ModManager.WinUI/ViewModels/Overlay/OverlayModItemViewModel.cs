using CommunityToolkit.Mvvm.ComponentModel;
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

    public override string ToString() => "OverlayModItem: " + Name + " (" + Id + ")";
}