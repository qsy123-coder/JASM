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
        // 懒加载:无预览图直接隐藏加载圈;有图的挂到待加载队列,滚动到视口才设 Source
        var thumb = FindThumbImage(card);
        if (mod.PreviewImageUrl is null)
        {
            // 没有预览图的卡片永不触发 ImageOpened/ImageFailed，需要直接隐藏加载圈，避免转圈不停
            HideCardLoadingRing(card);
        }
        else if (thumb is not null)
        {
            _pendingImages.Add(new PendingCard
            {
                Card = card,
                Thumb = thumb,
                Ring = FindProgressRing(card),
                Url = mod.PreviewImageUrl
            });
        }
        ModCardsPanel.Children.Add(card);
    }

    /// <summary>卡片模板根 Border → 顶层 Grid(Height=340) 的直接子级里唯一的 Image 即缩略图。</summary>
    private static Image? FindThumbImage(FrameworkElement card)
    {
        if (card is not Border { Child: Grid top }) return null;
        foreach (var child in top.Children)
            if (child is Image img) return img;
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
