using System.ComponentModel;
using GIMI_ModManager.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace GIMI_ModManager.WinUI.Views;

/// <summary>
/// 「下载管理」抽屉。滑入 / 滑出与商店详情抽屉是同一套，区别在**开合由它自己的 ViewModel 驱动**
/// （<see cref="ModDownloadManagerViewModel.IsOpen"/>）—— 打开它的按钮在页面上，
/// 而面板本身属于队列而不是某一个页面，让页面再插一道转发只会多一处可能忘记同步的状态。
/// </summary>
public sealed partial class ModDownloadPanel : UserControl
{
    /// <summary>抽屉宽度。必须与 XAML 里两处动画的 From/To 一致，改宽度要一起改。</summary>
    private const double DrawerWidth = 460;

    /// <summary>关闭动画进行中。动画的 Completed 里要先看它一眼（详见下方注释）。</summary>
    private bool _isClosing;

    public ModDownloadManagerViewModel ViewModel { get; }

    public ModDownloadPanel()
    {
        ViewModel = App.GetService<ModDownloadManagerViewModel>();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        SlideOutStoryboard.Completed += (_, _) =>
        {
            // 防御：WinUI 的 Stop() 也可能触发 Completed；若已被 Show() 打断则不能 Collapse，
            // 否则会出现「刚滑进来就被收起来」。
            if (!_isClosing)
                return;

            _isClosing = false;
            PanelRoot.Visibility = Visibility.Collapsed;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModDownloadManagerViewModel.IsOpen))
            return;

        if (ViewModel.IsOpen)
            Show();
        else
            Hide();
    }

    private void Show()
    {
        // 防御：关闭动画进行中被打断时，停掉它并清标记，免得它的 Completed 稍后把面板错误 Collapse。
        _isClosing = false;
        SlideOutStoryboard.Stop();
        SlideInStoryboard.Stop();
        DrawerBorder.RenderTransform = new TranslateTransform { X = DrawerWidth };
        PanelRoot.Visibility = Visibility.Visible;
        SlideInStoryboard.Begin();
    }

    /// <summary>开始收起动画。收完（<c>SlideOutStoryboard.Completed</c>）才真正 Collapse。</summary>
    private void Hide()
    {
        if (PanelRoot.Visibility != Visibility.Visible) return;
        _isClosing = true;
        SlideOutStoryboard.Begin();
    }

    private void TapCloseArea_Tapped(object sender, TappedRoutedEventArgs e) =>
        ViewModel.ClosePanelCommand.Execute(null);

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        ViewModel.ClosePanelCommand.Execute(null);
}