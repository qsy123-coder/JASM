using System.Collections.Specialized;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace GIMI_ModManager.WinUI.Views;

public sealed partial class ModMarketPage : Page
{
    public ModMarketViewModel ViewModel { get; }

    // 懒加载:已加卡但尚未设图片源的卡片。滚到视口附近才设置 Source(COS 拉图),
    // 离屏卡片的进度圈默认折叠、不空转。
    private readonly List<PendingCard> _pendingImages = [];

    public ModMarketPage()
    {
        ViewModel = App.GetService<ModMarketViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        ViewModel.Mods.CollectionChanged += OnModsCollectionChanged;
        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ModMarketViewModel.SelectedMod))
                OnSelectedModChanged();
        };
        // 关闭请求经 ViewModel 重置 SelectedMod=null,保证下次点同一 mod 时 PropertyChanged 能触发
        DetailPanel.Closed += (_, _) => ViewModel.CloseDetailPanelCommand.Execute(null);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.Tick += (s, args) =>
        {
            if (ViewModel.Categories.Count > 0 && CategoryListView.SelectedIndex < 0)
            {
                CategoryListView.SelectedIndex = 0;
                timer.Stop();
            }
        };
        timer.Start();
    }

    private void OnSelectedModChanged()
    {
        if (ViewModel.SelectedMod is { } mod)
            DetailPanel.Show(mod);
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
                    foreach (var mod in ViewModel.Mods) AddCard((ModMarketMod)mod);
                    break;
                case NotifyCollectionChangedAction.Add:
                    foreach (var item in e.NewItems!) AddCard((ModMarketMod)item);
                    break;
            }

            // Force layout so ScrollViewer picks up new extent
            ModCardsPanel.InvalidateMeasure();
            ModCardsPanel.UpdateLayout();

            // 懒加载:先加载当前视口内的图;再排一帧兜底(首次布局完成后 ViewportHeight 才可读)
            LazyLoadVisibleImages();
            DispatcherQueue.TryEnqueue(() => LazyLoadVisibleImages());

            // If content fits viewport, auto-load more
            if (CardScrollViewer.ScrollableHeight <= 0
                && ViewModel.HasMorePages && !ViewModel.IsLoading
                && ModCardsPanel.Children.Count > 0)
            {
                _ = ViewModel.LoadMoreCommand.ExecuteAsync(null);
            }
        });
    }

    private void AddCard(ModMarketMod mod)
    {
        var tpl = Resources["ModCardTemplate"] as DataTemplate;
        if (tpl is null) return;
        var card = tpl.LoadContent() as FrameworkElement;
        if (card is null) return;
        card.DataContext = mod;
        // Wire click to show detail panel
        card.Tapped += (s, e) =>
        {
            ViewModel.OpenModDetailCommand.Execute(mod);
        };
        card.IsTapEnabled = true;

        // 模板里的尺寸是照商店那份参考图写的设计值,而卡片宽度是容器均分来的、会随窗口变 ——
        // 整卡按槽位宽等比缩放,比例才在任何窗口宽度下都与商店一致。
        if (CardScaler.Collect(card) is { } scaler)
            card.SizeChanged += (_, e) => scaler.Apply(e.NewSize.Width);

        // 懒加载:无预览图直接隐藏加载圈;有图的挂到待加载队列,滚动到视口才设 Source
        var thumb = FindByName<Image>(card, "CardImage");
        if (mod.PreviewImageUrl is null)
        {
            // 没有预览图的卡片永不触发 ImageOpened/ImageFailed，需要直接隐藏加载圈，避免转圈不停
            if (FindByName<ProgressRing>(card, "CardLoadingRing") is { } idleRing)
            {
                idleRing.IsActive = false;
                idleRing.Visibility = Visibility.Collapsed;
            }
        }
        else if (thumb is not null)
        {
            _pendingImages.Add(new PendingCard
            {
                Card = card,
                Thumb = thumb,
                // 按名字在**逻辑树**里找(此刻卡片还没进可视树,VisualTreeHelper 认不到)
                Ring = FindByName<ProgressRing>(card, "CardLoadingRing"),
                Url = mod.PreviewImageUrl
            });
        }
        ModCardsPanel.Children.Add(card);
    }

    /// <summary>设计稿的列距 = 卡片外框 304 + 左右间距 26。槽位宽除以它就是缩放系数 s。</summary>
    private const double DesignPitchWidth = 330;

    /// <summary>写回尺寸时差值低于这个数就当没变 —— 免得自己再触发一场布局,来回没完。</summary>
    private const double MetricEpsilon = 0.01;

    /// <summary>
    /// 把一张卡片的「设计值」整体乘上缩放系数 s 写回去。
    ///
    /// <para>
    /// 模板里的尺寸是照参考图逐像素量出来的设计值(卡片外框 304 宽那一套),而卡片实际宽度是
    /// <c>WrapGridPanel</c> 均分槽位得来的、会随窗口变,所以字号 / 内边距 / 外边距 / 行高 /
    /// 圆角要**一起**缩放 —— 只缩封面高度的话,卡片比例会随窗口漂移。
    /// </para>
    ///
    /// <para>
    /// 这份实现与 <c>ModStorePage.CardScaler</c> **刻意重复**(照该仓库既有的做法:两份都注释清楚、
    /// 等第三处调用者出现再抽公共件)。改一处记得改另一处 —— 两个页面的卡片版式必须完全一致,
    /// 否则「市场严格用商店的布局」这条约定就断了。
    /// </para>
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
        /// 只收「尺寸类」属性(字号 / 间距 / 内外边距 / 宽高 / 圆角 / 定高行);边框粗细、字重、
        /// MaxLines、颜色一律不缩放 —— 边框要一直是发丝级的 1px,行数上限也不是尺寸。
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
    /// 那一刻可视化树是空的。
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

    private static ProgressRing? FindProgressRing(DependencyObject root)
    {
        if (root is ProgressRing ring) return ring;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindProgressRing(VisualTreeHelper.GetChild(root, i));
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>把滚动到视口(含少量预加载缓冲)内的卡片图片源设置好，并从待加载队列移除。</summary>
    private void LazyLoadVisibleImages()
    {
        if (_pendingImages.Count == 0) return;
        var h = CardScrollViewer.ViewportHeight;
        if (h <= 0) return; // 尚未完成布局,下一次再触发
        // 预加载缓冲:视口上下各 0.5 屏,滚到时已就绪,避免白板闪烁
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
            if (e.Ring is not null)
            {
                e.Ring.Visibility = Visibility.Visible;
                e.Ring.IsActive = true;
            }
            e.Thumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(e.Url));
            _pendingImages.RemoveAt(i);
        }
    }

    /// <summary>待懒加载的卡片状态。</summary>
    private sealed class PendingCard
    {
        public FrameworkElement Card = null!;
        public Image? Thumb;
        public ProgressRing? Ring;
        public string? Url;
    }

    /// <summary>图片加载成功：隐藏卡片加载圈</summary>
    private void CardImage_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is Image { Parent: Grid grid }) HideCardLoadingRing(grid);
    }

    /// <summary>图片加载失败：隐藏卡片加载圈（占位灰底仍然可见）</summary>
    private void CardImage_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is Image { Parent: Grid grid }) HideCardLoadingRing(grid);
    }

    private static void HideCardLoadingRing(DependencyObject root)
    {
        if (root is ProgressRing ring)
        {
            ring.IsActive = false;
            ring.Visibility = Visibility.Collapsed;
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            HideCardLoadingRing(VisualTreeHelper.GetChild(root, i));
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

    private void CategoryListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryListView.SelectedItem is ModMarketCategory cat)
            ViewModel.SelectedCategory = cat;
    }

    /// <summary>点击 NSFW 模糊遮罩后移除遮罩，显示原图</summary>
    private void NsfwOverlay_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is Border overlay)
            overlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }
}
