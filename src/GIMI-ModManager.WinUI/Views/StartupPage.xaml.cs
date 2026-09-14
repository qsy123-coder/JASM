using Windows.Storage.Pickers;
using GIMI_ModManager.WinUI.ViewModels;
using GIMI_ModManager.WinUI.ViewModels.SubVms;
using GIMI_ModManager.WinUI.Views.Controls;
using Microsoft.UI.Xaml.Controls;

namespace GIMI_ModManager.WinUI.Views;

public sealed partial class StartupPage : Page
{
    public StartupViewModel ViewModel { get; }

    public StartupPage()
    {
        ViewModel = App.GetService<StartupViewModel>();
        InitializeComponent();
    }

    private void GimiFolder_OnPathChangedEvent(object? sender, FolderSelector.StringEventArgs e)
        => ViewModel.PathToGIMIFolderPicker.Validate(e.Value);


    private void ModsFolder_OnPathChangedEvent(object? sender, FolderSelector.StringEventArgs e)
        => ViewModel.PathToModsFolderPicker.Validate(e.Value);

    /// <summary>
    /// Picks the folder XXMI gets installed into. Cancelling keeps the current choice, so a misclick
    /// cannot silently reset an already-picked location.
    /// </summary>
    private async void XxmiRootFolder_OnClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker();
        folderPicker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder is null) return;

        await ViewModel.SetXxmiRootFolderAsync(folder.Path);
    }

    private async void GameSelector_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        await ViewModel.SetGameCommand.ExecuteAsync(((GameComboBoxEntryVM)e.AddedItems[0]!).Value.ToString()).ConfigureAwait(false);
    }
}