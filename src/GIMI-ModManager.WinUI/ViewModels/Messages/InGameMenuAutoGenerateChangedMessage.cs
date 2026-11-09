namespace GIMI_ModManager.WinUI.ViewModels.Messages;

/// <summary>
/// 「安装 Mod 后自动生成菜单」开关在设置页与浮窗之间同步。
///
/// 两处显示的是同一份设置（<c>InGameMenuSettings.AutoGenerateOnInstall</c>）。没有这条消息时，
/// 一处改了另一处要等到重新打开/重启才更新，用户会以为开关没生效。
/// </summary>
public record InGameMenuAutoGenerateChangedMessage(object Sender, bool Value);
