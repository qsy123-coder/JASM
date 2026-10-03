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

        // 缩略图区的高度按卡片宽度算:横屏卡片的宽度是容器均分出来的,写死高度会在宽窗口下变成细长条。
        if (FindByName<Grid>(card, "ThumbHost") is { } thumbHost)
            ApplyThumbHeight(card, thumbHost);

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

    // 横屏卡片的图片区比例(16:9)。上下限兜住极端窗口:太扁看不出内容,太高会把信息块挤出去。
    private const double ThumbAspectRatio = 9d / 16d;
    private const double MinThumbHeight = 96;
    private const double MaxThumbHeight = 220;

    /// <summary>
    /// 图片区高度 = 卡片实际宽度 × 9/16。
    ///
    /// 宽度是容器均分出来的(一行 6 个),所以只能在布局之后量;卡片挂上 <c>SizeChanged</c>,
    /// 首帧落地与之后每次窗口缩放都会走到这里。写入前比对一下,免得设同值引发又一次布局。
    /// </summary>
    private static void ApplyThumbHeight(FrameworkElement card, FrameworkElement thumbHost)
    {
        card.SizeChanged += (_, e) =>
        {
            var height = Math.Clamp(e.NewSize.Width * ThumbAspectRatio, MinThumbHeight, MaxThumbHeight);
            if (Math.Abs(thumbHost.Height - height) > 0.5)
                thumbHost.Height = height;
        };
    }

    /// <summary>
    /// 在**逻辑树**里按名字找模板里的元素(那些 x:Name)。
    ///
    /// 为什么不用 <c>VisualTreeHelper</c>:卡片是 <c>LoadContent()</c> 刚实例化出来的,还没进可视树,
    /// 那一刻可视化树是空的 —— 以前按「根 Border 的 Grid 里第一个 Image」找缩略图就是这么写的,
    /// 换成横屏版式后会静默返回 null(缩略图全不加载)。逻辑树(Panel.Children / Border.Child /
    /// ContentControl.Content)在解析时就建好了,不依赖布局。
    /// </summary>
    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T hit && hit.Name == name)
            return hit;

        if (root is Panel panel)
        {
            foreach (var child in panel.Children)
                if (FindByName<T>(child, name) is { } found)
                    return found;
        }
        else if (root is Border { Child: { } borderChild })
        {
            if (FindByName<T>(borderChild, name) is { } fromBorder)
                return fromBorder;
        }
        else if (root is ContentControl { Content: DependencyObject content })
        {
            if (FindByName<T>(content, name) is { } fromContent)
                return fromContent;
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