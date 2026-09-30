using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using CommunityToolkit.WinUI;
using GIMI_ModManager.WinUI.Helpers.Xaml;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.ViewModels;
using GIMI_ModManager.WinUI.ViewModels.SubVms;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Serilog;

namespace GIMI_ModManager.WinUI.Views;

public sealed partial class CharactersPage : Page
{
    public CharactersViewModel ViewModel { get; }

    public CharactersPage()
    {
        ViewModel = App.GetService<CharactersViewModel>();
        InitializeComponent();
        Loaded += (sender, args) =>
        {
            SearchBox.Focus(FocusState.Keyboard);
            ViewModel.SimpleSelectProcessDialogVM.Dialog = SelectProcessDialog;
        };
        ViewModel.OnScrollToCharacter += ViewModel_OnScrollToCharacter;
    }

    private async void ViewModel_OnScrollToCharacter(object? sender, ScrollToCharacterArgs e)
    {
        var character = e?.Character?.Character;
        if (character == null!) return;

        await CharactersGridView.AwaitUiElementLoaded(TimeSpan.FromMilliseconds(500));


        var item = CharactersGridView.Items.FirstOrDefault(x =>
            ((CharacterGridItemModel)x).Character.InternalNameEquals(character));

        if (item is null)
        {
            Debugger.Break();
        }

        await CharactersGridView.SmoothScrollIntoViewWithItemAsync(item, ScrollItemPlacement.Center, disableAnimation: true,
            scrollIfVisible: false);
    }


    private void AutoSuggestBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ViewModel.AutoSuggestBox_TextChanged(sender.Text);
    }


    private void CharacterSearchKeyShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
    }

    private async void SearchBox_OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        await ViewModel.SuggestionBox_Chosen((CharacterGridItemModel?)args.ChosenSuggestion);
    }

    private void ImageCommandsFlyout_OnOpening(object? sender, object e)
    {
        if (sender is not MenuFlyout menuFlyout)
            return;

        if (menuFlyout.Target.DataContext is not CharacterGridItemModel character)
            return;

        ViewModel.OnRightClickContext(character);
    }

    private void SetGridDropHereVisibility(Grid characterThumbnail, Visibility visibility)
    {
        var dropHereIcon = ((FontIcon)characterThumbnail.FindName("DropHereIcon"));
        dropHereIcon.Visibility = visibility;
        var dropHereBorder = ((Border)characterThumbnail.FindName("DropHereBorder"));
        dropHereBorder.Visibility = visibility;
    }

    private void CharacterThumbnail_OnDragEnter(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;

        var gridItem = ((Grid)sender);
        SetGridDropHereVisibility(gridItem, Visibility.Visible);
    }

    private void CharacterThumbnail_OnDragLeave(object sender, DragEventArgs e)
    {
        var gridItem = ((Grid)sender);
        SetGridDropHereVisibility(gridItem, Visibility.Collapsed);
    }

    private async void CharacterThumbnail_OnDrop(object sender, DragEventArgs e)
    {
        // 卡片认领这一下：别再冒泡到页面根 Grid —— 那儿是「自动识别」，会照另一个角色再装一遍
        e.Handled = true;
        HideAutoDetectArea();

        if (((Grid)sender).DataContext is CharacterGridItemModel characterGridItem)
        {
            var urlFormats = new[] { "Text", "UniformResourceLocatorW", "UniformResourceLocator" };
            if (urlFormats.All(format => e.DataView.Contains(format)))
            {
                try
                {
                    var uri = await e.DataView.GetWebLinkAsync();
                    await ViewModel.ModUrlDroppedOnCharacterAsync(characterGridItem, uri);
                }
                catch (Exception)
                {
                    // ignored
                }
            }
            else
                await ViewModel.ModDroppedOnCharacterAsync(characterGridItem, await e.DataView.GetStorageItemsAsync());
        }

        var gridItem = ((Grid)sender);
        SetGridDropHereVisibility(gridItem, Visibility.Collapsed);
    }

    /// <summary>
    /// 毛玻璃提示层平时是收起的：Collapsed 的元素收不到拖拽事件，所以显形只能由页面根 Grid 点。
    /// </summary>
    private void PageRoot_OnDragEnter(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        ShowAutoDetectArea();
    }

    /// <summary>
    /// DragOver 每次指针移动都会来一发。留着它是为了兜住这种情况：指针挪到角色卡片上方时，
    /// 根 Grid 可能先收到一次 DragLeave（拖拽事件会在子元素之间来回冒），提示层就灭了 ——
    /// 靠这一手补回来。显示是幂等的，多来几次没有代价。
    /// </summary>
    private void PageRoot_OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        ShowAutoDetectArea();
    }

    private void PageRoot_OnDragLeave(object sender, DragEventArgs e) => HideAutoDetectArea();

    /// <summary>
    /// 落在角色列表上的拖放（不是某张卡片）：走「自动识别」—— 解压、认出属于哪个角色、交给安装向导。
    ///
    /// <para>
    /// 提示层是 <c>IsHitTestVisible="False"</c> 的，接不到事件，所以真正的落点是
    /// 「列表空白处 → 冒泡到页面根 Grid」。卡片那条路会在自己的 Drop 里把事件标成已处理
    /// （按落点那张卡片的角色装），到不了这里。
    /// </para>
    /// </summary>
    private async void PageRoot_OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideAutoDetectArea();

        var storageItems = await e.DataView.GetStorageItemsAsync();
        Log.Information("Auto detect drop: {ItemCount} item(s)", storageItems.Count);

        await ViewModel.ModDroppedOnAutoDetectAreaAsync(storageItems);
    }

    private void ShowAutoDetectArea() => AutoDetectOverlay.Visibility = Visibility.Visible;

    private void HideAutoDetectArea() => AutoDetectOverlay.Visibility = Visibility.Collapsed;

    private void BitmapImage_OnImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Log.Error("Failed to load dock panel element icon. Reason: {e}", e.ErrorMessage);
    }

    private void Selector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.DockPanelVM.ElementSelectionChanged(e.AddedItems.OfType<ElementIcon>(),
            e.RemovedItems.OfType<ElementIcon>());
    }

    private void SortingComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ViewModel.SortByCommand.Execute(e.AddedItems.OfType<CharactersViewModel.GridItemSortingMethod>());
    }
}