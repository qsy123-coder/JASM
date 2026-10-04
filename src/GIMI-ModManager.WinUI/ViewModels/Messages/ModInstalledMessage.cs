namespace GIMI_ModManager.WinUI.ViewModels.Messages;

/// <summary>
/// 刚装好一个 Mod（拖进来、走安装向导、商店部署都汇到这里）。
///
/// 为什么需要这条：游戏内浮窗的角色/Mod 列表不是实时视图，是拿 <c>ISkinManagerService</c> 的快照
/// 建出来的（见 <c>OverlayViewModel.RefreshCharacters</c>）。没有这条消息，用户装完 Mod 还得先
/// 把浮窗关掉再唤出一次才看得到 —— 实机反馈就是「拖进去装完，浮窗没反应」。
/// </summary>
/// <param name="CharacterInternalName">
/// 装到哪个角色上了。浮窗收到就把选中**切到这个角色** —— 否则它会留在原角色，
/// 用户装完看不见刚装的东西（实机反馈：「应该跳转到对应角色下」）。
/// </param>
public record ModInstalledMessage(object Sender, string? CharacterInternalName = null);
