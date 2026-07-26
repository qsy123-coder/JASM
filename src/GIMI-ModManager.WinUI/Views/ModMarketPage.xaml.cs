using System.Collections.Specialized;
using GIMI_ModManager.WinUI.Models;
using GIMI_ModManager.WinUI.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace GIMI_ModManager.WinUI.Views;

public sealed partial class ModMarketPage : Page
{
    public ModMarketViewModel ViewModel { get; }

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
        ModCardsPanel.Children.Add(card);
    }

    private void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
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
