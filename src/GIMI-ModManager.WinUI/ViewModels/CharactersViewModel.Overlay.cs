using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.Services.Overlay;

namespace GIMI_ModManager.WinUI.ViewModels;

/// <summary>
/// 角色概览页的「打开浮窗」入口。
///
/// 浮窗本身只服务鸣潮，所以按钮在别的游戏下直接不显示；打开动作走
/// <see cref="OverlayWindowService.ShowOverlayAsync"/>，与全局热键同一条显隐路径，
/// 不依赖热键是否注册成功（热键被占时按钮仍然可用）。
/// </summary>
public partial class CharactersViewModel
{
    /// <summary>当前游戏是否支持游戏内浮窗（目前只有鸣潮）。</summary>
    public bool IsOverlayAvailable => _gameService.GameShortName == "WuWa";

    [RelayCommand]
    private async Task OpenOverlayAsync()
    {
        var overlay = App.GetService<OverlayWindowService>();
        if (await overlay.ShowOverlayAsync())
            return;

        App.GetService<NotificationManager>().ShowNotification(
            "浮窗", "当前游戏不支持浮窗，或浮窗尚未就绪。", TimeSpan.FromSeconds(6));
    }
}