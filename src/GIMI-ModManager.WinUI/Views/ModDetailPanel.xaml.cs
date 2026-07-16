using System.Text.Json;
using GIMI_ModManager.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
            DrawerBorder.RenderTransform = new TranslateTransform { X = 360 };
            SlideInStoryboard.Begin();
            return;
        }

        _currentMod = mod;
        DataContext = mod;
        BuildGallery(mod);
        BuildDriveLinks(mod);
        SelectTab(overview: true);

        // 防御:若关闭动画进行中被打断,停止它并清除关闭标记,避免其 Completed 稍后触发把面板错误 Collapse
        _isClosing = false;
        SlideOutStoryboard.Stop();
        DrawerBorder.RenderTransform = new TranslateTransform { X = 360 };
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

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMod?.DownloadUrl is not null)
            _ = Windows.System.Launcher.LaunchUriAsync(new Uri(_currentMod.DownloadUrl));
    }
}
