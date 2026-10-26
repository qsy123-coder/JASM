using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>
/// 浮窗内的删除 Mod 流程。
///
/// 确认提示必须是**浮窗内嵌卡片**：浮窗是 WS_EX_NOACTIVATE，主窗口的 ContentDialog 会激活
/// 主窗口并把游戏踢出前台；这里用一层覆盖 Grid 顶住指针，不碰前台。
/// 删除复用 <c>CharacterModList.DeleteModBySkinEntryId</c> 的回收站模式，误删可从 Windows 回收站恢复。
/// </summary>
internal sealed partial class OverlayViewModel
{
    private Guid? _pendingDeleteModId;
    private string? _pendingDeleteModName;
    private bool _pendingDeleteWasEnabled;

    [ObservableProperty] private bool _isDeleteConfirmVisible;
    [ObservableProperty] private string _deleteConfirmModName = string.Empty;
    [ObservableProperty] private bool _isDeletingMod;
    [ObservableProperty] private string? _deleteStatusMessage;

    public bool HasDeleteStatus => !string.IsNullOrWhiteSpace(DeleteStatusMessage);
    public bool CanConfirmDelete => !IsDeletingMod;
    public string ConfirmDeleteButtonText => IsDeletingMod ? "正在删除…" : "移至回收站";

    partial void OnIsDeletingModChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfirmDelete));
        OnPropertyChanged(nameof(ConfirmDeleteButtonText));
    }

    partial void OnDeleteStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasDeleteStatus));

    /// <summary>
    /// 点了某行的垃圾桶：先把目标记下来并弹确认卡；真正动盘在 <see cref="ConfirmDeleteAsync"/>。
    /// 修复任务运行中直接拒绝 —— 安装/删除/修复同时动同一个 Mod 目录没有可预期的结果。
    /// </summary>
    public void RequestDeleteMod(OverlayModItemViewModel? item)
    {
        if (item is null)
            return;

        if (FixSession.IsRunning)
        {
            ErrorMessage = "修复进行中，暂不能删除 Mod。";
            SetFixMessage("修复进行中，暂不能删除 Mod。", isError: true);
            return;
        }

        _pendingDeleteModId = item.Id;
        _pendingDeleteModName = item.Name;
        _pendingDeleteWasEnabled = item.IsEnabled;

        DeleteConfirmModName = item.Name;
        IsDeleteConfirmVisible = true;
        DeleteStatusMessage = null;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        if (IsDeletingMod)
            return;

        ResetDeleteConfirm();
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (IsDeletingMod || _pendingDeleteModId is not { } modId)
            return;

        IsDeletingMod = true;
        var modName = _pendingDeleteModName ?? "Mod";
        var wasEnabled = _pendingDeleteWasEnabled;

        try
        {
            var modList = ResolveModList(SelectedCharacter?.Character);
            if (modList is null)
                throw new InvalidOperationException("当前角色已不在列表中");

            // 回收站模式下 Remove-Item 可能较慢，放到线程池；不要在 UI 线程上删大文件夹
            await Task.Run(() => modList.DeleteModBySkinEntryId(modId, moveToRecycleBin: true));

            ResetDeleteConfirm();
            DeleteStatusMessage = $"已删除「{modName}」（已移至回收站）";
            _logger.Information("[浮窗] 已删除 Mod: {Mod}（移至回收站）", modName);

            // 列表与角色下拉都可能有变化（删掉角色最后一个 Mod 时该角色会消失）
            RefreshCharacters();
            RefreshFixTargets();

            // 只有启用中的 Mod 才影响游戏；禁用中的删掉不需要重载
            if (wasEnabled)
                await RefreshCoordinator.RequestRefreshAsync();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗] 删除 Mod 失败: {Mod}", modName);
            ResetDeleteConfirm();
            ErrorMessage = "删除失败：" + e.Message;

            // 失败可能发生在“已从列表摘掉、文件夹没删掉”之后，重对一次真实状态
            RefreshCharacters();
            RefreshFixTargets();
        }
        finally
        {
            IsDeletingMod = false;
        }
    }

    private void ResetDeleteConfirm()
    {
        IsDeleteConfirmVisible = false;
        DeleteConfirmModName = string.Empty;
        _pendingDeleteModId = null;
        _pendingDeleteModName = null;
        _pendingDeleteWasEnabled = false;
    }
}