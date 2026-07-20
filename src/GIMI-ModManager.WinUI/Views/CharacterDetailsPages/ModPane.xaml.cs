using System.Text;
using Windows.ApplicationModel.DataTransfer;
using GIMI_ModManager.WinUI.ViewModels.CharacterDetailsViewModels.SubViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace GIMI_ModManager.WinUI.Views.CharacterDetailsPages;

public sealed partial class ModPane : UserControl
{
    private Button? _debugToggleBtn;
    private TextBox? _debugTextBox;
    private bool _debugVisible;
    private bool _isKeySwapExpanded;

    public ModPane()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ModPaneVM), typeof(ModPane), new PropertyMetadata(default(ModPaneVM)));

    public ModPaneVM ViewModel
    {
        get { return (ModPaneVM)GetValue(ViewModelProperty); }
        set
        {
            SetValue(ViewModelProperty, value);
            OnViewModelSetHandler(ViewModel);
        }
    }


    private void OnViewModelSetHandler(ModPaneVM viewModel)
    {
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 延迟创建调试面板，等当前布局 pass 完成后再操作 Children
        DispatcherQueue.TryEnqueue(() =>
        {
            try { CreateDebugPanel(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ModPane] CreateDebugPanel failed: {ex}"); }
        });
    }

    private void CreateDebugPanel()
    {
        if (KeyBindingHintPanel.Children.Count == 0) return;

        // 调试开关按钮
        _debugToggleBtn = new Button
        {
            Content = "📋 调试",
            FontSize = 10,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 0, 4),
        };
        ToolTipService.SetToolTip(_debugToggleBtn, "显示原始按键数据（可复制）");
        _debugToggleBtn.Click += DebugToggle_Click;

        // 可复制文本块
        _debugTextBox = new TextBox
        {
            FontSize = 10,
            FontFamily = new FontFamily("Consolas"),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 60,
            MaxHeight = 200,
            Visibility = Visibility.Collapsed,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(4)
        };

        // 打包成一个 StackPanel 插到最前面
        var debugPanel = new StackPanel { Spacing = 4 };
        debugPanel.Children.Add(_debugToggleBtn);
        debugPanel.Children.Add(_debugTextBox);
        KeyBindingHintPanel.Children.Insert(0, debugPanel);
    }

    private void DebugToggle_Click(object sender, RoutedEventArgs e)
    {
        _debugVisible = !_debugVisible;
        if (_debugTextBox is null) return;

        _debugTextBox.Visibility = _debugVisible ? Visibility.Visible : Visibility.Collapsed;

        if (_debugVisible && ViewModel?.ModModel is { } mod)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 按键绑定原始数据（可直接复制粘贴）===");
            sb.AppendLine($"Mod: {mod.ModDisplayName}");
            sb.AppendLine();
            foreach (var g in mod.KeyBindingGroups)
            {
                sb.AppendLine($"--- {g.IniFileRelativePath} ---");
                foreach (var b in g.Bindings)
                {
                    sb.AppendLine($"  Section : {b.SectionName}");
                    sb.AppendLine($"  RawLine : {b.RawLine}");
                    sb.AppendLine($"  KeyValue: {b.KeyValue}");
                    sb.AppendLine($"  IsArrow : {b.IsArrowKey}");
                    sb.AppendLine($"  Action  : {b.ActionLabel}");
                    sb.AppendLine($"  Desc    : {b.Description}");
                    sb.AppendLine();
                }
            }
            _debugTextBox.Text = sb.ToString();
        }
    }


    private void KeySwapToggle_Click(object sender, RoutedEventArgs e)
    {
        _isKeySwapExpanded = !_isKeySwapExpanded;
        KeySwapChevron.Glyph = _isKeySwapExpanded ? "" : "";
        KeySwapContent.Visibility = _isKeySwapExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PaneImage_OnDragEnter(object sender, DragEventArgs e)
    {
        if (ViewModel.IsReadOnly || ViewModel.BusySetter.IsHardBusy)
            return;
        var deferral = e.GetDeferral();

        if (e.DataView.Contains(StandardDataFormats.WebLink))
        {
            var url = await e.DataView.GetWebLinkAsync();
            var isValidHttpLink = ViewModel.CanSetImageFromDragDropWeb(url);
            if (isValidHttpLink)
                e.AcceptedOperation = DataPackageOperation.Copy;
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var data = await e.DataView.GetStorageItemsAsync();
            if (ViewModel.CanSetImageFromDragDropStorageItem(data))
                e.AcceptedOperation = DataPackageOperation.Copy;
        }

        deferral.Complete();
    }

    private async void PaneImage_OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel.IsReadOnly || ViewModel.BusySetter.IsHardBusy)
            return;

        var deferral = e.GetDeferral();
        if (e.DataView.Contains(StandardDataFormats.Uri))
        {
            var uri = await e.DataView.GetUriAsync();
            await ViewModel.SetImageFromDragDropWeb(uri);
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            await ViewModel.SetImageFromDragDropFile(await e.DataView.GetStorageItemsAsync());
        }

        deferral.Complete();
    }
}