using System;
using System.Linq;
using System.Threading.Tasks;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.GamesService.Interfaces;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.Services.ModHandling;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace GIMI_ModManager.WinUI.Services;

public class ModRandomizationService
{
    private readonly IGameService _gameService;
    private readonly ISkinManagerService _skinManagerService;
    private readonly IWindowManagerService _windowManagerService;
    private readonly CharacterSkinService _characterSkinService;
    private readonly ElevatorService _elevatorService;
    private readonly NotificationManager _notificationManager;
    private readonly ILogger _logger;
    private readonly ILanguageLocalizer _localizer = App.GetService<ILanguageLocalizer>();
    private static readonly Random Random = new();

    public ModRandomizationService(
        IGameService gameService,
        ISkinManagerService skinManagerService,
        IWindowManagerService windowManagerService,
        CharacterSkinService characterSkinService,
        ElevatorService elevatorService,
        NotificationManager notificationManager,
        ILogger logger)
    {
        _gameService = gameService;
        _skinManagerService = skinManagerService;
        _windowManagerService = windowManagerService;
        _characterSkinService = characterSkinService;
        _elevatorService = elevatorService;
        _notificationManager = notificationManager;
        _logger = logger.ForContext<ModRandomizationService>();
    }

    public async Task ShowRandomizeModsDialog()
    {
        var dialog = new ContentDialog
        {
            Title = _localizer.GetLocalizedStringOrDefault("ModRandom_Title", defaultValue: "Randomize Mods"),
            PrimaryButtonText = _localizer.GetLocalizedStringOrDefault("ModRandom_RandomizeBtn", defaultValue: "Randomize"),
            CloseButtonText = _localizer.GetLocalizedStringOrDefault("ModRandom_CancelBtn", defaultValue: "Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        var categories = _gameService.GetCategories();
        var stackPanel = new StackPanel();

        stackPanel.Children.Add(new TextBlock
        {
            Text = _localizer.GetLocalizedStringOrDefault("ModRandom_SelectCategoriesText", defaultValue: "Select the categories you want to randomize mods for:")
        });

        stackPanel.Children.Add(new TextBlock
        {
            Text = _localizer.GetLocalizedStringOrDefault("ModRandom_NoteText", defaultValue: "Note: This will only randomize mod folders that are meant to only have one mod active. So 'Others __' folders will not be randomized. While only one mod wil be enabled per in game character skin"),
            TextWrapping = TextWrapping.WrapWholeWords,
            Margin = new Thickness(0, 0, 0, 10)
        });

        foreach (var category in categories)
        {
            var checkBox = new CheckBox
            {
                Content = category.DisplayNamePlural,
                IsChecked = true
            };
            stackPanel.Children.Add(checkBox);
        }

        stackPanel.Children.Add(new CheckBox
        {
            Margin = new Thickness(0, 10, 0, 0),
            Content = _localizer.GetLocalizedStringOrDefault("ModRandom_AllowNoModsText", defaultValue: "Allow no mods as a result. This means it is possible for no mods to be enabled for a mod folder"),
            IsChecked = false
        });

        stackPanel.Children.Add(new TextBlock
        {
            Text = _localizer.GetLocalizedStringOrDefault("ModRandom_SuggestionText", defaultValue: "I suggest creating a preset (or a backup) of your mods before randomizing if you have a lot of enabled mods"),
            TextWrapping = TextWrapping.WrapWholeWords,
            Margin = new Thickness(0, 10, 0, 0)
        });

        dialog.Content = stackPanel;

        var result = await _windowManagerService.ShowDialogAsync(dialog);
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var selectedCategories = stackPanel.Children
            .OfType<CheckBox>()
            .SkipLast(1)
            .Where(c => c.IsChecked == true)
            .Select(c => categories.First(cat => cat.DisplayNamePlural.Equals(c.Content)))
            .ToList();

        var allowNoMods = stackPanel.Children
            .OfType<CheckBox>()
            .Last()
            .IsChecked == true;

        if (selectedCategories.Count == 0)
        {
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("ModRandom_NoCategoriesSelectedTitle", defaultValue: "No categories selected"), _localizer.GetLocalizedStringOrDefault("ModRandom_NoCategoriesSelectedMessage", defaultValue: "No categories were selected to randomize."),
                TimeSpan.FromSeconds(5));
            return;
        }

        try
        {
            await Task.Run(async () =>
            {
                var modLists = _skinManagerService.CharacterModLists
                    .Where(modList => selectedCategories.Contains(modList.Character.ModCategory))
                    .Where(modList => !modList.Character.IsMultiMod)
                    .ToList();

                foreach (var modList in modLists)
                {
                    var mods = modList.Mods.ToList();

                    if (mods.Count == 0)
                        continue;

                    // Need special handling for characters because they have an in game skins
                    if (modList.Character is ICharacter { Skins.Count: > 1 } character)
                    {
                        var skinModMap = await _characterSkinService.GetAllModsBySkinAsync(character)
                            .ConfigureAwait(false);
                        if (skinModMap is null)
                            continue;

                        // Don't know what to do with undetectable mods
                        skinModMap.UndetectableMods.ForEach(mod => modList.DisableMod(mod.Id));

                        foreach (var (_, skinMods) in skinModMap.ModsBySkin)
                        {
                            if (skinMods.Count == 0)
                                continue;

                            foreach (var mod in skinMods.Where(mod => modList.IsModEnabled(mod)))
                            {
                                modList.DisableMod(mod.Id);
                            }

                            var randomModIndex = Random.Next(0, skinMods.Count + (allowNoMods ? 1 : 0));

                            if (randomModIndex == skinMods.Count)
                                continue;

                            modList.EnableMod(skinMods.ElementAt(randomModIndex).Id);
                        }

                        continue;
                    }

                    foreach (var characterSkinEntry in mods.Where(characterSkinEntry => characterSkinEntry.IsEnabled))
                    {
                        modList.DisableMod(characterSkinEntry.Id);
                    }

                    var randomIndex = Random.Next(0, mods.Count + (allowNoMods ? 1 : 0));
                    if (randomIndex == mods.Count)
                        continue;

                    modList.EnableMod(mods[randomIndex].Id);
                }
            });
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to randomize mods");
            _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("ModRandom_FailedTitle", defaultValue: "Failed to randomize mods"), e.Message, TimeSpan.FromSeconds(5));
            return;
        }

        if (_elevatorService.ElevatorStatus == ElevatorStatus.Running)
        {
            await Task.Run(() => _elevatorService.RefreshGenshinMods());
        }

        _notificationManager.ShowNotification(_localizer.GetLocalizedStringOrDefault("ModRandom_ModsRandomizedTitle", defaultValue: "Mods randomized"),
            string.Format(_localizer.GetLocalizedStringOrDefault("ModRandom_ModsRandomizedMessage", defaultValue: "Mods have been randomized for the categories: {0}"),
                string.Join(", ", selectedCategories.Select(c => c.DisplayNamePlural))),
            TimeSpan.FromSeconds(5));
    }
}