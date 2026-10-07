using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using CommunityToolkit.WinUI.UI.Animations;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Helpers.Xaml;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.ViewModels.CharacterDetailsViewModels;
using GIMI_ModManager.WinUI.ViewModels.CharacterDetailsViewModels.SubViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Serilog;

namespace GIMI_ModManager.WinUI.Views.CharacterDetailsPages;

public sealed partial class CharacterDetailsPage : Page
{
    public CharacterDetailsViewModel ViewModel { get; } = App.GetService<CharacterDetailsViewModel>();

    public CharacterDetailsPage()
    {
        InitializeComponent();
        CharacterCard.ViewModel = ViewModel;
        ModPane.ViewModel = ViewModel.ModPaneVM;
        ModGrid.ViewModel = ViewModel.ModGridVM;
        ModGrid.ViewModel.OnModsReloaded += OnModsReloaded;

        ViewModel.OnModObjectLoaded += OnModObjectLoaded;
        ViewModel.OnModsLoaded += OnModsLoaded;
        ViewModel.OnInitializingFinished += OnInitializingFinished;

        ViewModel.ContextMenuVM.CloseFlyout += ContextMenuVM_CloseFlyout;
    }


    private void OnModObjectLoaded(object? sender, EventArgs e)
    {
        ViewModel.OnModObjectLoaded -= OnModObjectLoaded;
        ViewModel.GridLoadedAwaiter = () => ModGrid.DataGrid.AwaitItemsSourceLoaded(ViewModel.CancellationToken);
        var button = CharacterCard.SelectSkinBox;

        if (!ViewModel.IsCharacter || ViewModel.Character.Skins.Count == 0) return;

        var tooltip = ToolTipService.GetToolTip(button);
        if (tooltip is ToolTip) return;
        var toolTip = new ToolTip
        {
            Content = "This character only has one default in-game skin, so you can't change it.",
            Placement = PlacementMode.Bottom
        };

        ToolTipService.SetToolTip(button, toolTip);
    }


    private void OnModsLoaded(object? sender, EventArgs e)
    {
        ViewModel.OnModsLoaded -= OnModsLoaded;
        PageInitLoader.Visibility = Visibility.Collapsed;
        RightWorkingArea.Visibility = Visibility.Visible;

        if (ModGrid.ViewModel.ModdableObjectHasAnyMods) return;
        ShowNoModsElement();
    }

    private void OnModsReloaded(object? sender, EventArgs eventArgs)
    {
        if (ViewModel.ModGridVM.ModdableObjectHasAnyMods)
            HideNoModsElement();
        else
            ShowNoModsElement();
    }

    private void OnInitializingFinished(object? sender, EventArgs e)
    {
        ViewModel.OnInitializingFinished -= OnInitializingFinished;
        ModGrid.DataGrid.ContextFlyout = ModRowFlyout;
        ModGrid.DataGrid.Focus(FocusState.Programmatic);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (CharacterCard?.ItemHero != null) // Trying to fix an argument null exception
            this.RegisterElementForConnectedAnimation("animationKeyContentGrid", CharacterCard.ItemHero);
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (e.NavigationMode == NavigationMode.Back)
        {
            var navigationService = App.GetService<INavigationService>();
            if (ViewModel.ShownModObject != null!)
                navigationService.SetListDataItemForNextConnectedAnimation(ViewModel.ShownModObject);
        }
    }


    private void ShowNoModsElement()
    {
        var noModsElement = EnsureNoModsUIElementAdded();
        noModsElement.Visibility = Visibility.Visible;
        ModGrid.Visibility = Visibility.Collapsed;
        ModPane.Visibility = Visibility.Collapsed;
        ModPaneSplitter.Visibility = Visibility.Collapsed;
        SearchModsTextBox.Visibility = Visibility.Collapsed;

        ModListArea.AllowDrop = false;
        MainContentArea.AllowDrop = true;
    }

    private void HideNoModsElement()
    {
        var noModsElement = FindNoModsUIElement();
        if (noModsElement is not null)
            noModsElement.Visibility = Visibility.Collapsed;


        ModGrid.Visibility = Visibility.Visible;
        ModPane.Visibility = Visibility.Visible;
        ModPaneSplitter.Visibility = Visibility.Visible;
        SearchModsTextBox.Visibility = Visibility.Visible;

        ModListArea.AllowDrop = true;
        MainContentArea.AllowDrop = false;
    }

    private StackPanel? FindNoModsUIElement()
    {
        if (MainContentArea.FindName("NoModsStackPanel") is StackPanel existingStackPanel)
            return existingStackPanel;

        return null;
    }

    private StackPanel EnsureNoModsUIElementAdded()
    {
        var existingStackPanel = FindNoModsUIElement();
        if (existingStackPanel is not null)
            return existingStackPanel;

        var stackPanel = new StackPanel()
        {
            Name = "NoModsStackPanel",
            Visibility = Visibility.Collapsed,
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AllowDrop = false
        };

        var title = new TextBlock()
        {
            Text = "No mods found for this character 😖",
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AllowDrop = false
        };
        stackPanel.Children.Add(title);


        var backgroundGrid = new Grid()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            AllowDrop = false,
            Background = new SolidColorBrush(Colors.Transparent)
        };

        stackPanel.Children.Add(backgroundGrid);

        var dottedLineBox = new Border
        {
            BorderBrush = Resources["SystemControlForegroundBaseMediumHighBrush"] as SolidColorBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),

            Width = 300,
            Height = 250,
            Margin = new Thickness(10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(5),
            AllowDrop = false
        };

        // Create the TextBlock for "Drop Mods Here"
        var dropText = new TextBlock
        {
            Text = "Drop Mods Here",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 20,
            AllowDrop = false
        };


        backgroundGrid.Children.Add(dottedLineBox);
        dottedLineBox.Child = dropText;

        MainContentArea.Children.Add(stackPanel);

        return stackPanel;
    }


    private void KeyboardAccelerator_OnInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchModsTextBox.Focus(FocusState.Keyboard);
    }

    private void SearchModsTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        ViewModel.SearchMods(SearchModsTextBox.Text);
    }

    private async void ModListArea_OnDragEnter(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.WebLink))
            {
                var uri = await e.DataView.GetWebLinkAsync();
                if (ViewModel.DescribeDragDropUrlRefusal(uri) is { } refusal)
                    NotifyDragRefused(refusal);
                else
                    e.AcceptedOperation = DataPackageOperation.Copy;
            }
            else if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                try
                {
                    var storageItems = await e.DataView.GetStorageItemsAsync();
                    if (ViewModel.DescribeDragDropRefusal(storageItems) is { } refusal)
                        NotifyDragRefused(refusal);
                    else
                        e.AcceptedOperation = DataPackageOperation.Copy;
                }
                catch (COMException exception)
                {
                    // When drag and dropping a folder from within an archive in WinRAR, GetStorageItemsAsync throws a COMException
                    // For this case, assume this is a valid drag and drop operation as the command itself will also check if the items are valid
                    // when (exception.HResult == -2147221404) HResult that is thrown specifically for WinRAR

                    e.AcceptedOperation = DataPackageOperation.Copy;

                    if (exception.HResult != -2147221404)
                    {
                        Log.Error(exception, "Error while checking if the dragged items are valid.");
                    }
                }
            }
            else
            {
                // 既不是链接也不是文件：文字、图片位图这些都落不到这里，说一句比干瞪禁止光标强
                NotifyDragRefused("这里只接 Mod 包（.zip / .rar / .7z / 自解压 exe）、Mod 文件夹，"
                                  + "或 GameBanana 的 Mod 链接。");
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void ModListArea_OnDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.WebLink))
            {
                await ViewModel.DragDropModUrlAsync(await e.DataView.GetWebLinkAsync());
            }
            else if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                await ViewModel.DragDropModAsync(await e.DataView.GetStorageItemsAsync());
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>两次「这一拖收不了」提示之间的最短间隔，见 <see cref="NotifyDragRefused"/>。</summary>
    private const int DragRefusalNoticeMinimumIntervalMs = 3000;

    /// <summary>上一次弹提示的时刻（<see cref="Environment.TickCount64"/>；null = 还没弹过）。</summary>
    private long? _lastDragRefusalNoticeTicks;

    /// <summary>
    /// 收不了这一拖时，给用户一句话说清楚为什么。
    ///
    /// <para>
    /// <b>为什么必须给</b>：XAML 的拖放不接受就是不设 <c>AcceptedOperation</c>，用户那边只有禁止光标，
    /// 而且松手连 Drop 事件都不会来 —— 界面上没有任何东西说明原因。于是「拖了两个包」「拖了个 exe」
    /// 「JASM 正忙」这三种毫不相干的来路，在用户眼里长得一模一样。
    /// </para>
    ///
    /// <para>
    /// <b>为什么要节流</b>：<c>DragEnter</c> 在指针每次进入元素时都会来一发（跨子元素边界也算），
    /// 同一个理由反复弹通知会盖掉用户真正要看的东西。这里按时间节流、不做「一次拖拽只记一次」的记账 ——
    /// 拖拽事件会在父子元素之间来回冒，Enter/Leave 的配对并不可靠（<c>CharactersPage</c> 里那条
    /// 「每次 DragOver 都重判」的注释就是为同一件事）。
    /// </para>
    /// </summary>
    private void NotifyDragRefused(string reason)
    {
        var now = Environment.TickCount64;

        if (_lastDragRefusalNoticeTicks is { } last && now - last < DragRefusalNoticeMinimumIntervalMs)
            return;

        _lastDragRefusalNoticeTicks = now;

        App.GetService<NotificationManager>().ShowNotification("拖拽安装没接住", reason, TimeSpan.FromSeconds(8));
    }

    private void ViewToggleSwitch_OnToggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.GoToGalleryScreenCommand.CanExecute(null))
            ViewModel.GoToGalleryScreenCommand.ExecuteAsync(null);
    }

    #region ModRowFlyout

    private void ContextMenuVM_CloseFlyout(object? sender, EventArgs e) => ModRowFlyout.Hide();

    private void ModRowFlyout_OnOpening(object? sender, object e)
    {
        if (!ViewModel.ContextMenuVM.CanOpenContextMenu)
        {
            ModRowFlyout.Hide();
            return;
        }
    }

    private void ModRowFlyout_OnOpened(object? sender, object e) => MoveModSearchBox.Focus(FocusState.Programmatic);


    private void ModRowFlyout_OnClosing(FlyoutBase sender, FlyoutBaseClosingEventArgs args) => ViewModel.ContextMenuVM.OnFlyoutClosing();

    private void MoveModSearch_OnTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            ViewModel.ContextMenuVM.SearchTextChanged(sender.Text);
    }

    private void MoveModSearch_OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        ViewModel.ContextMenuVM.OnSuggestionChosen((SuggestedModObject)args.ChosenSuggestion);
        MoveModsButton.Focus(FocusState.Programmatic);
    }

    #endregion
}