using System.Text.Json;
using GIMI_ModManager.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace GIMI_ModManager.WinUI.Views;

public sealed partial class ModDetailPanel : UserControl
{
    private readonly ILogger _logger = Log.ForContext<ModDetailPanel>();
    private ModMarketMod? _currentMod;
    private bool _isClosing;

    /// <summary>页面订阅后转发到底部调试面板</summary>
    public event Action<string>? DebugLog;

    /// <summary>用户请求关闭面板(点关闭按钮/空白区)。由页面重置 ViewModel.SelectedMod,经 PropertyChanged 链路调用 Hide()</summary>
    public event EventHandler? Closed;

    private void LogDebug(string msg)
    {
        _logger.Information(msg);
        DebugLog?.Invoke(msg);
    }

    public ModDetailPanel()
    {
        InitializeComponent();
        SlideOutStoryboard.Completed += (_, _) =>
        {
            // 防御:WinUI 的 Stop() 也可能触发 Completed;若已被 Show() 打断则不能 Collapse
            if (!_isClosing)
            {
                LogDebug("[Panel] SlideOut完成但已被打断(非关闭中),忽略");
                return;
            }
            _isClosing = false;
            LogDebug("[Panel] SlideOut完成 → 面板Collapsed");
            PanelRoot.Visibility = Visibility.Collapsed;
            _currentMod = null;
        };
        _logger.Information("ModDetailPanel constructed");
    }

    public void Show(ModMarketMod mod)
    {
        LogDebug($"[Panel] Show调用: 当前={_currentMod?.Title ?? "null"}, 新={mod.Title}, 可见={PanelRoot.Visibility}");

        if (_currentMod == mod && PanelRoot.Visibility == Visibility.Visible)
        {
            LogDebug("[Panel] 同一mod且面板可见 → 重新滑入");
            _isClosing = false;
            SlideOutStoryboard.Stop();
            SlideInStoryboard.Stop();
            DrawerBorder.RenderTransform = new TranslateTransform { X = 420 };
            SlideInStoryboard.Begin();
            return;
        }

        _currentMod = mod;
        DataContext = mod;
        BuildGallery(mod);
        BuildDownloadSection(mod);
        SelectTab(overview: true);

        // 防御:若关闭动画进行中被打断,停止它并清除关闭标记,避免其 Completed 稍后触发把面板错误 Collapse
        _isClosing = false;
        SlideOutStoryboard.Stop();
        DrawerBorder.RenderTransform = new TranslateTransform { X = 420 };
        PanelRoot.Visibility = Visibility.Visible;
        _logger.Information("PanelRoot visible={Vis}, opacity={Op}",
            PanelRoot.Visibility, PanelRoot.Opacity);
        SlideInStoryboard.Begin();
    }

    public void Hide()
    {
        LogDebug($"[Panel] Hide调用: 可见={PanelRoot.Visibility}");
        if (PanelRoot.Visibility != Visibility.Visible) return;
        _isClosing = true;
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
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(url));
            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                MaxWidth = 320,
                MaxHeight = 200
            };
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Child = img,
                IsTapEnabled = true
            };
            border.Tapped += (s, e) =>
            {
                ShowLightbox(new Uri(url));
                e.Handled = true;
            };
            GalleryPanel.Children.Add(border);
        }
    }

    // ── 灯箱预览 ──────────────────────────────────────

    private void ShowLightbox(Uri imageUri)
    {
        LightboxImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(imageUri);
        Lightbox.Visibility = Visibility.Visible;
    }

    private void LightboxBg_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is Grid)
            Lightbox.Visibility = Visibility.Collapsed;
    }

    private void LightboxClose_Click(object sender, RoutedEventArgs e)
    {
        Lightbox.Visibility = Visibility.Collapsed;
    }

    // ── Download section ────────────────────────────────

    private string? _directDownloadUrl;

    private void BuildDownloadSection(ModMarketMod mod)
    {
        // ── Direct download ──
        if (!string.IsNullOrWhiteSpace(mod.DownloadUrl))
        {
            _directDownloadUrl = mod.DownloadUrl;
            DirectDownloadCard.Visibility = Visibility.Visible;
            DirectDownloadLabel.Text = $"{mod.DownloadsCount} 次下载";
        }
        else
        {
            _directDownloadUrl = null;
            DirectDownloadCard.Visibility = Visibility.Collapsed;
        }

        // ── Drive links ──
        DrivePanel.Children.Clear();
        try
        {
            var raw = mod.DriveLinks;
            if (raw is { ValueKind: JsonValueKind.Array })
            {
                var links = raw.Value.Deserialize<List<DriveLinkEntry>>();
                if (links is { Count: > 0 })
                {
                    foreach (var link in links)
                        DrivePanel.Children.Add(BuildDriveCard(link.Name, link.Url));
                }
            }
        }
        catch { }
    }

    private void DirectDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_directDownloadUrl is not null)
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(_directDownloadUrl));
    }

    private FrameworkElement BuildDriveCard(string name, string url)
    {
        var border = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            CornerRadius = new CornerRadius(8),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0.5)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        leftStack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        leftStack.Children.Add(new TextBlock
        {
            Text = "点击下载",
            FontSize = 10,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        });
        Grid.SetColumn(leftStack, 0);

        var btn = new Button
        {
            Width = 36, Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Tag = url
        };
        btn.Click += DriveDownload_Click;
        btn.Content = new FontIcon
        {
            FontSize = 14,
            Glyph = "",
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(btn, 1);

        grid.Children.Add(leftStack);
        grid.Children.Add(btn);
        border.Child = grid;
        return border;
    }

    private void DriveDownload_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url })
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    // ── Event handlers ──────────────────────────────────

    private void TapCloseArea_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        LogDebug("[Panel] 点击空白关闭区 → 触发Closed事件");
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        LogDebug("[Panel] 点击关闭按钮 → 触发Closed事件");
        Closed?.Invoke(this, EventArgs.Empty);
    }

}
