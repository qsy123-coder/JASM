using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.Services.AppManagement;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModHandling;

/// <summary>
/// 「这包是谁的」拿不准时，把候选角色摆出来让用户点一个。
///
/// <para>
/// 只有拿不准才会走到这里：<c>CharacterNameMatcher</c> 给出明确最高分时直接落地，不打扰用户。
/// 候选就是它排好序的那几条。榜上都没有时用户只能取消 —— 所以对话框里必须写清出路：
/// 把包直接拖到那个角色的卡片上（那条路角色已定，不需要认）。
/// </para>
///
/// <para>
/// 与 <see cref="ArchivePasswordService"/> 一样是<b>代码搭的</b>对话框，理由相同。
/// 候选列表只有名字没有头像：头像要从游戏数据里解析资源 URI，为这一处再走一遍那条链路不划算，
/// 而列表里显示的正是角色的<b>显示名</b>（中文），认人足够。
/// </para>
/// </summary>
public sealed class CharacterPickerService
{
    private readonly IWindowManagerService _windowManagerService;
    private readonly ILogger _logger;
    private readonly ILanguageLocalizer _localizer = App.GetService<ILanguageLocalizer>();

    public CharacterPickerService(IWindowManagerService windowManagerService, ILogger logger)
    {
        _windowManagerService = windowManagerService;
        _logger = logger.ForContext<CharacterPickerService>();
    }

    /// <summary>
    /// 让用户从候选里挑一个角色。返回 <c>null</c> = 取消（或对话框弹不起来）。
    /// </summary>
    /// <param name="candidates">候选，按可信度从高到低（空列表也允许：那就只告诉用户「没认出来」）。</param>
    public async Task<ICharacter?> PickAsync(IReadOnlyList<CharacterNameMatch> candidates)
    {
        ICharacter? selected = null;

        // 用一组 RadioButton 而不是 ListView：单选语义天然、纯代码就能搭出来
        // （ListView 想显示两行得给 DataTemplate，代码里造 DataTemplate 要写 XamlReader，不值当）
        var options = new StackPanel { Spacing = 4 };

        foreach (var candidate in candidates)
        {
            var radio = new RadioButton
            {
                GroupName = "DroppedPackageCharacter",
                Content = candidate.DisplayName,
                Tag = candidate.Character
            };

            options.Children.Add(radio);
        }

        var content = new StackPanel();

        content.Children.Add(new TextBlock
        {
            Text = _localizer.GetLocalizedStringOrDefault("CharacterPicker_NoConfidentMatchHint",
                defaultValue: "Could not tell which character this package belongs to."),
            TextWrapping = TextWrapping.Wrap
        });

        if (candidates.Count > 0)
        {
            content.Children.Add(new TextBlock
            {
                Text = _localizer.GetLocalizedStringOrDefault("CharacterPicker_SelectHint",
                    defaultValue:
                    "Pick one below if it is in the list. Otherwise cancel and drop the package onto that character's card."),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });

            content.Children.Add(new ScrollViewer
            {
                Content = options,
                MaxHeight = 320,
                Margin = new Thickness(0, 10, 0, 0)
            });
        }

        var dialog = new ContentDialog
        {
            // XamlRoot 由 ShowDialogAsync 设
            Title = _localizer.GetLocalizedStringOrDefault("CharacterPicker_Title",
                defaultValue: "Which character is this mod for?"),
            PrimaryButtonText = _localizer.GetLocalizedStringOrDefault("CharacterPicker_ConfirmBtn",
                defaultValue: "That's the one"),
            CloseButtonText = candidates.Count > 0
                ? _localizer.GetLocalizedStringOrDefault("CharacterPicker_CancelBtn", defaultValue: "Cancel")
                : _localizer.GetLocalizedStringOrDefault("CharacterPicker_OkBtn", defaultValue: "OK"),
            DefaultButton = ContentDialogButton.Primary,
            Content = content,
            // 没选中任何一条就没得确认（候选为空时这个按钮永远是灰的）
            IsPrimaryButtonEnabled = false
        };

        // 选好了才让点「就是这个」
        foreach (var radio in options.Children.OfType<RadioButton>())
        {
            radio.Checked += (sender, _) =>
            {
                selected = ((RadioButton)sender).Tag as ICharacter;
                dialog.IsPrimaryButtonEnabled = true;
            };
        }

        ContentDialogResult result;
        try
        {
            result = await _windowManagerService.ShowDialogAsync(dialog);
        }
        catch (InvalidOperationException e)
        {
            // 同一窗口已经有别的对话框开着（ShowDialogAsync 的守卫）：当作用户没选，让调用方安静退出
            _logger.Warning(e, "Could not show the character picker because another dialog is open");
            return null;
        }

        return result == ContentDialogResult.Primary ? selected : null;
    }
}