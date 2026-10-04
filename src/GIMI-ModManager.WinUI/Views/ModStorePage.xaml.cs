using System.Collections.Specialized;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace GIMI_ModManager.WinUI.Views;

/// <summary>
/// 商店页的卡片是**在代码里手工搭出来**的（不是 ItemsControl + 虚拟化），与 Mod 市场同一套做法：
/// 卡片要挂懒加载图片队列、要按视口位置决定何时设 Source，用模板 + 虚拟化反而更绕。
/// </summary>
public sealed partial class ModStorePage : Page
{
    public ModStoreViewModel ViewModel { get; }

    /// <summary>已加卡但尚未设图片源的卡片；滚到视口附近才设置 Source，离屏的进度圈折叠、不空转。</summary>
    private readonly List<PendingCard> _pendingImages = [];

    public ModStorePage()
    {
        ViewModel = App.GetService<ModStoreViewModel>();
        InitializeComponent();
        ViewModel.Mods.CollectionChanged += OnModsCollectionChanged;

        // 抽屉的开合由 DetailItem 这一条属性驱动(非 null = 打开),与市场侧的 SelectedMod 同一套。
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModStoreViewModel.DetailItem))
                OnDetailItemChanged();
        };

        // 关抽屉走 ViewModel:置回 null 才会再触发一次 PropertyChanged,否则同一 mod 再点一次不弹。
        DetailPanel.Closed += (_, _) => ViewModel.CloseDetailPanelCommand.Execute(null);

        // 重试也走 ViewModel —— 面板没有服务,重新取数要靠它。
        DetailPanel.RetryRequested += (_, _) => ViewModel.RetryModDetailCommand.Execute(null);

        // 「下载选中文件」同样转给 ViewModel：由它把「哪条 mod + 哪个文件」拼成入队请求
        // （抽屉里那个文件对象只知道自己的显示字段，拼请求要用 Core 那份原始记录）。
        DetailPanel.DownloadRequested += (_, file) => ViewModel.DownloadSelectedFileCommand.Execute(file);
    }

    private void OnDetailItemChanged()
    {
        if (ViewModel.DetailItem is { } item)
            DetailPanel.Show(item);
        else
            DetailPanel.Hide();
    }

    private void OnModsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Reset:
                case NotifyCollectionChangedAction.Remove:
                    _pendingImages.Clear();
                    ModCardsPanel.Children.Clear();
                    if (e.Action == NotifyCollectionChangedAction.Reset) break;
                    foreach (var mod in ViewModel.Mods) AddCard((ModStoreItem)mod);
                    break;
                case NotifyCollectionChangedAction.Add:
                    foreach (var item in e.NewItems!) AddCard((ModStoreItem)item);
                    break;
            }

            // 让 ScrollViewer 立刻看到新的内容高度
            ModCardsPanel.InvalidateMeasure();
            ModCardsPanel.UpdateLayout();

            // 懒加载:先处理当前视口,再排一帧兜底(首次布局完成后 ViewportHeight 才可读)
            LazyLoadVisibleImages();
            DispatcherQueue.TryEnqueue(() => LazyLoadVisibleImages());

            // 内容不足一屏时自动续下一页,否则用户没有东西可滚、也就永远触发不了加载更多
            if (CardScrollViewer.ScrollableHeight <= 0
                && ViewModel.HasMorePages && !ViewModel.IsLoading
                && ModCardsPanel.Children.Count > 0)
            {
                _ = ViewModel.LoadMoreCommand.ExecuteAsync(null);
            }
        });
    }

    private void AddCard(ModStoreItem mod)
    {
        var tpl = Resources["ModStoreCardTemplate"] as DataTemplate;
        if (tpl is null) return;
        var card = tpl.LoadContent() as FrameworkElement;
        if (card is null) return;
        card.DataContext = mod;

        // 点卡片开详情抽屉。整张卡都能点(图片和叠在上面的文字都算)——
        // 卡片里没有任何可交互控件,所以不需要再判「点到的是不是按钮」。
        card.IsTapEnabled = true;
        card.Tapped += (_, _) => ViewModel.OpenModDetailCommand.Execute(mod);

        var thumb = FindByName<Image>(card, "CardImage");
        var ring = FindByName<ProgressRing>(card, "CardLoadingRing");
        var avatarBrush = FindByName<Ellipse>(card, "AuthorAvatar")?.Fill as ImageBrush;

        // 模板里的尺寸是照参考图写的设计值,而卡片宽度是容器均分来的、会随窗口变 —— 整卡按 s 等比缩放。
        if (CardScaler.Collect(card) is { } scaler)
            card.SizeChanged += (_, e) => scaler.Apply(e.NewSize.Width);

        var avatarUrl = mod.AuthorAvatarUrl;
        if (avatarBrush is not null && avatarUrl is not null)
        {
            // 头像加载失败时清掉笔刷 —— 底下的底色圆就露出来了,不需要额外的可见性切换。
            avatarBrush.ImageFailed += (_, _) => avatarBrush.ImageSource = null;
        }

        // 懒加载:无预览图直接收起加载圈;有图的入队,滚到视口才设 Source
        if (mod.PreviewImageUrl is null || thumb is null)
        {
            // 没有预览图的卡片永不触发 ImageOpened/ImageFailed,得直接隐藏,否则转圈不停
            HideLoadingRing(ring);
        }

        // 头像与缩略图走同一个队列:一次滚屏冒出几十个请求,头像再小也不合适
        if (mod.PreviewImageUrl is not null || avatarUrl is not null)
        {
            _pendingImages.Add(new PendingCard
            {
                Card = card,
                Thumb = thumb,
                Ring = ring,
                Url = mod.PreviewImageUrl,
                AvatarBrush = avatarBrush,
                AvatarUrl = avatarUrl
            });
        }

        ModCardsPanel.Children.Add(card);
    }

    // ── 卡片版式:设计值(照参考图实测,见 ModStorePage.xaml 的模板注释)与整卡等比缩放 ──

    /// <summary>设计稿的列距 = 卡片外框 304 + 左右间距 26。槽位宽除以它就是缩放系数 s。</summary>
    private const double DesignPitchWidth = 330;

    /// <summary>写回尺寸时差值低于这个数就当没变 —— 免得自己再触发一场布局,来回没完。</summary>
    private const double MetricEpsilon = 0.01;

    /// <summary>
    /// 把一张卡片的「设计值」整体乘上缩放系数 s 写回去。
    ///
    /// 模板里的尺寸是照参考图逐像素量出来的设计值(卡片外框 304 宽那一套),而卡片实际宽度是
    /// <c>WrapGridPanel</c> 均分槽位得来的、会随窗口变,所以字号 / 内边距 / 外边距 /
    /// 圆角 / 头像 / 图标要**一起**缩放 —— 只缩封面高度的话,卡片比例会随窗口漂移,就不是「照参考图」了。
    ///
    /// 设计值只在 <see cref="Collect"/> 时读一次当基准,之后每次写「基准 × s」:
    /// 拿当前值去乘会一次比一次小。
    /// </summary>
    private sealed class CardScaler
    {
        private readonly Border _root;
        private readonly List<Metric> _metrics;

        /// <summary>
        /// 上一次写进去的左右外边距之和。
        ///
        /// 缩放系数要用「槽位宽」算,而 <c>SizeChanged</c> 给的宽度是 <c>ActualWidth</c> —— **不含 Margin**;
        /// 卡片外边距本身又在缩放集合里,拿实宽直接算就会和「写回后实宽又变了」互相追。
        /// 用上一次写进去的值把槽位宽反推回来(槽位宽 = 实宽 + 左右外边距),等式一次就成立,不再迭代。
        /// </summary>
        private double _appliedMarginH;

        private CardScaler(Border root, List<Metric> metrics)
        {
            _root = root;
            _metrics = metrics;
            _appliedMarginH = root.Margin.Left + root.Margin.Right; // 首帧还没写过,就是 XAML 里的设计值
        }

        /// <summary>收集一张卡片的设计值。卡片根不是 Border(模板被改过)时返回 null,调用方跳过。</summary>
        public static CardScaler? Collect(FrameworkElement card)
        {
            if (card is not Border root) return null;

            var metrics = new List<Metric>();
            Walk(card, metrics);
            return new CardScaler(root, metrics);
        }

        /// <summary>按卡片实宽重写整张卡。</summary>
        public void Apply(double cardWidth)
        {
            var scale = (cardWidth + _appliedMarginH) / DesignPitchWidth;
            foreach (var metric in _metrics)
                metric.Apply(scale);

            _appliedMarginH = _root.Margin.Left + _root.Margin.Right;
        }

        /// <summary>
        /// 沿**逻辑树**走一遍卡片,把要跟着缩放的尺寸登记下来。
        ///
        /// 只收「尺寸类」属性(字号 / 间距 / 内外边距 / 宽高 / 圆角);边框粗细、字重、MaxLines、颜色
        /// 一律不缩放 —— 边框要一直是发丝级的 1px,行数上限也不是尺寸。
        /// </summary>
        private static void Walk(DependencyObject node, List<Metric> metrics)
        {
            if (node is FrameworkElement element)
            {
                Track(metrics, () => element.Width, v => element.Width = v);
                Track(metrics, () => element.Height, v => element.Height = v);
                Track(metrics, () => element.MinWidth, v => element.MinWidth = v);
                Track(metrics, () => element.MinHeight, v => element.MinHeight = v);
                TrackThickness(metrics, () => element.Margin, v => element.Margin = v);
            }

            switch (node)
            {
                case TextBlock text:
                    Track(metrics, () => text.FontSize, v => text.FontSize = v);
                    // 写死的行高也要跟着缩:不然字号缩了、行高还留在设计值上,两行就撑破了预定高度。
                    Track(metrics, () => text.LineHeight, v => text.LineHeight = v);
                    break;
                case FontIcon icon:
                    Track(metrics, () => icon.FontSize, v => icon.FontSize = v);
                    break;
                case StackPanel panel:
                    Track(metrics, () => panel.Spacing, v => panel.Spacing = v);
                    TrackThickness(metrics, () => panel.Padding, v => panel.Padding = v);
                    break;
                case Grid grid:
                    Track(metrics, () => grid.RowSpacing, v => grid.RowSpacing = v);
                    Track(metrics, () => grid.ColumnSpacing, v => grid.ColumnSpacing = v);
                    TrackThickness(metrics, () => grid.Padding, v => grid.Padding = v);
                    // 定高行也要跟着缩 —— RowDefinition 不是 FrameworkElement,遍历认不到它,得单独登记
                    foreach (var row in grid.RowDefinitions)
                        TrackRowHeight(metrics, row);
                    break;
                case Border border:
                    TrackThickness(metrics, () => border.Padding, v => border.Padding = v);
                    TrackCornerRadius(metrics, border);
                    break;
            }

            foreach (var child in LogicalChildren(node))
                Walk(child, metrics);
        }

        /// <summary>登记一个标量设计值(字号 / 间距 / 宽高)。没显式设过(NaN)或非正的不登记。</summary>
        private static void Track(List<Metric> metrics, Func<double> read, Action<double> write)
        {
            var design = read();
            if (double.IsNaN(design) || design <= 0) return;

            metrics.Add(new Metric(s =>
            {
                var value = design * s;
                if (Math.Abs(read() - value) > MetricEpsilon) write(value);
            }));
        }

        /// <summary>登记一个 Thickness(内 / 外边距):四个分量一起乘 s。</summary>
        private static void TrackThickness(List<Metric> metrics, Func<Thickness> read, Action<Thickness> write)
        {
            var design = read();
            if (design is { Left: 0, Top: 0, Right: 0, Bottom: 0 }) return;

            metrics.Add(new Metric(s =>
            {
                var scaled = new Thickness(design.Left * s, design.Top * s, design.Right * s, design.Bottom * s);
                if (Math.Abs(read().Left - scaled.Left) > MetricEpsilon) write(scaled);
            }));
        }

        /// <summary>登记一个 Grid 的定高行。Auto / Star 行不登记(它们本来就该由内容或剩余空间定)。</summary>
        private static void TrackRowHeight(List<Metric> metrics, RowDefinition row)
        {
            var design = row.Height;
            if (!design.IsAbsolute || design.Value <= 0) return;

            metrics.Add(new Metric(s =>
            {
                var value = design.Value * s;
                if (Math.Abs(row.Height.Value - value) > MetricEpsilon) row.Height = new GridLength(value);
            }));
        }

        /// <summary>登记一个 CornerRadius:四个角一起乘 s。</summary>
        private static void TrackCornerRadius(List<Metric> metrics, Border border)
        {
            var design = border.CornerRadius;
            if (design is { TopLeft: 0, TopRight: 0, BottomRight: 0, BottomLeft: 0 }) return;

            metrics.Add(new Metric(s =>
            {
                var scaled = new CornerRadius(
                    design.TopLeft * s, design.TopRight * s, design.BottomRight * s, design.BottomLeft * s);
                if (Math.Abs(border.CornerRadius.TopLeft - scaled.TopLeft) > MetricEpsilon)
                    border.CornerRadius = scaled;
            }));
        }
    }

    /// <summary>一条登记项:一个设计值 + 一个「按缩放系数 s 重写自己」的动作(动作自带差值守卫)。</summary>
    private sealed class Metric(Action<double> applyByScale)
    {
        public void Apply(double scale) => applyByScale(scale);
    }

    /// <summary>
    /// 逻辑树里的直接子元素 —— 只认 <c>Panel.Children</c> / <c>Border.Child</c> /
    /// <c>ContentControl.Content</c> 三类容器,卡片模板用到的就是这三类。
    ///
    /// 为什么不走 <c>VisualTreeHelper</c>:卡片是 <c>LoadContent()</c> 刚实例化出来的,还没进可视树,
    /// 那一刻可视化树是空的 —— 以前按「根 Border 的 Grid 里第一个 Image」找缩略图就是这么写的,
    /// 换成横屏版式后会静默返回 null(缩略图全不加载)。逻辑树在解析时就建好了,不依赖布局。
    /// </summary>
    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject node)
    {
        switch (node)
        {
            case Panel panel:
                foreach (var child in panel.Children)
                    yield return child;
                break;
            case Border { Child: { } borderChild }:
                yield return borderChild;
                break;
            case ContentControl { Content: DependencyObject content }:
                yield return content;
                break;
        }
    }

    /// <summary>在**逻辑树**里按名字找模板里的元素(那些 x:Name),理由见 <see cref="LogicalChildren"/>。</summary>
    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T hit && hit.Name == name)
            return hit;

        foreach (var child in LogicalChildren(root))
            if (FindByName<T>(child, name) is { } found)
                return found;

        return null;
    }

    /// <summary>把滚到视口(含上下各半屏预加载缓冲)内的卡片图片源设置好，并从待加载队列移除。</summary>
    private void LazyLoadVisibleImages()
    {
        if (_pendingImages.Count == 0) return;
        var h = CardScrollViewer.ViewportHeight;
        if (h <= 0) return; // 尚未完成布局,等下一次触发
        var overscan = h * 0.5;
        var top = -overscan;
        var bottom = h + overscan;
        for (var i = _pendingImages.Count - 1; i >= 0; i--)
        {
            var e = _pendingImages[i];
            double y;
            try
            {
                y = e.Card.TransformToVisual(CardScrollViewer).TransformPoint(new Point(0, 0)).Y;
            }
            catch
            {
                continue;
            }
            if (y < top || y > bottom) continue;

            if (e.Thumb is not null && e.Url is not null)
            {
                if (e.Ring is not null)
                {
                    e.Ring.Visibility = Visibility.Visible;
                    e.Ring.IsActive = true;
                }
                e.Thumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(e.Url));
            }

            if (e.AvatarBrush is not null && e.AvatarUrl is not null)
                e.AvatarBrush.ImageSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(e.AvatarUrl);

            _pendingImages.RemoveAt(i);
        }
    }

    private sealed class PendingCard
    {
        public FrameworkElement Card = null!;
        public Image? Thumb;
        public ProgressRing? Ring;
        public string? Url;

        /// <summary>作者头像那层圆(用笔刷画,所以拿的是 Ellipse 的 Fill)。</summary>
        public ImageBrush? AvatarBrush;

        /// <summary>头像地址。缩略图用 string、头像用 Uri,是因为来源本来就不同(一个是 string 属性)。</summary>
        public Uri? AvatarUrl;
    }

    private void CardImage_Opened(object sender, RoutedEventArgs e) => HideRingForImage(sender);

    private void CardImage_Failed(object sender, ExceptionRoutedEventArgs e) => HideRingForImage(sender);

    /// <summary>
    /// 图片落地(成功或失败)就收掉同一张卡片上的加载圈。能按名字找是因为此刻卡片已经进了可视树,
    /// 而加载圈与图片同在图片区那一层(ThumbHost)。
    /// </summary>
    private static void HideRingForImage(object sender)
    {
        if (sender is Image { Parent: DependencyObject parent })
            HideLoadingRing(FindByName<ProgressRing>(parent, "CardLoadingRing"));
    }

    private static void HideLoadingRing(ProgressRing? ring)
    {
        if (ring is null) return;
        ring.IsActive = false;
        ring.Visibility = Visibility.Collapsed;
    }

    private void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        // 滚动过程中也要触发懒加载,让滚入视口的图片提前就绪
        LazyLoadVisibleImages();
        if (e.IsIntermediate) return;
        if (CardScrollViewer.VerticalOffset >= CardScrollViewer.ScrollableHeight - 200
            && !ViewModel.IsLoading && ViewModel.HasMorePages)
        {
            _ = ViewModel.LoadMoreCommand.ExecuteAsync(null);
        }
    }
}