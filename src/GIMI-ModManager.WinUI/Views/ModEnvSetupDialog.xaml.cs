using System.ComponentModel;
using Windows.Storage.Pickers;
using GIMI_ModManager.WinUI.Services.ModEnv;
using GIMI_ModManager.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GIMI_ModManager.WinUI.Views;

/// <summary>
/// One-click Mod environment setup wizard. Shows the idempotency pre-check, lets the user pick a
/// game directory when auto-detection fails, then runs the install pipeline with live progress/logs.
/// Read <see cref="MiFolder"/> and <see cref="ModsFolder"/> after it closes to fill JASM's paths.
/// </summary>
public sealed partial class ModEnvSetupDialog : ContentDialog
{
    public ModEnvSetupViewModel ViewModel { get; }

    /// <summary>Result of the setup run, or null when it did not complete successfully.</summary>
    public ModEnvSetupResult? Result => ViewModel.Result;

    public string? MiFolder => ViewModel.Result?.MiFolder;

    public string? ModsFolder => ViewModel.Result?.ModsFolder;

    private CancellationTokenSource? _cts;

    public ModEnvSetupDialog(ModEnvSetupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // Auto-scroll the log to the bottom as new lines arrive.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModel.LogText) && LogScrollViewer is not null)
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, disableAnimation: true);
    }

    private async void Dialog_OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        StartButton.IsEnabled = false;
        await ViewModel.RunPreCheckAsync();
        StartButton.IsEnabled = ViewModel.CanStart;
    }

    private void Dialog_OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        // Never leave an orphaned install running if the user dismisses mid-run.
        _cts?.Cancel();
    }

    private async void Start_OnClick(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        await ViewModel.RunSetupAsync(_cts.Token);
        StartButton.IsEnabled = ViewModel.CanStart;
    }

    private async void TestLaunch_OnClick(object sender, RoutedEventArgs e) =>
        await ViewModel.RunTestLaunchAsync();

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void BrowseGameDir_OnClick(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker();
        folderPicker.FileTypeFilter.Add("*");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder is null) return;

        ViewModel.GameInstallDir = folder.Path;
        StartButton.IsEnabled = false;
        await ViewModel.RunPreCheckAsync();
        StartButton.IsEnabled = ViewModel.CanStart;
    }

    private async void RedetectGame_OnClick(object sender, RoutedEventArgs e)
    {
        ViewModel.GameInstallDir = null;
        StartButton.IsEnabled = false;
        await ViewModel.RunPreCheckAsync();
        StartButton.IsEnabled = ViewModel.CanStart;
    }
}
