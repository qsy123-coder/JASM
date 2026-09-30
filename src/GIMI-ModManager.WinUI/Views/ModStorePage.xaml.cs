using System.Collections.Specialized;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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

        // 懒加载:无预览图直接收起加载圈;有图的入队,滚到视口才设 Source
        var thumb = FindThumbImage(card);
        if (mod.PreviewImageUrl is null)
        {
            // 没有预览图的卡片永不触发 ImageOpened/ImageFailed,得直接隐藏,否则转圈不停
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
            if (e.Ring is not null)
            {
                e.Ring.Visibility = Visibility.Visible;
                e.Ring.IsActive = true;
            }
            e.Thumb.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(e.Url!));
            _pendingImages.RemoveAt(i);
        }
    }

    private sealed class PendingCard
    {
        public FrameworkElement Card = null!;
        public Image? Thumb;
        public ProgressRing? Ring;
        public string? Url;
    }

    private void CardImage_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is Image { Parent: Grid grid }) HideCardLoadingRing(grid);
    }

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
}