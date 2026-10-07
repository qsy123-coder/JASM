using System.Text;
using Windows.ApplicationModel.DataTransfer;
using GIMI_ModManager.Core.Entities.Mods.Helpers;
using GIMI_ModManager.WinUI.Services.Input;
using GIMI_ModManager.WinUI.Services.Notifications;
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


    /// <summary>
    /// 点击按键徽章 → 把该按键合成发给正在运行的游戏（等于在真实键盘上按一次）。
    /// 处理器放 code-behind 而不是 Core 的 POCO 上：SendInput 需要 windows TFM，
    /// 而模板里的 x:Bind 也看不到外层的 ModPane.ViewModel。
    /// </summary>
    private async void SendKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is not FrameworkElement element)
                return;

            // 条目优先从 Tag 取（模板里 Tag="{x:Bind}"，编译期写入，不依赖 ItemsRepeater 是否给
            // 实现元素设置 DataContext —— 这一步在真机上不报错、只是静默什么都不做，不留隐患）；
            // DataContext 作为兜底。
            var entry = element.Tag as ModIniKeyBindingEntry ?? element.DataContext as ModIniKeyBindingEntry;
            if (entry is null || !entry.CanSendKey)
                return;

            var result = await App.GetService<IGameKeySender>()
                .SendKeyAsync(entry.KeyCode!.Value, entry.ModifierKeyCodes);

            // 成功不打扰用户：游戏里已经能看到反应
            if (result.Status != GameKeySendStatus.Sent)
                ShowKeySendFailure(result);
        }
        catch (Exception ex)
        {
            // 发按键失败绝不能把 UI 线程炸掉
            System.Diagnostics.Debug.WriteLine($"[ModPane] SendKey_Click failed: {ex}");
        }
    }

    private static void ShowKeySendFailure(GameKeySendResult result)
    {
        // 只给用户能自己做点什么的话；具体失败的路径 / 进程名在 Serilog 里（见 GameKeySender）
        var fallback = result.Status switch
        {
            GameKeySendStatus.BlockedChord =>
                "出于安全考虑没有发送这个组合键（Alt+F4 这类会直接把游戏关掉）。",
            GameKeySendStatus.TargetNotConfigured =>
                "找不到游戏信息，请先确认 JASM 里配置的游戏目录指向装了 mod 的 XXMI 目录。",
            GameKeySendStatus.GameProcessNotRunning =>
                "游戏当前没有运行，先把游戏开起来再点。",
            GameKeySendStatus.GameWindowNotFound =>
                "找到游戏进程了，但没有可用的游戏窗口。切回游戏画面后再试一次。",
            // 这两条**不再劝用户把 JASM 自己以管理员身份运行**。那条老出路确实送得进按键，
            // 代价却是跨完整性级别的 OLE 拖拽被 UIPI 整个掐掉：浮窗与主窗口会同时只剩禁止光标、
            // 松手没反应、且不报任何错 —— 拿「拖拽安装全灭」换「按键能送」不划算，
            // 也不该由一句失败提示替用户做这个决定。助手（Elevator.exe，4.0.0.0）就是为这两种情况准备的。
            GameKeySendStatus.NeedsElevation =>
                "游戏正以管理员身份运行，JASM 自己和提权助手都没能把按键送进去。"
                + "请更新 JASM 后重试（新版助手专为这种情况准备）；别用管理员身份运行 JASM 本身，那修不了按键，还会让拖拽安装失效。",
            GameKeySendStatus.SendInputFailed =>
                "按键没能送进游戏。如果游戏是以管理员身份运行的，请更新 JASM 让提权助手代发；"
                + "别用管理员身份运行 JASM 本身，那会让拖拽安装失效。",
            _ => "按键没能送进游戏。"
        };

        // 提权 / 助手的失败原因是**动态**的（助手回执里的 token 决定下一步是「先点一下游戏画面」
        // 还是「更新 JASM」），这时用它带出来的那句话，别让上面那句泛泛的 fallback 盖掉。
        var message = result.Detail ?? fallback;

        App.GetService<NotificationManager>().ShowNotification("发送按键失败", message, TimeSpan.FromSeconds(6));
    }

    private void KeySwapToggle_Click(object sender, RoutedEventArgs e)
    {
        _isKeySwapExpanded = !_isKeySwapExpanded;
        KeySwapChevron.Glyph = _isKeySwapExpanded ? "" : "";
        KeySwapContent.Visibility = _isKeySwapExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>两次「这一拖收不了」提示之间的最短间隔，见 <see cref="NotifyDragRefused"/>。</summary>
    private const int DragRefusalNoticeMinimumIntervalMs = 3000;

    /// <summary>上一次弹提示的时刻（<see cref="Environment.TickCount64"/>；null = 还没弹过）。</summary>
    private long? _lastDragRefusalNoticeTicks;

    /// <summary>
    /// 收不了这一拖时给用户一句话说清楚为什么。
    ///
    /// <para>
    /// 必须给：不接受拖拽就是不设 <c>AcceptedOperation</c>，用户那边只有禁止光标、松手也不会有 Drop，
    /// 界面上没有一个字说明原因。默认拖到 <c>ModListArea</c> 上（那里也是 Mod 包的落点）时，
    /// 用户根本分不清「这里不接图」和「JASM 坏了」。
    /// </para>
    ///
    /// 节流按时间而不是「一次拖拽只记一次」的记账：拖拽事件会在父子元素之间来回冒，
    /// Enter/Leave 的配对并不可靠（<c>CharactersPage</c> 里「每次 DragOver 都重判」那条注释就为同一件事）。
    /// </summary>
    private void NotifyDragRefused(string reason)
    {
        var now = Environment.TickCount64;

        if (_lastDragRefusalNoticeTicks is { } last && now - last < DragRefusalNoticeMinimumIntervalMs)
            return;

        _lastDragRefusalNoticeTicks = now;

        App.GetService<NotificationManager>().ShowNotification("封面图没接住", reason, TimeSpan.FromSeconds(6));
    }

    private async void PaneImage_OnDragEnter(object sender, DragEventArgs e)
    {
        // 早退（只读 / 忙）挪进了 ViewModel 的判定里，这里只负责把理由说出来 ——
        // 原先那两处早退是静默的，用户只看到禁止光标
        var deferral = e.GetDeferral();
        try
        {
            if (e.DataView.Contains(StandardDataFormats.WebLink))
            {
                var url = await e.DataView.GetWebLinkAsync();
                if (ViewModel.DescribeImageDropRefusalForWeb(url) is { } refusal)
                    NotifyDragRefused(refusal);
                else
                    e.AcceptedOperation = DataPackageOperation.Copy;
            }
            else if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var data = await e.DataView.GetStorageItemsAsync();
                if (ViewModel.DescribeImageDropRefusalForFile(data) is { } refusal)
                    NotifyDragRefused(refusal);
                else
                    e.AcceptedOperation = DataPackageOperation.Copy;
            }
            else
            {
                NotifyDragRefused("图片区只接图片文件，或 http/https 的图片链接。");
            }
        }
        finally
        {
            deferral.Complete();
        }
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