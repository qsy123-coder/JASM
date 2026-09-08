using System.Text.Json;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.Services;
using GIMI_ModManager.WinUI.Services.Notifications;
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

    /// <summary>用户请求关闭面板(点关闭按钮/空白区)。由页面重置 ViewModel.SelectedMod,经 PropertyChanged 链路调用 Hide()</summary>
    public event EventHandler? Closed;

    public ModDetailPanel()
    {
        InitializeComponent();
        SlideOutStoryboard.Completed += (_, _) =>
        {
            // 防御:WinUI 的 Stop() 也可能触发 Completed;若已被 Show() 打断则不能 Collapse
            if (!_isClosing)
            {
                _logger.Information("[Panel] SlideOut完成但已被打断(非关闭中),忽略");
                return;
            }
            _isClosing = false;
            _logger.Information("[Panel] SlideOut完成 → 面板Collapsed");
            PanelRoot.Visibility = Visibility.Collapsed;
            _currentMod = null;
        };
        _logger.Information("ModDetailPanel constructed");
    }

    public void Show(ModMarketMod mod)
    {
        _logger.Information($"[Panel] Show调用: 当前={_currentMod?.Title ?? "null"}, 新={mod.Title}, 可见={PanelRoot.Visibility}");

        if (_currentMod == mod && PanelRoot.Visibility == Visibility.Visible)
        {
            _logger.Information("[Panel] 同一mod且面板可见 → 重新滑入");
            _isClosing = false;
            SlideOutStoryboard.Stop();
            SlideInStoryboard.Stop();
            DrawerBorder.RenderTransform = new TranslateTransform { X = 420 };
            SlideInStoryboard.Begin();
            _ = FillMissingDetailAsync(mod);
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
        _ = FillMissingDetailAsync(mod);
    }

    public void Hide()
    {
        _logger.Information($"[Panel] Hide调用: 可见={PanelRoot.Visibility}");
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

            // 图片加载动画:加载中显示进度圈,加载完成/失败后隐藏。
            // 构造 BitmapImage 后立即订阅事件再赋 UriSource,避免缓存命中时事件先于订阅触发导致转圈不停。
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            var ring = new ProgressRing
            {
                IsActive = true,
                Width = 24,
                Height = 24,
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x78, 0xD4))
            };
            bmp.ImageOpened += (_, _) => { ring.IsActive = false; ring.Visibility = Visibility.Collapsed; };
            bmp.ImageFailed += (_, _) => { ring.IsActive = false; ring.Visibility = Visibility.Collapsed; };
            bmp.UriSource = new Uri(url);

            var img = new Image
            {
                Source = bmp,
                Stretch = Stretch.Uniform,
                MaxWidth = 320,
                MaxHeight = 200
            };

            var container = new Grid { IsTapEnabled = true };
            container.Children.Add(img);
            container.Children.Add(ring);

            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Child = container,
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

    /// <summary>补拉详情递增序号，用于丢弃过期(已切换/已关闭)的异步结果。</summary>
    private int _detailFetchSeq;

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
                    // 顶部引导：先提示操作方式，再列出网盘卡片
                    DrivePanel.Children.Add(BuildDriveHint());
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
            Text = "点击复制链接",
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
            Margin = new Thickness(8, 0, 0, 0)
        };
        // 默认 WinUI 按钮在 hover/pressed 时会把行内 Background 换成浅色主题刷,
        // 白色复制图标在浅色卡片上会几乎不可见(看起来像"消失")。覆盖这两支为深蓝,
        // 让悬停/按下时背景不变淡,图标始终清晰。
        btn.Resources["ButtonBackgroundPointerOver"] =
            new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x5A, 0xA8));
        btn.Resources["ButtonBackgroundPressed"] =
            new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x00, 0x4E, 0x8F));
        // 点击 = 复制链接(而非跳转浏览器),引导用户去对应网盘转存下载
        btn.Click += (s, e) => CopyNetdiskLink(url, name);
        btn.Content = new FontIcon
        {
            FontSize = 14,
            Glyph = "",
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

    /// <summary>网盘列表顶部的操作提示，向用户说明「复制链接→去网盘转存」的流程。</summary>
    private FrameworkElement BuildDriveHint()
    {
        return new TextBlock
        {
            Text = "提示：点击下方网盘卡片即可复制链接，请打开对应的网盘客户端转存并下载（网页端通常限速且需登录，推荐用客户端）。",
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            TextWrapping = TextWrapping.Wrap
        };
    }

    /// <summary>复制网盘链接到剪贴板，并引导用户打开对应网盘转存下载（不再直接跳转浏览器）。</summary>
    private void CopyNetdiskLink(string url, string platform)
    {
        try
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to copy netdisk link to clipboard");
            App.GetService<NotificationManager>()?.ShowNotification("复制失败", "无法复制网盘链接，请手动复制。",
                TimeSpan.FromSeconds(4));
            return;
        }

        _logger.Information("Copied {Platform} netdisk link to clipboard", platform);
        App.GetService<NotificationManager>()?.ShowNotification("网盘链接已复制",
            $"已复制「{platform}」网盘链接，请打开{platform}客户端粘贴链接后转存下载。网页端往往限速且需登录，不推荐使用网页端。",
            TimeSpan.FromSeconds(6));
    }

    // ── 补拉详情(description) ───────────────────────────

    /// <summary>
    /// 列表接口只拉网格字段,description 是唯一的大字段被省略。点开详情时按 id 补拉一次并回填,
    /// 若期间已切换到别的 mod 或面板已关闭,则丢弃过期结果。
    /// </summary>
    private async Task FillMissingDetailAsync(ModMarketMod mod)
    {
        // 已补过(例如同一 mod 再次点击)不再重复请求
        if (!string.IsNullOrEmpty(mod.Description)) return;
        var seq = ++_detailFetchSeq;
        try
        {
            var full = await App.GetService<ModMarketService>().GetModByIdAsync(mod.Id);
            if (full is null || seq != _detailFetchSeq || !ReferenceEquals(_currentMod, mod))
                return; // 已切换/已关闭本次面板,丢弃过期结果
            DescriptionText.Text = full.Description;
            mod.Description = full.Description; // 供后续再次点击时直接复用
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load full mod detail for '{Title}'", mod.Title);
            if (ReferenceEquals(_currentMod, mod))
                DescriptionText.Text = "（描述加载失败，请重试）";
        }
    }

    // ── Event handlers ──────────────────────────────────

    private void TapCloseArea_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _logger.Information("[Panel] 点击空白关闭区 → 触发Closed事件");
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _logger.Information("[Panel] 点击关闭按钮 → 触发Closed事件");
        Closed?.Invoke(this, EventArgs.Empty);
    }

}
