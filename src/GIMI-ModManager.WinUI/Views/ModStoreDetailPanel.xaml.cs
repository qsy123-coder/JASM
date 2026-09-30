using System.ComponentModel;
using GIMI_ModManager.WinUI.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace GIMI_ModManager.WinUI.Views;

/// <summary>
/// 商店的详情抽屉：滑入/滑出/灯箱与市场侧的 <see cref="ModDetailPanel"/> 是同一套，
/// 但数据来源完全不同 —— 那份面板自己会去补详情（<c>ModMarketMod</c> 是个可变模型），
/// 这份**只负责显示** <see cref="ModStoreDetailItem"/>。
///
/// 为什么详情不在这里取：详情请求要处理「用户已经点了别的 mod / 已经把抽屉关了」的竞态，
/// 判断依据（当前是第几次请求、抽屉里是谁）都握在 <c>ModStoreViewModel</c> 手里。
/// 面板自己再取一次就会有两份互相打架的状态。所以：
/// <list type="bullet">
///   <item>打开：页面把 <c>DetailItem</c> 属性变化转成 <see cref="Show"/>；</item>
///   <item>补内容：ViewModel 改数据 → 这里靠 <c>PropertyChanged</c> 重建（见
///         <see cref="RefreshDynamicParts"/>）；</item>
///   <item>重试 / 关闭：面板只发事件，命令由页面转给 ViewModel。</item>
/// </list>
/// </summary>
public sealed partial class ModStoreDetailPanel : UserControl
{
    /// <summary>抽屉宽度。必须与 XAML 里两处动画的 From/To 一致，改宽度要一起改。</summary>
    private const double DrawerWidth = 460;

    private readonly ILogger _logger = Log.ForContext<ModStoreDetailPanel>();
    private ModStoreDetailItem? _current;
    private bool _isClosing;

    /// <summary>用户请求关闭（关闭按钮 / 点空白区）。页面把它转成 ViewModel 的关闭命令。</summary>
    public event EventHandler? Closed;

    /// <summary>点了「重试」。面板不认识商店服务，得请页面转给 ViewModel。</summary>
    public event EventHandler? RetryRequested;

    public ModStoreDetailPanel()
    {
        InitializeComponent();
        SlideOutStoryboard.Completed += (_, _) =>
        {
            // 防御：WinUI 的 Stop() 也可能触发 Completed；若已被 Show() 打断则不能 Collapse，
            // 否则会出现「刚滑进来就被收起来」。
            if (!_isClosing)
                return;

            _isClosing = false;
            PanelRoot.Visibility = Visibility.Collapsed;

            // 收起来就别再跟着数据变了 —— 重试成功后回来写界面会白忙一场。
            DetachCurrent();
            _current = null;
            DataContext = null;
        };
    }

    /// <summary>打开抽屉并显示 <paramref name="item"/>。同一条 mod 再点一次会重放滑入动画。</summary>
    public void Show(ModStoreDetailItem item)
    {
        // 换了一条 mod：先把上一条的订阅摘掉，否则两条的数据变化都会来刷这个面板。
        DetachCurrent();

        _current = item;
        DataContext = item;
        item.PropertyChanged += OnItemPropertyChanged;

        RefreshDynamicParts();
        SelectTab(overview: true);

        // 防御：关闭动画进行中被打断时，停掉它并清标记，免得它的 Completed 稍后把面板错误 Collapse。
        _isClosing = false;
        SlideOutStoryboard.Stop();
        SlideInStoryboard.Stop();
        DrawerBorder.RenderTransform = new TranslateTransform { X = DrawerWidth };
        PanelRoot.Visibility = Visibility.Visible;
        SlideInStoryboard.Begin();
    }

    /// <summary>开始收起动画。收完（<c>SlideOutStoryboard.Completed</c>）才真正 Collapse。</summary>
    public void Hide()
    {
        if (PanelRoot.Visibility != Visibility.Visible) return;
        _isClosing = true;
        SlideOutStoryboard.Begin();
    }

    // ── 数据变化 → 重建 ────────────────────────────────────

    /// <summary>
    /// 抽屉是「先开再补」的（见 <see cref="ModStoreDetailItem"/>）：图片列表和作者头像
    /// 一开始是空的，详情回来才有。这两个属性是用 C# 搭控件 / 设 <c>Source</c>，
    /// 绑不上去，只能监听变化重建。
    /// </summary>
    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ModStoreDetailItem.Images):
                BuildGallery();
                break;
            case nameof(ModStoreDetailItem.AuthorAvatarUrl):
                UpdateAuthorAvatar();
                break;
        }
    }

    /// <summary>把当前条目的「非绑定部分」重刷一遍（Show 时与数据变化时共用）。</summary>
    private void RefreshDynamicParts()
    {
        BuildGallery();
        UpdateAuthorAvatar();

        // 没有原页面地址就不摆这个按钮 —— 点了没反应的按钮比没有按钮更让人困惑。
        OpenInBrowserButton.Visibility = _current?.ModPageUrl is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void DetachCurrent()
    {
        if (_current is not null)
            _current.PropertyChanged -= OnItemPropertyChanged;
    }

    // ── 截图画廊 ───────────────────────────────────────────

    /// <summary>
    /// 把 <see cref="ModStoreDetailItem.Images"/>（URL 列表）搭成一排缩略图。
    ///
    /// 与市场侧同样的做法（代码搭控件而不是 ItemsControl + 模板）：要在加载中的那张图上面压一个
    /// 转圈，且 <c>BitmapImage</c> 必须先订阅 <c>ImageOpened</c>/<c>ImageFailed</c> 再赋
    /// <c>UriSource</c>，否则命中缓存时事件先于订阅触发，转圈就永远停不下来。
    /// </summary>
    private void BuildGallery()
    {
        GalleryPanel.Children.Clear();

        if (_current?.Images is not { Count: > 0 } images)
            return;

        foreach (var url in images)
        {
            var bitmap = new BitmapImage();
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
            bitmap.ImageOpened += (_, _) => { ring.IsActive = false; ring.Visibility = Visibility.Collapsed; };
            bitmap.ImageFailed += (_, _) => { ring.IsActive = false; ring.Visibility = Visibility.Collapsed; };
            bitmap.UriSource = url;

            var container = new Grid { IsTapEnabled = true };
            container.Children.Add(new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                MaxWidth = 320,
                MaxHeight = 200
            });
            container.Children.Add(ring);

            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                Child = container,
                IsTapEnabled = true
            };
            border.Tapped += (_, e) =>
            {
                ShowLightbox(url);
                e.Handled = true;
            };

            GalleryPanel.Children.Add(border);
        }
    }

    /// <summary>
    /// 作者头像。默认头像（<c>.../defaults/avatar.gif</c>）也是真图，照常显示 ——
    /// 没有头像地址时底下那层「圆圈 + 人形图标」就露出来顶着。
    /// </summary>
    private void UpdateAuthorAvatar()
    {
        AuthorAvatarImage.Source = _current?.AuthorAvatarUrl is { } url ? new BitmapImage(url) : null;
    }

    // ── 灯箱 ───────────────────────────────────────────────

    private void ShowLightbox(Uri imageUri)
    {
        LightboxImage.Source = new BitmapImage(imageUri);
        Lightbox.Visibility = Visibility.Visible;
    }

    private void LightboxBg_Tapped(object sender, TappedRoutedEventArgs e)
    {
        // 点图片本身不该关灯箱（只有点到背后的遮罩才算）。
        if (e.OriginalSource is Grid)
            Lightbox.Visibility = Visibility.Collapsed;
    }

    private void LightboxClose_Click(object sender, RoutedEventArgs e)
    {
        Lightbox.Visibility = Visibility.Collapsed;
    }

    // ── 标签页 ─────────────────────────────────────────────

    private void SelectTab(bool overview)
    {
        OverviewContent.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        DescriptionContent.Visibility = overview ? Visibility.Collapsed : Visibility.Visible;

        // 「选中」= 主色文字（与市场侧同一手法，不用 TabView：那套自带的下划线在抽屉里太重）。
        OverviewTabBtn.Foreground = overview
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        DescriptionTabBtn.Foreground = overview
            ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    }

    private void OverviewTab_Click(object sender, RoutedEventArgs e) => SelectTab(true);

    private void DescriptionTab_Click(object sender, RoutedEventArgs e) => SelectTab(false);

    // ── 事件 ───────────────────────────────────────────────

    private void TapCloseArea_Tapped(object sender, TappedRoutedEventArgs e) =>
        Closed?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Closed?.Invoke(this, EventArgs.Empty);

    private void RetryButton_Click(object sender, RoutedEventArgs e) =>
        RetryRequested?.Invoke(this, EventArgs.Empty);

    private void OpenInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.ModPageUrl is not { } url)
            return;

        _logger.Information("打开 GameBanana 原页面 | ModId: {ModId}", _current.GbModId);
        _ = Windows.System.Launcher.LaunchUriAsync(url);
    }
}