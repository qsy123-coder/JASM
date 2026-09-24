using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.WinUI.Models;

namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>
/// 浮窗角色下拉里的一行：角色本体 + 拿来显示的头像。
///
/// 为什么不把 <see cref="IModdableObject" /> 直接丢进下拉：它的 <c>ImageUri</c> 是可空的（自建角色可以没有图），
/// 而 <c>BitmapImage.UriSource</c> 要的是非空 Uri —— 这里就地补一张占位图兜底，与主界面
/// <see cref="CharacterGridItemModel" />、以及浮窗自己的 <see cref="OverlayModItemViewModel" /> 是同一套做法。
///
/// 它不是 ViewModel：没有可变状态、没有命令，纯粹是给下拉用的显示包装。
/// </summary>
internal sealed class OverlayCharacterItem
{
    public OverlayCharacterItem(IModdableObject character)
    {
        Character = character;
        ImageUri = character.ImageUri ?? ModModel.PlaceholderImagePath;
    }

    /// <summary>角色本体。切角色后拉 Mod 列表、记「上次选的是谁」都要它。</summary>
    public IModdableObject Character { get; }

    /// <summary>下拉里显示的头像。**保证非空**，理由见类注释。</summary>
    public Uri ImageUri { get; }

    /// <summary>下拉里显示的名字。用显示名而不是内部名 —— 内部名是给文件夹和设置文件用的。</summary>
    public string DisplayName => Character.DisplayName;
}