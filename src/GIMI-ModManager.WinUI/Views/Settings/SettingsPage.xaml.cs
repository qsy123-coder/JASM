using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.WinUI.ViewModels;
using GIMI_ModManager.WinUI.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace GIMI_ModManager.WinUI.Views;

// TODO: Set the URL for your privacy policy by updating SettingsPage_PrivacyTermsLink.NavigateUri in Resources.resw.
public sealed partial class SettingsPage : Page
{
    /// <summary>拖拽自检的等待计时器；没在自检时是 <c>null</c>。</summary>
    private DispatcherTimer? _dragSelfCheckTimer;

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = App.GetService<SettingsViewModel>();
        InitializeComponent();
    }

    private void GimiFolder_OnPathChangedEvent(object? sender, FolderSelector.StringEventArgs e)
        => ViewModel.PathToGIMIFolderPicker.Validate(e.Value);


    private void ModsFolder_OnPathChangedEvent(object? sender, FolderSelector.StringEventArgs e)
        => ViewModel.PathToModsFolderPicker.Validate(e.Value);

    private async void LanguageSelectorComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var item = (string)e.AddedItems[0];
        await ViewModel.SelectLanguageCommand.ExecuteAsync(item).ConfigureAwait(false);
    }

    private async void GameSelectorComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var item = (string)e.AddedItems[0];
        await ViewModel.SelectGameCommand.ExecuteAsync(item).ConfigureAwait(false);
    }

    private void LocalCacheSlider_OnValueChanged(object _, RangeBaseValueChangedEventArgs e)
    {
        if (ViewModel.SetCacheLimitCommand.CanExecute((int)e.NewValue))
            ViewModel.SetCacheLimitCommand.ExecuteAsync((int)e.NewValue);
    }

    // ── 拖拽自检：10 秒窗口 + 落点事件（结论由 ViewModel 出）────────────────

    /// <summary>
    /// 「开始自检」按下的那一瞬间开始计时。
    ///
    /// 计时放在页面而不是 ViewModel 里：它只需要一个 UI 计时器，而页面是唯一知道「这一段界面还在不在」的地方；
    /// 结论与报告文本仍归 ViewModel（命令的 CanExecute 也仍由它管，所以自检中按钮本来就是禁用的 ——
    /// 禁用的按钮不会再触发 Click，计时不会被重复开）。
    /// </summary>
    private void DragSelfCheckStart_OnClick(object sender, RoutedEventArgs e)
    {
        _dragSelfCheckTimer?.Stop();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(DragDropSelfCheck.WindowSeconds) };
        timer.Tick += (_, _) =>
        {
            StopDragSelfCheckTimer();
            ViewModel.CompleteDragSelfCheck(dragEventReceived: false);
        };

        _dragSelfCheckTimer = timer;
        timer.Start();
    }

    /// <summary>
    /// 拖着文件进了自检框。<c>DragEnter</c> 与 <c>DragOver</c> 都挂它：
    /// 指针进入元素时只来一发 Enter，之后在框里移动只有 Over —— 两个都挂才不会漏。
    /// </summary>
    private void DragSelfCheckBox_OnDragEnter(object sender, DragEventArgs e) => ReportDragSelfCheckEvent(e);

    private void DragSelfCheckBox_OnDragOver(object sender, DragEventArgs e) => ReportDragSelfCheckEvent(e);

    /// <summary>
    /// 收到拖拽事件就收尾。**这里刻意不判「数据里有没有真文件」**：自检要回答的正是「事件到底进没进 JASM」，
    /// 若按数据内容提前拒绝，用户看到的禁止光标又会变成两条来路分不清 —— 那正是这个功能要终结的事。
    /// 有没有真文件由报告里的格式清单与结论去说。
    /// </summary>
    private void ReportDragSelfCheckEvent(DragEventArgs e)
    {
        if (!ViewModel.IsDragSelfCheckRunning)
            return;

        // 无条件接：光标显示「复制」是「事件到了」的现场证据，用户一眼就能与禁止光标区分开
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;

        string[] formats;
        try
        {
            formats = e.DataView.AvailableFormats.ToArray();
        }
        catch (Exception)
        {
            // 延迟渲染的数据对象读格式清单会抛（来源进程忙 / 已退出）。这是诊断，不能反过来打断拖拽
            formats = [];
        }

        StopDragSelfCheckTimer();
        ViewModel.CompleteDragSelfCheck(dragEventReceived: true, formats);
    }

    /// <summary>松手落下。事件到达这一点与 DragOver 是同一个事实，区别只是**落下了**（用户看到的东西动过）。</summary>
    private void DragSelfCheckBox_OnDrop(object sender, DragEventArgs e)
    {
        if (!ViewModel.IsDragSelfCheckRunning)
            return;

        e.Handled = true;

        StopDragSelfCheckTimer();
        ViewModel.CompleteDragSelfCheck(dragEventReceived: true);
    }

    private void StopDragSelfCheckTimer()
    {
        _dragSelfCheckTimer?.Stop();
        _dragSelfCheckTimer = null;
    }
}