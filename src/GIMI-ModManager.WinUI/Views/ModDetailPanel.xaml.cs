using System.Text.Json;
using GIMI_ModManager.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace GIMI_ModManager.WinUI.Views;

public sealed partial class ModDetailPanel : UserControl
{
    private ModMarketMod? _currentMod;

    public ModDetailPanel()
    {
        InitializeComponent();
        SlideOutStoryboard.Completed += (_, _) =>
        {
            PanelRoot.Visibility = Visibility.Collapsed;
            _currentMod = null;
        };
    }

    public void Show(ModMarketMod mod)
    {
        _currentMod = mod;
        DataContext = mod;
        BuildGallery(mod);
        BuildDriveLinks(mod);
        SelectTab(overview: true);

        // Reset to off-screen position for the slide-in animation
        DrawerBorder.RenderTransform = new TranslateTransform { X = 360 };
        PanelRoot.Visibility = Visibility.Visible;
        SlideInStoryboard.Begin();
    }

    public void Hide()
    {
        SlideOutStoryboard.Begin();
    }

    // ── Tab switching ───────────────────────────────────

    private void SelectTab(bool overview)
    {
        OverviewContent.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        DescriptionContent.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;
        OverviewTabBtn.Foreground = overview
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        DescriptionTabBtn.Foreground = overview
            ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    private void OverviewTab_Click(object sender, RoutedEventArgs e) => SelectTab(true);
    private void DescriptionTab_Click(object sender, RoutedEventArgs e) => SelectTab(false);

    // ── Gallery ─────────────────────────────────────────

    private void BuildGallery(ModMarketMod mod)
    {
        GalleryPanel.Children.Clear();
        var images = mod.Images;
        if (images is not { Count: > 0 }) return;
        foreach (var url in images)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;
            var border = new Border
            {
                Width = 280, Height = 160,
                CornerRadius = new CornerRadius(8)
            };
            border.Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url)),
                Stretch = Stretch.UniformToFill,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            GalleryPanel.Children.Add(border);
        }
    }

    // ── Drive links ─────────────────────────────────────

    private void BuildDriveLinks(ModMarketMod mod)
    {
        try
        {
            var raw = mod.DriveLinks;
            if (raw is not { ValueKind: JsonValueKind.Array })
            {
                FilesList.Visibility = Visibility.Collapsed;
                return;
            }
            var links = raw.Value.Deserialize<List<DriveLinkEntry>>();
            if (links is not { Count: > 0 })
            {
                FilesList.Visibility = Visibility.Collapsed;
                return;
            }
            var displayLinks = links.Select(l => new { l.Name, l.Url }).ToList();
            FilesList.ItemsSource = displayLinks;
            FilesList.Visibility = Visibility.Visible;
        }
        catch
        {
            FilesList.Visibility = Visibility.Collapsed;
        }
    }

    // ── Event handlers ──────────────────────────────────

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMod?.DownloadUrl is not null)
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(_currentMod.DownloadUrl));
    }
}
