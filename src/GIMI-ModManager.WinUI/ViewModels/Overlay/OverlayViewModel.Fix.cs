using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.Core.Entities.Mods.Helpers;
using GIMI_ModManager.Core.Services.WuwaModFixer;
using GIMI_ModManager.WinUI.Services.Overlay;

namespace GIMI_ModManager.WinUI.ViewModels.Overlay;

/// <summary>
/// 浮窗「修复」页签：修复/回滚两个子视图、五个开关、勾选列表、进度日志、配置更新。
///
/// 拆成 partial 文件而不是第二套 VM：这页必须复用主管道的
/// <c>SelectedCharacter</c>、<c>ResolveModList</c>、<c>RefreshCoordinator</c> 与设置落盘，
/// 另起一个 VM 只会把「当前角色」镜像成两份、迟早对不上。
/// </summary>
internal sealed partial class OverlayViewModel
{
    /// <summary>修复页专用尺寸：五开关 + 勾选列表 + 日志，460×480 的大档装不下。</summary>
    public static OverlaySizeOption RepairSizeOption { get; } = new("修复", 520, 660);

    private bool _loadingFixSettings;
    private bool _configCheckedThisSession;
    private List<WuwaBackupGroup> _lastBackupGroups = [];
    private string? _rollbackAllowedRoot;

    /// <summary>女漂修复上次用的模式（1/2）。关掉再打开时恢复，不默认退回模式 1。</summary>
    private int _lastAeroMode = 1;

    // ── 页签 ────────────────────────────────────────────────────────────

    /// <summary>0 = Mod 切换、1 = 修复。</summary>
    [ObservableProperty] private int _activeTabIndex;

    public bool IsModTabActive => ActiveTabIndex == 0;
    public bool IsFixTabActive => ActiveTabIndex == 1;

    /// <summary>空态文案只在 Mod 页显示；修复页有自己的空态。</summary>
    public bool ShowModListEmpty => IsModTabActive && IsListEmpty;

    partial void OnIsListEmptyChanged(bool value) => OnPropertyChanged(nameof(ShowModListEmpty));

    partial void OnActiveTabIndexChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 1);
        if (clamped != value)
        {
            ActiveTabIndex = clamped;
            return;
        }

        OnPropertyChanged(nameof(IsModTabActive));
        OnPropertyChanged(nameof(IsFixTabActive));
        OnPropertyChanged(nameof(ShowModListEmpty));
        OnPropertyChanged(nameof(CurrentSizeOption));

        if (IsFixTabActive)
            _ = EnterFixTabAsync();
    }

    // ── 修复 / 回滚子视图 ───────────────────────────────────────────────

    /// <summary>0 = 修复、1 = 回滚。</summary>
    [ObservableProperty] private int _fixSubTabIndex;

    public bool IsFixViewActive => FixSubTabIndex == 0;
    public bool IsRollbackViewActive => FixSubTabIndex == 1;

    partial void OnFixSubTabIndexChanged(int value)
    {
        var clamped = value == 1 ? 1 : 0;
        if (clamped != value)
        {
            FixSubTabIndex = clamped;
            return;
        }

        OnPropertyChanged(nameof(IsFixViewActive));
        OnPropertyChanged(nameof(IsRollbackViewActive));

        if (_loadingFixSettings)
            return;

        Settings.FixSubTab = clamped;
        _ = SaveSettingsAsync();

        if (IsRollbackViewActive)
            _ = RefreshRestorePointsAsync();
    }

    // ── 五个开关 ────────────────────────────────────────────────────────

    [ObservableProperty] private bool _fixDerivedHashes;
    [ObservableProperty] private bool _fixStableTexture;
    [ObservableProperty] private bool _fixRendering33;
    [ObservableProperty] private bool _fixAemeathMech;
    [ObservableProperty] private int _fixAeroMode;
    [ObservableProperty] private bool _isLogExpanded;

    /// <summary>女漂修复的总开关。关掉时把模式归零；打开时默认 TexCoord 覆盖。</summary>
    public bool FixAeroEnabled
    {
        get => FixAeroMode > 0;
        set
        {
            if (value == FixAeroEnabled)
                return;

            FixAeroMode = value ? Math.Clamp(_lastAeroMode, 1, 2) : 0;
        }
    }

    /// <summary>女漂模式二选一的 Segmented 序号：0 = TexCoord、1 = 镜像。</summary>
    public int FixAeroModeIndex
    {
        get => FixAeroMode == 2 ? 1 : 0;
        set
        {
            if (FixAeroMode > 0)
                FixAeroMode = value == 1 ? 2 : 1;
        }
    }

    partial void OnFixDerivedHashesChanged(bool value)
    {
        if (value && FixStableTexture)
            FixStableTexture = false;

        PersistFixSettings();
    }

    partial void OnFixStableTextureChanged(bool value)
    {
        if (value && FixDerivedHashes)
            FixDerivedHashes = false;

        PersistFixSettings();
    }

    partial void OnFixRendering33Changed(bool value) => PersistFixSettings();
    partial void OnFixAemeathMechChanged(bool value) => PersistFixSettings();

    partial void OnFixAeroModeChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 2);
        if (clamped != value)
        {
            FixAeroMode = clamped;
            return;
        }

        if (clamped is 1 or 2)
            _lastAeroMode = clamped;

        OnPropertyChanged(nameof(FixAeroEnabled));
        OnPropertyChanged(nameof(FixAeroModeIndex));
        PersistFixSettings();
    }

    partial void OnIsFixTargetListExpandedChanged(bool value) =>
        OnPropertyChanged(nameof(FixTargetListToggleText));

    partial void OnIsEngineReadyChanged(bool value) => OnPropertyChanged(nameof(CanStartFix));
    partial void OnIsUpdatingConfigChanged(bool value) => NotifyFixCommandStates();
    partial void OnIsRollbackRunningChanged(bool value) => NotifyFixCommandStates();
    partial void OnIsCleanConfirmPendingChanged(bool value) => OnPropertyChanged(nameof(CleanBackupsLabel));

    partial void OnIsLogExpandedChanged(bool value)
    {
        if (_loadingFixSettings)
            return;

        Settings.FixLogExpanded = value;
        _ = SaveSettingsAsync();
    }

    /// <summary>修复/回滚进行中时，开关与配置按钮都不可点。</summary>
    public bool CanEditFixOptions => !FixSession.IsRunning && !IsRollbackRunning && !IsUpdatingConfig;

    /// <summary>修复运行中锁定页签与角色：目标路径已经快照，切角色/回 Mod 页只会让用户误以为改的是同一批。</summary>
    public bool CanSwitchTabs => !FixSession.IsRunning;
    public bool CanSwitchCharacter => !FixSession.IsRunning;

    // ── 目标勾选 ────────────────────────────────────────────────────────

    public ObservableCollection<OverlayFixTargetItem> FixTargets { get; } = [];

    [ObservableProperty] private bool _isFixTargetListExpanded;

    public bool HasFixTargets => FixTargets.Count > 0;
    public bool HasNoFixTargets => FixTargets.Count == 0;
    public int FixTargetCount => FixTargets.Count;
    public int SelectedFixTargetCount => FixTargets.Count(item => item.IsSelected);

    public string FixScopeText => SelectedCharacter is null
        ? "未选择角色"
        : $"{SelectedCharacter.DisplayName} · {FixTargetCount} 个 Mod";

    public string FixTargetListToggleText =>
        IsFixTargetListExpanded ? "收起选择" : $"选择 Mod（{SelectedFixTargetCount}/{FixTargetCount}）";

    public bool CanStartFix =>
        IsEngineReady
        && !FixSession.IsRunning
        && !IsRollbackRunning
        && !IsUpdatingConfig
        && SelectedFixTargetCount > 0;

    // ── 引擎 / 配置 / 消息 ─────────────────────────────────────────────

    public WuwaFixSession FixSession { get; }

    [ObservableProperty] private bool _isEngineReady;
    [ObservableProperty] private bool _hasConfigUpdate;
    [ObservableProperty] private string _configVersionText = "cfg -";
    [ObservableProperty] private string? _fixMessage;
    [ObservableProperty] private bool _isFixMessageError;

    public bool HasFixMessage => !string.IsNullOrWhiteSpace(FixMessage);

    // ── 回滚 ────────────────────────────────────────────────────────────

    public ObservableCollection<OverlayRestorePointItem> RestorePoints { get; } = [];

    [ObservableProperty] private bool _isRollbackScanning;
    [ObservableProperty] private bool _isRollbackRunning;
    [ObservableProperty] private bool _isUpdatingConfig;
    [ObservableProperty] private string? _rollbackMessage;
    [ObservableProperty] private bool _isRollbackMessageError;
    [ObservableProperty] private bool _isCleanConfirmPending;
    [ObservableProperty] private OverlayRestorePointItem? _selectedRestorePoint;

    public bool HasRestorePoints => RestorePoints.Count > 0;
    public bool HasNoRestorePoints => RestorePoints.Count == 0 && !IsRollbackScanning;
    public bool HasRollbackMessage => !string.IsNullOrWhiteSpace(RollbackMessage);
    public string CleanBackupsLabel => IsCleanConfirmPending ? "确认清理？" : "清理全部备份";
    public bool CanRollback => !FixSession.IsRunning && !IsRollbackRunning;
    public bool CanRollbackSelected => CanRollback && SelectedRestorePoint is not null;

    partial void OnSelectedRestorePointChanged(OverlayRestorePointItem? value) =>
        OnPropertyChanged(nameof(CanRollbackSelected));

    partial void OnFixMessageChanged(string? value) => OnPropertyChanged(nameof(HasFixMessage));

    // ── 初始化 ──────────────────────────────────────────────────────────

    /// <summary>Settings 已加载后调用（InitializeAsync 里），把上次的开关/子页还原出来。</summary>
    private void LoadFixSettings()
    {
        _loadingFixSettings = true;
        try
        {
            FixStableTexture = Settings.FixStableTexture;
            FixDerivedHashes = Settings.FixDerivedHashes && !Settings.FixStableTexture;
            FixRendering33 = Settings.FixRendering33;
            FixAemeathMech = Settings.FixAemeathMech;
            FixAeroMode = Math.Clamp(Settings.FixAeroMode ?? 0, 0, 2);
            FixSubTabIndex = Settings.FixSubTab == 1 ? 1 : 0;
            IsLogExpanded = Settings.FixLogExpanded;
        }
        finally
        {
            _loadingFixSettings = false;
        }
    }

    private void InitializeFixSession()
    {
        FixSession.PropertyChanged += OnFixSessionPropertyChanged;
    }

    private void OnFixSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WuwaFixSession.IsRunning))
        {
            OnPropertyChanged(nameof(CanStartFix));
            OnPropertyChanged(nameof(CanEditFixOptions));
            OnPropertyChanged(nameof(CanRollback));
            OnPropertyChanged(nameof(CanRollbackSelected));
            OnPropertyChanged(nameof(CanSwitchTabs));
            OnPropertyChanged(nameof(CanSwitchCharacter));
        }
    }

    /// <summary>换角色时若正停在回滚子页，还原点列表也要跟着换 —— 否则会显示上一个角色的备份。</summary>
    private void RefreshRollbackForCharacterChange()
    {
        if (!IsRollbackViewActive)
            return;

        SelectedRestorePoint = null;
        _ = RefreshRestorePointsAsync();
    }

    private async Task EnterFixTabAsync()
    {
        if (!FixSession.IsRunning)
            RefreshFixTargets();

        await EnsureEngineAsync();

        if (IsRollbackViewActive)
            await RefreshRestorePointsAsync();

        _ = CheckConfigUpdateAsync();
    }

    private async Task EnsureEngineAsync()
    {
        try
        {
            var ready = await _fixerEngine.EnsureReadyAsync();
            IsEngineReady = ready.IsReady;
            ConfigVersionText = ready.Config is null ? "cfg -" : $"cfg {ready.Config.CurrentVersion}";

            if (!ready.IsReady)
                SetFixMessage(ready.Error ?? "修复引擎不可用", isError: true);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 引擎初始化失败");
            IsEngineReady = false;
            SetFixMessage("修复引擎初始化失败：" + e.Message, isError: true);
        }
    }

    private async Task CheckConfigUpdateAsync()
    {
        var last = ParseLastConfigCheck();
        if (!WuwaConfigCheckPolicy.ShouldCheck(last, DateTimeOffset.UtcNow, _configCheckedThisSession))
            return;

        _configCheckedThisSession = true;
        Settings.LastConfigCheckUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        _ = SaveSettingsAsync();

        try
        {
            HasConfigUpdate = await _fixerEngine.CheckForConfigUpdateAsync();
        }
        catch (Exception e)
        {
            _logger.Debug(e, "[浮窗修复] 静默检查配置失败");
            HasConfigUpdate = false;
        }
    }

    private DateTimeOffset? ParseLastConfigCheck()
    {
        if (string.IsNullOrWhiteSpace(Settings.LastConfigCheckUtc))
            return null;

        return DateTimeOffset.TryParse(Settings.LastConfigCheckUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value)
            ? value
            : null;
    }

    // ── 命令：目标与开关 ────────────────────────────────────────────────

    [RelayCommand]
    private void SelectAllFixTargets()
    {
        foreach (var target in FixTargets)
            target.IsSelected = true;
    }

    [RelayCommand]
    private void DeselectAllFixTargets()
    {
        foreach (var target in FixTargets)
            target.IsSelected = false;
    }

    [RelayCommand]
    private void ToggleFixTargetList() => IsFixTargetListExpanded = !IsFixTargetListExpanded;

    [RelayCommand]
    private async Task UpdateConfigAsync()
    {
        if (!CanEditFixOptions)
            return;

        IsUpdatingConfig = true;
        NotifyFixCommandStates();
        SetFixMessage("正在更新配置…", isError: false);

        try
        {
            var info = await _fixerEngine.UpdateConfigAsync();
            ConfigVersionText = $"cfg {info.CurrentVersion}";
            HasConfigUpdate = false;
            SetFixMessage($"配置已更新到 {info.CurrentVersion}", isError: false);
            await EnsureEngineAsync();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 更新配置失败");
            SetFixMessage("更新配置失败：" + e.Message, isError: true);
        }
        finally
        {
            IsUpdatingConfig = false;
            NotifyFixCommandStates();
        }
    }

    // ── 命令：开始 / 终止修复 ──────────────────────────────────────────

    [RelayCommand]
    private async Task StartFixAsync()
    {
        if (!CanStartFix || SelectedCharacter is null)
            return;

        var characterName = SelectedCharacter.DisplayName;
        var targets = FixTargets
            .Where(item => item.IsSelected)
            .Select(item => new WuwaFixTarget(item.Id, item.Name, item.ModPath))
            .ToList();

        if (targets.Count == 0)
        {
            SetFixMessage("至少选择一个 Mod", isError: true);
            return;
        }

        SetFixMessage(null, isError: false);
        NotifyFixCommandStates();

        var options = new WuwaFixOptions(
            DerivedHashes: FixDerivedHashes,
            StableTexture: FixStableTexture,
            Rendering33: FixRendering33,
            AemeathMech: FixAemeathMech,
            AeroFixMode: FixAeroMode).Normalize();

        await FixSession.StartAsync(targets, options, characterName);
    }

    [RelayCommand]
    private void TerminateFix()
    {
        if (!FixSession.IsRunning)
            return;

        FixSession.Cancel();
        SetFixMessage("正在终止…", isError: false);
    }

    private async void OnFixRunCompleted(object? sender, WuwaFixRunSummary summary)
    {
        try
        {
            SetFixMessage(summary.ToSummaryText(), isError: summary.Failed > 0);
            OnPropertyChanged(nameof(CanStartFix));
            OnPropertyChanged(nameof(CanEditFixOptions));

            if (summary.HasChanges)
            {
                // 复用浮窗现有刷新链路：前台交接、2.5s 节流、失败提示都在协调器里
                await RefreshCoordinator.RequestRefreshAsync();
            }
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 收尾失败");
        }
    }

    // ── 命令：回滚 ──────────────────────────────────────────────────────

    private async Task RefreshRestorePointsCoreAsync()
    {
        if (IsRollbackScanning)
            return;

        var modList = ResolveModList(SelectedCharacter?.Character);
        RestorePoints.Clear();
        _lastBackupGroups = [];

        if (modList is null)
        {
            _rollbackAllowedRoot = null;
            NotifyRestorePointState();
            return;
        }

        IsRollbackScanning = true;
        NotifyRestorePointState();

        try
        {
            _rollbackAllowedRoot = modList.AbsModsFolderPath;
            var roots = modList.Mods
                .Select(entry => entry.Mod.FullPath)
                .Where(Directory.Exists)
                .ToList();

            var scan = await _rollbackService.ScanAsync(roots, modList.AbsModsFolderPath);
            _lastBackupGroups = scan.Groups.ToList();

            foreach (var group in scan.Groups)
                RestorePoints.Add(new OverlayRestorePointItem(group));

            SelectedRestorePoint = RestorePoints.FirstOrDefault();

            if (scan.Warnings.Count > 0)
                SetRollbackMessage("部分备份被跳过：" + string.Join("；", scan.Warnings.Take(3)), isError: true);
            else
                SetRollbackMessage(null, isError: false);
        }
        catch (OperationCanceledException)
        {
            SetRollbackMessage("扫描已取消", isError: true);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 扫描备份失败");
            SetRollbackMessage("扫描备份失败：" + e.Message, isError: true);
        }
        finally
        {
            IsRollbackScanning = false;
            NotifyRestorePointState();
        }
    }

    [RelayCommand]
    private async Task RollbackSelectedAsync()
    {
        var item = SelectedRestorePoint;
        if (item is null || !CanRollback || _rollbackAllowedRoot is null)
            return;

        await ExecuteRollbackAsync(
            () => _rollbackService.RollbackToAsync(_lastBackupGroups, item.TimestampKey, _rollbackAllowedRoot),
            $"已回滚到 {item.DisplayTime}");
    }

    [RelayCommand]
    private Task RefreshRestorePointsAsync() => RefreshRestorePointsCoreAsync();

    [RelayCommand]
    private async Task RestoreEarliestAsync()
    {
        if (!CanRollback || _rollbackAllowedRoot is null || _lastBackupGroups.Count == 0)
            return;

        var earliest = RestorePoints.LastOrDefault();
        await ExecuteRollbackAsync(
            () => _rollbackService.RestoreEarliestAsync(_lastBackupGroups, _rollbackAllowedRoot),
            earliest is null ? "已还原到最早状态" : $"已还原到最早状态（{earliest.DisplayTime} 之前）");
    }

    private async Task ExecuteRollbackAsync(Func<Task<WuwaRollbackReport>> action, string successMessage)
    {
        IsRollbackRunning = true;
        IsCleanConfirmPending = false;
        NotifyFixCommandStates();
        SetRollbackMessage("正在回滚…", isError: false);

        try
        {
            var report = await action();

            if (report.Success)
            {
                SetRollbackMessage(successMessage, isError: false);
                await RefreshRestorePointsAsync();

                if (report.AppliedCount > 0)
                    await RefreshCoordinator.RequestRefreshAsync();
            }
            else
            {
                var detail = report.Failures.Count > 0
                    ? string.Join("；", report.Failures.Take(3))
                    : report.FailureMessage;
                SetRollbackMessage("回滚失败：" + detail, isError: true);
            }
        }
        catch (OperationCanceledException)
        {
            SetRollbackMessage("回滚已取消", isError: true);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 回滚失败");
            SetRollbackMessage("回滚失败：" + e.Message, isError: true);
        }
        finally
        {
            IsRollbackRunning = false;
            NotifyFixCommandStates();
        }
    }

    [RelayCommand]
    private async Task CleanBackupsAsync()
    {
        if (!CanRollback || _rollbackAllowedRoot is null || _lastBackupGroups.Count == 0)
            return;

        if (!IsCleanConfirmPending)
        {
            IsCleanConfirmPending = true;
            OnPropertyChanged(nameof(CleanBackupsLabel));
            SetRollbackMessage("再次点击确认：永久删除当前角色的全部 .BAK 备份", isError: false);
            return;
        }

        IsCleanConfirmPending = false;
        OnPropertyChanged(nameof(CleanBackupsLabel));
        IsRollbackRunning = true;
        NotifyFixCommandStates();

        try
        {
            var report = await _rollbackService.CleanAllAsync(_lastBackupGroups, _rollbackAllowedRoot);
            var message = $"已清理 {report.Deleted} 个备份文件";
            if (report.Failures.Count > 0)
                message += "；失败：" + string.Join("；", report.Failures.Take(3));

            SetRollbackMessage(message, isError: report.Failures.Count > 0);
            await RefreshRestorePointsAsync();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 清理备份失败");
            SetRollbackMessage("清理备份失败：" + e.Message, isError: true);
        }
        finally
        {
            IsRollbackRunning = false;
            NotifyFixCommandStates();
        }
    }

    // ── 内部工具 ────────────────────────────────────────────────────────

    private void RefreshFixTargets()
    {
        // 运行中不重建：本次任务已经拿走了目标快照，界面再改只会让用户以为改的是当前任务
        if (FixSession.IsRunning)
            return;

        foreach (var item in FixTargets)
            item.PropertyChanged -= OnFixTargetItemPropertyChanged;

        FixTargets.Clear();

        var modList = ResolveModList(SelectedCharacter?.Character);
        if (modList is not null)
        {
            foreach (var entry in modList.Mods
                         .OrderBy(entry => entry.Mod.GetDisplayName(), StringComparer.CurrentCultureIgnoreCase))
            {
                var item = OverlayFixTargetItem.FromEntry(entry);
                item.PropertyChanged += OnFixTargetItemPropertyChanged;
                FixTargets.Add(item);
            }
        }

        OnPropertyChanged(nameof(HasFixTargets));
        OnPropertyChanged(nameof(HasNoFixTargets));
        OnPropertyChanged(nameof(FixTargetCount));
        OnPropertyChanged(nameof(FixScopeText));
        NotifyFixSelectionChanged();
    }

    private void OnFixTargetItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayFixTargetItem.IsSelected))
            NotifyFixSelectionChanged();
    }

    private void NotifyFixSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedFixTargetCount));
        OnPropertyChanged(nameof(FixTargetListToggleText));
        OnPropertyChanged(nameof(CanStartFix));
    }

    private void NotifyRestorePointState()
    {
        OnPropertyChanged(nameof(HasRestorePoints));
        OnPropertyChanged(nameof(HasNoRestorePoints));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(CanRollbackSelected));
    }

    private void NotifyFixCommandStates()
    {
        OnPropertyChanged(nameof(CanStartFix));
        OnPropertyChanged(nameof(CanEditFixOptions));
        OnPropertyChanged(nameof(CanRollback));
        OnPropertyChanged(nameof(CanRollbackSelected));
    }

    private void SetFixMessage(string? message, bool isError)
    {
        FixMessage = message;
        IsFixMessageError = isError;
        OnPropertyChanged(nameof(HasFixMessage));
    }

    private void SetRollbackMessage(string? message, bool isError)
    {
        RollbackMessage = message;
        IsRollbackMessageError = isError;
        OnPropertyChanged(nameof(HasRollbackMessage));
    }

    private void PersistFixSettings()
    {
        if (_loadingFixSettings)
            return;

        Settings.FixDerivedHashes = FixDerivedHashes;
        Settings.FixStableTexture = FixStableTexture;
        Settings.FixRendering33 = FixRendering33;
        Settings.FixAemeathMech = FixAemeathMech;
        Settings.FixAeroMode = FixAeroMode;
        _ = SaveSettingsAsync();
    }
}