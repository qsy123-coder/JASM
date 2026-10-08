using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using CommunityToolkit.WinUI;
using GIMI_ModManager.WinUI.Helpers;
using GIMI_ModManager.WinUI.Helpers.Xaml;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.ViewModels;
using GIMI_ModManager.WinUI.ViewModels.SubVms;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

        // 压在卡片上就让毛玻璃退让 —— 落在卡片上时角色是卡片说了算，而提示层写着「松手自动识别」，
        // 挡着的话用户既看不清自己压的是哪张卡，也被那行字带偏（见 PageRoot_OnDrop 的注释）
        HideAutoDetectArea();

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
            // 记一笔落点卡片：落在哪张卡上不等于用户想装给谁（下面就把包改判给自动识别了），
            // 事后只能靠这条日志分辨「他瞄的是谁」，所以照记不误
            Log.Information("Drop on character card: {Character}", characterGridItem.Character.InternalName.Id);

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
            {
                var storageItems = await e.DataView.GetStorageItemsAsync();

                // 压缩包 / 自解压 exe 一律改判给自动识别：用户拖包进来要的是「你帮我认这是谁的」，
                // 落点在哪张卡上并不代表他知道是谁的 —— 实机连着两次都把包拖在了卡片上，
                // 结果装进了压到的那张卡的角色（其中一次还被他瞄着的角色不是同一只）。
                // 文件夹不在此列：文件夹是用户自己整理好的「这就是 X 的」，仍旧装进落点那个角色。
                if (storageItems.Count > 0 && storageItems.All(item => item is StorageFile))
                {
                    Log.Information("A file drop on a character card is handed to auto detect");
                    await ViewModel.ModDroppedOnAutoDetectAreaAsync(storageItems);
                }
                else
                {
                    await ViewModel.ModDroppedOnCharacterAsync(characterGridItem, storageItems);
                }
            }
        }

        var gridItem = ((Grid)sender);
        SetGridDropHereVisibility(gridItem, Visibility.Collapsed);
    }

    /// <summary>
    /// 毛玻璃提示层平时是收起的：Collapsed 的元素收不到拖拽事件，所以显形只能由页面根 Grid 点。
    /// </summary>
    private void PageRoot_OnDragEnter(object sender, DragEventArgs e)
    {
        DragProbe.Log("概览页", e);

        e.AcceptedOperation = DataPackageOperation.Copy;
        UpdateAutoDetectArea(e);
    }

    /// <summary>
    /// DragOver 每次指针移动都会来一发。除了兜住「拖拽事件在子元素之间来回冒」导致的误灭，
    /// 主要靠它把状态纠回来：拖拽中指针一直在动，所以每一动都重新判一次压在卡片上还是空白处。
    /// </summary>
    private void PageRoot_OnDragOver(object sender, DragEventArgs e)
    {
        // 探针在 DragOver 也挂一份：指针在页面内移动时 DragEnter 不会重发，只有这里能证明
        // 「拖拽还压在页面上」（内部有 3 秒节流，不会刷屏）
        DragProbe.Log("概览页/DragOver", e);

        e.AcceptedOperation = DataPackageOperation.Copy;
        UpdateAutoDetectArea(e);
    }

    private void PageRoot_OnDragLeave(object sender, DragEventArgs e) => HideAutoDetectArea();

    /// <summary>
    /// 落在角色列表上的拖放（不是某张卡片）：走「自动识别」—— 解压、认出属于哪个角色、交给安装向导。
    ///
    /// <para>
    /// 提示层是 <c>IsHitTestVisible="False"</c> 的，接不到事件，所以真正的落点是
    /// 「列表空白处 → 冒泡到页面根 Grid」。
    /// </para>
    ///
    /// <para>
    /// <b>为什么这里还要自己判一次「压没压在卡片上」</b>：卡片那条路只在自己的 Drop 里把事件标成
    /// <c>Handled</c>，而拖拽事件的 Handled 在 AllowDrop 链上未必拦得住冒泡。一旦漏到这里，
    /// 同一份包就会「卡片装进 A（用户瞄的角色）+ 自动识别认出 B」—— 一次拖拽装进两个角色，
    /// 用户看到的是「我明明拖到 X 上，Y 里也冒出来一份」。所以这里再兜一道：落点在卡片上就整件事归卡片。
    /// </para>
    /// </summary>
    private async void PageRoot_OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        HideAutoDetectArea();

        if (IsPointerOverCharacterCard(e))
        {
            Log.Information("A drop on the page root was over a character card; the card path owns it");
            return;
        }

        var storageItems = await e.DataView.GetStorageItemsAsync();
        // formats 一起记：「这一拖里没有文件」和「事件根本没到」是两回事，日志要能分开
        Log.Information("Auto detect drop on the page root: {ItemCount} item(s)；formats=[{Formats}]",
            storageItems.Count, string.Join(",", e.DataView.AvailableFormats));

        await ViewModel.ModDroppedOnAutoDetectAreaAsync(storageItems);
    }

    /// <summary>
    /// 指针压在角色卡片上 → 收起毛玻璃（落点角色由卡片定，提示层只会挡视线、还会被
    /// 「松手自动识别」那行字带偏）；在列表空白处 → 亮出来，那里的落下确实走自动识别。
    ///
    /// <para>
    /// 每次都从事件本身重判，不靠 Enter/Leave 记账 —— 拖拽事件在父子元素之间来回冒，
    /// 记账会飘（上一版就是因此加了这个每次 DragOver 都重判的兜底）。
    /// </para>
    /// </summary>
    private void UpdateAutoDetectArea(DragEventArgs e)
    {
        if (IsPointerOverCharacterCard(e))
            HideAutoDetectArea();
        else
            ShowAutoDetectArea();
    }

    /// <summary>
    /// 这一发拖拽事件是不是发生在某张角色卡片上：拖拽事件的 <c>OriginalSource</c> 就是指针底下的元素，
    /// 顺着它往上找有没有 <c>DataContext</c> 是 <see cref="CharacterGridItemModel" /> 的祖先 —— 那就是卡片。
    /// </summary>
    private bool IsPointerOverCharacterCard(DragEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;

        while (current is not null)
        {
            if (current is FrameworkElement { DataContext: CharacterGridItemModel })
                return true;

            if (ReferenceEquals(current, this))
                break;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
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