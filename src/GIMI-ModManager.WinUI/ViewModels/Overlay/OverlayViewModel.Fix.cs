using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Entities.Mods.Helpers;
using GIMI_ModManager.Core.Services.WuwaModFixer;
using GIMI_ModManager.Core.Services.WuwaUhd;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services;
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
    private bool _loadingFixSettings;
    private bool _configCheckedThisSession;
    private List<WuwaBackupGroup> _lastBackupGroups = [];
    private string? _rollbackAllowedRoot;

    /// <summary>本次修复是不是由 UHD 面板的“执行 UHD 修复”触发的；完成时把结果写回 UHD 状态行。</summary>
    private bool _uhdHashFixPending;

    // ── UHD 学习向导状态 ───────────────────────────────────────────────
    private CancellationTokenSource? _uhdLearningCts;
    private string? _uhdLearningRole;
    private ICharacterModList? _uhdLearningModList;
    private List<Guid> _uhdLearningOriginallyEnabledIds = [];
    private List<string> _uhdLearningTargetPaths = [];
    private int _uhdLearningBaselineUhd;

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

        if (IsFixTabActive)
        {
            ApplyDefaultFixSize();
            _ = EnterFixTabAsync();
        }
    }

    /// <summary>
    /// 首次进修复页自动选大档（520×660）。只做一次并落盘：
    /// 用户之后手动选小/中，再进修复页不能被重新推回大档。
    /// </summary>
    private void ApplyDefaultFixSize()
    {
        if (Settings.FixSizeInitialized)
            return;

        Settings.FixSizeInitialized = true;

        if (SelectedSizeIndex < SizeOptions.Count - 1)
            SelectedSizeIndex = SizeOptions.Count - 1;

        _ = SaveSettingsAsync();
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

    // ── UHD 贴图修复（资源档位 + 配置侧） ──────────────────────────────
    //
    // 上游 Wuwa_Mod_Fixer 没有“UHD 开关”：UHD 档位下的贴图差异靠 --derived-hashes
    // （LOD Bias High/Medium 的派生哈希）与 --stable-texture 处理；档位本身由启动参数
    // -krqlv=UHD|HD|SD 决定。这里把三件事放到用户面前：
    //   1) 记录当前档位（后续直启/注入时带上启动参数）；
    //   2) 一键用派生哈希修复贴图引用；
    //   3) 按 Rocket 的 XXMI 方式修复 Engine.ini/GameUserSettings.ini（带备份/恢复）。

    public IReadOnlyList<string> UhdTierOptions { get; } = ["自动识别", "流畅 SD", "高清 HD", "极致 UHD"];

    /// <summary>
    /// UHD 修复方式。三者的适用面不同，别默认走 RabbitFX：
    /// <list type="bullet">
    /// <item>基础哈希：普通 mod 的 hash 过期，最安全；</item>
    /// <item>稳定纹理：部分角色 + 最新 RabbitFX；</item>
    /// <item>补全状态贴图：只给依赖 RabbitFX 的状态贴图（湿身、强化 E 等）补绑定，普通 mod 加了反而可能变绿。</item>
    /// </list>
    /// </summary>
    public IReadOnlyList<string> UhdFixModeOptions { get; } =
        ["基础哈希修复", "稳定纹理修复", "补全状态贴图（RabbitFX）"];

    [ObservableProperty] private int _uhdFixModeIndex;
    [ObservableProperty] private bool _isUhdLearning;
    [ObservableProperty] private bool _useInjectionLaunch;
    private bool _loadingInjectionLaunchSetting;

    partial void OnUseInjectionLaunchChanged(bool value)
    {
        if (_loadingInjectionLaunchSetting)
            return;

        _ = SaveInjectionLaunchSettingAsync(value);
    }

    public bool CanStartUhdLearning =>
        !IsUhdLearning && !IsUhdBusy && SelectedCharacter is not null && SelectedFixTargetCount > 0;
    public bool CanFinishUhdLearning => IsUhdLearning && !IsUhdBusy;

    partial void OnIsUhdLearningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartUhdLearning));
        OnPropertyChanged(nameof(CanFinishUhdLearning));
        OnPropertyChanged(nameof(CanRunUhdActions));
        OnPropertyChanged(nameof(CanSwitchCharacter));
    }

    [ObservableProperty] private int _uhdTierIndex;
    [ObservableProperty] private bool _isUhdBusy;
    [ObservableProperty] private string? _uhdStatusMessage;
    [ObservableProperty] private bool _isUhdStatusError;

    public bool HasUhdStatus => !string.IsNullOrWhiteSpace(UhdStatusMessage);
    public bool CanRunUhdActions => !IsUhdBusy && !FixSession.IsRunning && !IsUhdLearning;
    public WuwaResourceTier SelectedUhdTier => WuwaResourceTierExtensions.FromIndex(UhdTierIndex);

    partial void OnUhdTierIndexChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 3);
        if (clamped != value)
        {
            UhdTierIndex = clamped;
            return;
        }

        if (_loadingFixSettings)
            return;

        Settings.UhdResourceTierIndex = clamped;
        _ = SaveSettingsAsync();
    }

    partial void OnIsUhdBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRunUhdActions));
        OnPropertyChanged(nameof(CanFinishUhdLearning));
        OnPropertyChanged(nameof(CanStartUhdLearning));
    }
    partial void OnUhdStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasUhdStatus));

    partial void OnUhdFixModeIndexChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, 2);
        if (clamped != value)
        {
            UhdFixModeIndex = clamped;
            return;
        }

        if (_loadingFixSettings)
            return;

        Settings.UhdFixModeIndex = clamped;
        _ = SaveSettingsAsync();
    }

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
    public bool CanSwitchCharacter => !FixSession.IsRunning && !IsUhdLearning;

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
            UhdTierIndex = Math.Clamp(Settings.UhdResourceTierIndex ?? 0, 0, 3);
            UhdFixModeIndex = Math.Clamp(Settings.UhdFixModeIndex ?? 0, 0, 2);
        }
        finally
        {
            _loadingFixSettings = false;
        }
    }

    private void InitializeFixSession()
    {
        FixSession.PropertyChanged += OnFixSessionPropertyChanged;
        _ = LoadInjectionLaunchSettingAsync();
    }

    private async Task LoadInjectionLaunchSettingAsync()
    {
        try
        {
            var settings = await _localSettingsService
                .ReadOrCreateSettingAsync<GameLaunchInjectionSettings>(GameLaunchInjectionSettings.Key,
                    SettingScope.App);
            _loadingInjectionLaunchSetting = true;
            UseInjectionLaunch = settings.Enabled;
            _loadingInjectionLaunchSetting = false;
        }
        catch (Exception e)
        {
            _loadingInjectionLaunchSetting = false;
            _logger.Debug(e, "[浮窗修复] 读取内置注入启动设置失败");
        }
    }

    private async Task SaveInjectionLaunchSettingAsync(bool enabled)
    {
        try
        {
            var settings = await _localSettingsService
                .ReadOrCreateSettingAsync<GameLaunchInjectionSettings>(GameLaunchInjectionSettings.Key,
                    SettingScope.App);
            settings.Enabled = enabled;
            await _localSettingsService.SaveSettingAsync(GameLaunchInjectionSettings.Key, settings,
                SettingScope.App);
            SetUhdStatus(enabled
                    ? "内置注入启动已开启：下次启动游戏由 JASM 挂起直启并注入 WWMI。"
                    : "内置注入启动已关闭：恢复 XXMI Launcher 启动。",
                isError: false);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 保存内置注入启动设置失败");
            SetUhdStatus("保存内置注入启动设置失败：" + e.Message, isError: true);
        }
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
            OnPropertyChanged(nameof(CanRunUhdActions));
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
        _ = RefreshUhdStatusAsync();

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

    // ── 命令：UHD 贴图修复 ────────────────────────────────────────────

    /// <summary>
    /// 执行 UHD 修复，按 <see cref="UhdFixModeIndex"/> 三选一：
    /// 基础哈希（默认，普通 mod）/ 稳定纹理（部分角色 + 最新 RabbitFX）/
    /// 补全状态贴图（仅依赖 RabbitFX 的状态贴图）。
    /// 与“应用稳定纹理”互斥的规则由 <see cref="WuwaFixOptions.Normalize"/> 兜底。
    /// </summary>
    [RelayCommand]
    private async Task RunUhdHashFixAsync()
    {
        if (!CanRunUhdActions)
            return;

        if (!IsEngineReady)
        {
            SetUhdStatus("修复引擎未就绪，请先在修复页等待引擎初始化完成。", isError: true);
            return;
        }

        if (!CanStartFix)
        {
            SetUhdStatus("请先在列表里勾选要修复的 Mod（至少一个）。", isError: true);
            return;
        }

        var mode = Math.Clamp(UhdFixModeIndex, 0, 2);
        FixStableTexture = mode == 1;
        FixDerivedHashes = mode == 2;

        _uhdHashFixPending = true;
        var modeLabel = mode switch
        {
            1 => "稳定纹理",
            2 => "状态贴图补全（RabbitFX）",
            _ => "基础哈希"
        };
        SetUhdStatus($"正在用{modeLabel}修复贴图引用…", isError: false);
        try
        {
            await StartFixAsync();
        }
        catch (Exception e)
        {
            _uhdHashFixPending = false;
            _logger.Warning(e, "[浮窗修复] UHD 哈希修复启动失败");
            SetUhdStatus("启动修复失败：" + e.Message, isError: true);
        }
    }

    /// <summary>按 Rocket 的 XXMI 方式写入 Engine.ini / GameUserSettings.ini（先备份）。</summary>
    [RelayCommand]
    private async Task ApplyUhdConfigAsync()
    {
        if (!CanRunUhdActions)
            return;

        IsUhdBusy = true;
        SetUhdStatus("正在写入 Engine.ini / GameUserSettings.ini…", isError: false);
        try
        {
            var result = await _uhdRepairService.ApplyAsync();
            SetUhdStatus(result.Message, isError: !result.Success);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] UHD 配置修复失败");
            SetUhdStatus("写入配置失败：" + e.Message, isError: true);
        }
        finally
        {
            IsUhdBusy = false;
        }
    }

    /// <summary>恢复最近一次 UHD 配置修复的备份。</summary>
    [RelayCommand]
    private async Task RestoreUhdConfigAsync()
    {
        if (!CanRunUhdActions)
            return;

        IsUhdBusy = true;
        SetUhdStatus("正在恢复配置备份…", isError: false);
        try
        {
            var result = await _uhdRepairService.RestoreLatestAsync();
            SetUhdStatus(result.Message, isError: !result.Success);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 恢复 UHD 配置失败");
            SetUhdStatus("恢复配置失败：" + e.Message, isError: true);
        }
        finally
        {
            IsUhdBusy = false;
        }
    }

    /// <summary>
    /// 从 Rocket RoleHash 学习库导入 UHD→HD 映射，为所选 Mod 生成
    /// <c>[TextureOverrideTexture*_UHD_*]</c> 段。需要先用新版 Rocket 在 UHD 下学习过该角色。
    /// </summary>
    [RelayCommand]
    private async Task ImportUhdAliasesAsync()
    {
        if (!CanRunUhdActions)
            return;

        if (SelectedCharacter is null)
        {
            SetUhdStatus("请先选择角色。", isError: true);
            return;
        }

        var targets = FixTargets
            .Where(item => item.IsSelected)
            .Select(item => item.ModPath)
            .ToList();

        if (targets.Count == 0)
        {
            SetUhdStatus("请先在列表里勾选要修复的 Mod（至少一个）。", isError: true);
            return;
        }

        IsUhdBusy = true;
        SetUhdStatus("正在读取 Rocket 学习库并生成 UHD 覆盖段…", isError: false);
        try
        {
            var result = await _uhdAliasImportService.ApplyAsync(SelectedCharacter.DisplayName, targets);
            SetUhdStatus(result.Message, isError: !result.Success);

            if (result.Success)
                await RefreshCoordinator.RequestRefreshAsync();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 导入 Rocket UHD 映射失败");
            SetUhdStatus("导入 UHD 映射失败：" + e.Message, isError: true);
        }
        finally
        {
            IsUhdBusy = false;
        }
    }

    /// <summary>
    /// 从 Rocket 的 <c>Lod0_texture.dll</c> 读取**内嵌的官方 HD→UHD 映射表**并合并进学习库。
    /// 这份表覆盖 Rocket 支持的角色（实测 2,110 条 / 18 个角色），不需要运行 Rocket；
    /// 与本地 aliases、JASM 自学习结果合并使用。
    /// </summary>
    [RelayCommand]
    private async Task ImportRocketBuiltinAsync()
    {
        if (!CanRunUhdActions)
            return;

        IsUhdBusy = true;
        SetUhdStatus("正在读取 Rocket 内置映射…", isError: false);
        try
        {
            var path = Settings.RocketLod0TextureDllPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".dll");
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await picker.PickSingleFileAsync();
                if (file is null)
                {
                    SetUhdStatus("已取消。", isError: true);
                    return;
                }

                path = file.Path;
                Settings.RocketLod0TextureDllPath = path;
                _ = SaveSettingsAsync();
            }

            var aliases = await Task.Run(() => RocketBuiltinAliasExtractor.ExtractFromDll(path))
                .ConfigureAwait(true);
            if (aliases.Count == 0)
            {
                SetUhdStatus("这个 DLL 里没有找到内置映射（请确认选的是新版 Lod0_texture.dll）。",
                    isError: true);
                return;
            }

            var added = RocketBuiltinAliasExtractor.MergeIntoLibrary(aliases);
            var preview = string.Join("、",
                aliases.Take(8).Select(item => $"{item.Role}({item.Pairs.Count})"));
            SetUhdStatus($"已导入 {aliases.Count} 个角色、新增 {added} 条映射：{preview}…",
                isError: false);

            await RefreshCoordinator.RequestRefreshAsync();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[浮窗修复] 导入 Rocket 内置映射失败");
            SetUhdStatus("导入 Rocket 内置映射失败：" + e.Message, isError: true);
        }
        finally
        {
            IsUhdBusy = false;
        }
    }

    // ── 命令：JASM 自研 UHD 学习（不依赖 Rocket）────────────────────────
    //
    // 一键流程：HD 启动 → 用户让角色出现并退出 → 自动换 UHD 再启动 → 再退出 →
    // 配对两份 learn.tsv → 合并学习库 → 为所选 Mod 生成 _UHD_ 段 → 恢复现场。
    //
    // 原生 DLL 在 learn=1 时只观察、不替换；配对使用 (IB hash, StartIndexLocation, 槽位)，
    // 与 Rocket 的数据语义一致（实测与内置表重叠）。

    private enum SelfLearnPhase
    {
        None,
        Hd,
        Uhd,
        Finishing,
    }

    private SelfLearnPhase _selfLearnPhase = SelfLearnPhase.None;
    private CancellationTokenSource? _selfLearnCts;
    private string? _selfLearnRole;
    private ICharacterModList? _selfLearnModList;
    private List<Guid> _selfLearnOriginallyEnabledIds = [];
    private List<string> _selfLearnTargetPaths = [];
    private int? _selfLearnOriginalTier;
    private int? _selfLearnPid;

    [ObservableProperty] private bool _isSelfLearning;
    [ObservableProperty] private string? _selfLearnStatus;
    [ObservableProperty] private int _selfLearnSamples;

    public bool HasSelfLearnStatus => !string.IsNullOrWhiteSpace(SelfLearnStatus);

    public bool CanStartSelfLearning => CanRunUhdActions && !IsSelfLearning;

    partial void OnIsSelfLearningChanged(bool value) => OnPropertyChanged(nameof(CanStartSelfLearning));

    partial void OnSelfLearnStatusChanged(string? value) => OnPropertyChanged(nameof(HasSelfLearnStatus));

    [RelayCommand]
    private async Task StartSelfLearningAsync()
    {
        if (!CanStartSelfLearning)
            return;

        if (SelectedCharacter is null)
        {
            SetUhdStatus("请先选择角色。", isError: true);
            return;
        }

        if (!UseInjectionLaunch)
        {
            SetUhdStatus("自研学习需要「内置注入启动」：请先打开上面的开关（学习依赖注入的映射 DLL 观察）。",
                isError: true);
            return;
        }

        if (IsGameRunning())
        {
            SetUhdStatus("请先完全退出游戏再开始学习 —— 向导要切换档位并启动两次游戏。", isError: true);
            return;
        }

        var modList = ResolveModList(SelectedCharacter.Character);
        if (modList is null)
        {
            SetUhdStatus("当前角色没有可用的 Mod 列表。", isError: true);
            return;
        }

        var selectedTargets = FixTargets.Where(item => item.IsSelected).ToList();
        if (selectedTargets.Count == 0)
        {
            SetUhdStatus("请先勾选要学习的 Mod（建议一次只勾一个，避免多个 Mod 抢同一个槽位）。",
                isError: true);
            return;
        }

        _selfLearnRole = SelectedCharacter.DisplayName;
        _selfLearnModList = modList;
        _selfLearnOriginallyEnabledIds = modList.Mods
            .Where(entry => entry.IsEnabled)
            .Select(entry => entry.Id)
            .ToList();
        _selfLearnTargetPaths = selectedTargets
            .Select(item => item.ModPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _selfLearnOriginalTier = Settings.UhdResourceTierIndex;

        var selectedIds = selectedTargets.Select(item => item.Id).ToHashSet();

        try
        {
            // 一次只留所选 Mod，避免其它 Mod 的贴图混进观察。
            await Task.Run(() =>
            {
                foreach (var entry in modList.Mods)
                {
                    var shouldBeEnabled = selectedIds.Contains(entry.Id);
                    if (shouldBeEnabled && !entry.IsEnabled)
                        modList.EnableMod(entry.Id);
                    else if (!shouldBeEnabled && entry.IsEnabled)
                        modList.DisableMod(entry.Id);
                }
            }).ConfigureAwait(true);

            // HD 档位 + learn 模式：DLL 只记录观察，不替换任何贴图。
            Settings.UhdResourceTierIndex = 2;
            await SaveSettingsAsync();
            UhdMapperConfig.SetLearnMode(true);
            UhdMapperConfig.ClearLearnFile();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[UHD自学习] 准备阶段失败");
            RestoreSelfLearningState();
            SetUhdStatus("准备学习失败：" + e.Message, isError: true);
            return;
        }

        IsSelfLearning = true;
        SelfLearnSamples = 0;
        _selfLearnPhase = SelfLearnPhase.Hd;
        SelfLearnStatus = "正在以 HD 启动游戏…";

        var launch = await _gameLaunchInjectionService.TryLaunchAsync().ConfigureAwait(true);
        if (!launch.Success)
        {
            RestoreSelfLearningState();
            IsSelfLearning = false;
            _selfLearnPhase = SelfLearnPhase.None;
            SetUhdStatus("启动学习失败：" + launch.Error + "（学习已取消）", isError: true);
            return;
        }

        _selfLearnPid = launch.ProcessId;
        _selfLearnCts = new CancellationTokenSource();
        _ = RunSelfLearnLoopAsync(_selfLearnCts.Token);
    }

    [RelayCommand]
    private void CancelSelfLearning()
    {
        if (!IsSelfLearning)
            return;

        StopSelfLearnLoop();
        IsSelfLearning = false;
        _selfLearnPhase = SelfLearnPhase.None;
        RestoreSelfLearningState();
        SetUhdStatus("已取消学习；Mod 启用状态、档位与映射模式已恢复（已启动的游戏不会被关闭）。",
            isError: false);
    }

    private async Task RunSelfLearnLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(true);

                SelfLearnSamples = UhdMapperConfig.CountLearnSamples();

                if (IsPidRunning(_selfLearnPid) || IsGameRunning())
                {
                    SelfLearnStatus = _selfLearnPhase == SelfLearnPhase.Hd
                        ? $"HD 观察中：{SelfLearnSamples} 条采样。让角色出现在画面里，然后退出游戏，向导会自动继续。"
                        : $"UHD 观察中：{SelfLearnSamples} 条采样。同样让角色出现，然后退出游戏即可完成。";
                    continue;
                }

                if (_selfLearnPhase == SelfLearnPhase.Hd)
                {
                    if (SelfLearnSamples < 50)
                    {
                        await FailSelfLearningAsync(
                            $"HD 观察样本只有 {SelfLearnSamples} 条，角色可能没出现在画面里。学习已取消。");
                        return;
                    }

                    UhdMapperConfig.ArchiveLearnFile(UhdMapperConfig.HdLearnPath);
                    UhdMapperConfig.ClearLearnFile();
                    Settings.UhdResourceTierIndex = 3;
                    await SaveSettingsAsync();
                    _selfLearnPhase = SelfLearnPhase.Uhd;
                    SelfLearnStatus = "HD 完成，正在以 UHD 启动游戏…";
                    SetUhdStatus("HD 观察完成，已切换到 UHD。", isError: false);

                    var launch = await _gameLaunchInjectionService.TryLaunchAsync().ConfigureAwait(true);
                    if (!launch.Success)
                    {
                        await FailSelfLearningAsync("UHD 启动失败：" + launch.Error);
                        return;
                    }

                    _selfLearnPid = launch.ProcessId;
                    continue;
                }

                if (_selfLearnPhase == SelfLearnPhase.Uhd)
                {
                    if (SelfLearnSamples < 50)
                    {
                        await FailSelfLearningAsync(
                            $"UHD 观察样本只有 {SelfLearnSamples} 条，角色可能没出现在画面里。学习已取消。");
                        return;
                    }

                    UhdMapperConfig.ArchiveLearnFile(UhdMapperConfig.UhdLearnPath);
                    _selfLearnPhase = SelfLearnPhase.Finishing;
                    SelfLearnStatus = "两次观察完成，正在配对并生成映射…";
                    IsSelfLearning = false;
                    await FinishSelfLearningAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 用户取消
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[UHD自学习] 监听循环失败");
            await FailSelfLearningAsync("学习失败：" + e.Message);
        }
    }

    private async Task FinishSelfLearningAsync()
    {
        var role = _selfLearnRole;
        var targetPaths = _selfLearnTargetPaths;

        try
        {
            SetUhdStatus("正在配对 HD/UHD 观察…", isError: false);
            var pairs = await Task.Run(() =>
                UhdLearningStore.JoinFiles(UhdMapperConfig.HdLearnPath, UhdMapperConfig.UhdLearnPath))
                .ConfigureAwait(true);

            if (pairs.Count == 0)
            {
                SetUhdStatus("没有配对出任何 HD→UHD 映射 —— 两次观察里角色是否都在画面中出现过？",
                    isError: true);
                return;
            }

            var added = UhdLearningStore.Merge(role ?? string.Empty, pairs);
            var import = await _uhdAliasImportService.ApplyAsync(role ?? string.Empty, targetPaths)
                .ConfigureAwait(true);

            SetUhdStatus($"学习完成：配对 {pairs.Count} 条（学习库新增 {added} 条）。{import.Message}",
                isError: !import.Success);

            if (import.Success)
                await RefreshCoordinator.RequestRefreshAsync();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[UHD自学习] 配对/生成失败");
            SetUhdStatus("学习完成但生成映射失败：" + e.Message, isError: true);
        }
        finally
        {
            RestoreSelfLearningState();
        }
    }

    private async Task FailSelfLearningAsync(string message)
    {
        StopSelfLearnLoop();
        IsSelfLearning = false;
        _selfLearnPhase = SelfLearnPhase.None;
        RestoreSelfLearningState();
        SetUhdStatus(message, isError: true);
        await Task.CompletedTask;
    }

    private void StopSelfLearnLoop()
    {
        try
        {
            _selfLearnCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _selfLearnCts?.Dispose();
        _selfLearnCts = null;
    }

    /// <summary>恢复进入学习前的 Mod 启用状态、档位与映射模式（learn 关闭）。可重复调用。</summary>
    private void RestoreSelfLearningState()
    {
        if (_selfLearnModList is { } modList)
        {
            try
            {
                foreach (var entry in modList.Mods)
                {
                    var shouldBeEnabled = _selfLearnOriginallyEnabledIds.Contains(entry.Id);
                    if (shouldBeEnabled && !entry.IsEnabled)
                        modList.EnableMod(entry.Id);
                    else if (!shouldBeEnabled && entry.IsEnabled)
                        modList.DisableMod(entry.Id);
                }
            }
            catch (Exception e)
            {
                _logger.Warning(e, "[UHD自学习] 恢复 Mod 启用状态失败");
            }
        }

        try
        {
            if (_selfLearnOriginalTier is not null)
            {
                Settings.UhdResourceTierIndex = _selfLearnOriginalTier;
                _ = SaveSettingsAsync();
            }
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[UHD自学习] 恢复档位失败");
        }

        try
        {
            UhdMapperConfig.SetLearnMode(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(e, "[UHD自学习] 关闭 learn 模式失败");
        }

        UhdMapperConfig.ClearLearnFile();
        _selfLearnModList = null;
        _selfLearnOriginallyEnabledIds = [];
        _selfLearnTargetPaths = [];
        _selfLearnRole = null;
        _selfLearnPid = null;
        _selfLearnOriginalTier = null;
    }

    /// <summary>
    /// 「游戏是否还在跑」只认 UE 的 shipping 进程：启动器/XXMI 窗口关不关不代表游戏退出了，
    /// 把它们算进来会让向导永远等不到「本遍结束」。
    /// </summary>
    private static bool IsGameRunning() =>
        Process.GetProcessesByName("Client-Win64-Shipping").Length > 0;

    private static bool IsPidRunning(int? pid)
    {
        if (pid is null)
            return false;

        try
        {
            using var process = Process.GetProcessById(pid.Value);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ── 命令：UHD 学习向导 ─────────────────────────────────────────────
    //
    // 流程：只启用所选 Mod（其余停用）→ 启动新版 Rocket → 用户在 UHD 下展示角色，
    // RoleHash 学习该 mod 的 HD/UHD 配对 → 退出游戏/Rocket → 完成：恢复原启用状态、导入 aliases、F10。
    //
    // 注意：这里必须让**目标 mod 处于启用状态**再学。原版角色的学习只能拿到游戏原生 hash，
    // 这些 mod 用的是启用时才绑定的 HD hash，原版学习配对不出来。

    [RelayCommand]
    private async Task StartUhdLearningAsync()
    {
        if (!CanRunUhdActions)
            return;

        if (SelectedCharacter is null)
        {
            SetUhdStatus("请先选择角色。", isError: true);
            return;
        }

        var modList = ResolveModList(SelectedCharacter.Character);
        if (modList is null)
        {
            SetUhdStatus("当前角色没有可用的 Mod 列表。", isError: true);
            return;
        }

        var selectedTargets = FixTargets.Where(item => item.IsSelected).ToList();
        if (selectedTargets.Count == 0)
        {
            SetUhdStatus("请先勾选要学习的 Mod（建议一次只勾一个，避免多个 mod 抢同一个槽位）。", isError: true);
            return;
        }

        var roleName = SelectedCharacter.DisplayName;
        var rocket = await Task.Run(() => RocketLearningCatalog.FindUpdatedRocket()).ConfigureAwait(true);
        if (rocket is null)
        {
            SetUhdStatus("没有找到新版 Rocket（Lod0_texture.dll ≥ 5 MB）。请把更新后的 Rocket 放到常见目录，或手动启动它学习后直接点「导入 Rocket UHD 映射」。",
                isError: true);
            return;
        }

        var entries = modList.Mods.ToList();
        var originallyEnabledIds = entries
            .Where(entry => entry.IsEnabled)
            .Select(entry => entry.Id)
            .ToList();
        var selectedIds = selectedTargets.Select(item => item.Id).ToHashSet();

        _uhdLearningRole = roleName;
        _uhdLearningModList = modList;
        _uhdLearningOriginallyEnabledIds = originallyEnabledIds;
        _uhdLearningTargetPaths = selectedTargets
            .Select(item => item.ModPath)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        IsUhdBusy = true;
        try
        {
            SetUhdStatus($"正在切换为只启用所选的 {selectedTargets.Count} 个 Mod（其余停用）…", isError: false);
            await Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    var shouldBeEnabled = selectedIds.Contains(entry.Id);
                    if (shouldBeEnabled && !entry.IsEnabled)
                        modList.EnableMod(entry.Id);
                    else if (!shouldBeEnabled && entry.IsEnabled)
                        modList.DisableMod(entry.Id);
                }
            }).ConfigureAwait(true);

            var baseline = RocketLearningCatalog.ReadStatus(roleName)?.UhdReady ?? 0;
            _uhdLearningBaselineUhd = baseline;

            var alreadyRunning = Process.GetProcessesByName("Rocket").Length > 0;
            if (!alreadyRunning)
            {
                Process.Start(new ProcessStartInfo(rocket)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WorkingDirectory = Path.GetDirectoryName(rocket) ?? string.Empty
                });
            }

            IsUhdLearning = true;
            SetUhdStatus(
                (alreadyRunning ? "Rocket 已在运行。" : "已启动 Rocket。")
                + $"请在 Rocket 里以 UHD 启动游戏，让「{roleName}」穿着所选 Mod 出现在画面中并保持一会儿。"
                + "退出游戏和 Rocket 后点「完成学习并导入」。",
                isError: false);

            _uhdLearningCts = new CancellationTokenSource();
            _ = PollUhdLearningAsync(roleName, baseline, _uhdLearningCts.Token);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223)
        {
            RestoreUhdLearningMods();
            SetUhdStatus("已取消 UAC，学习没有开始；Mod 已恢复。", isError: true);
            ClearUhdLearningState();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[UHD学习] 启动失败");
            RestoreUhdLearningMods();
            SetUhdStatus("启动学习向导失败，Mod 已恢复：" + e.Message, isError: true);
            ClearUhdLearningState();
        }
        finally
        {
            IsUhdBusy = false;
        }
    }

    [RelayCommand]
    private async Task FinishUhdLearningAsync()
    {
        if (!IsUhdLearning)
            return;

        if (Process.GetProcessesByName("Client-Win64-Shipping").Length > 0
            || Process.GetProcessesByName("Wuthering Waves").Length > 0)
        {
            SetUhdStatus("请先完全退出游戏和 Rocket，再点「完成学习并导入」——否则 Rocket 的 DLL 会继续钉住旧贴图。",
                isError: true);
            return;
        }

        IsUhdBusy = true;
        StopUhdLearningPolling();
        var roleName = _uhdLearningRole;
        var targetPaths = _uhdLearningTargetPaths;
        RestoreUhdLearningMods();
        IsUhdLearning = false;

        try
        {
            if (string.IsNullOrWhiteSpace(roleName) || targetPaths.Count == 0)
            {
                SetUhdStatus("学习结束，但缺少角色或 Mod 路径，未导入映射。", isError: true);
                return;
            }

            SetUhdStatus("正在导入 Rocket UHD 映射…", isError: false);
            var result = await _uhdAliasImportService.ApplyAsync(roleName, targetPaths);
            SetUhdStatus(result.Message + "（Mod 已恢复）", isError: !result.Success);

            if (result.Success)
                await RefreshCoordinator.RequestRefreshAsync();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[UHD学习] 导入映射失败");
            SetUhdStatus("导入 UHD 映射失败：" + e.Message, isError: true);
        }
        finally
        {
            IsUhdBusy = false;
            ClearUhdLearningState();
        }
    }

    [RelayCommand]
    private void CancelUhdLearning()
    {
        if (!IsUhdLearning)
            return;

        StopUhdLearningPolling();
        RestoreUhdLearningMods();
        IsUhdLearning = false;
        ClearUhdLearningState();
        SetUhdStatus("已取消学习，Mod 已恢复。", isError: false);
    }

    private async Task PollUhdLearningAsync(string roleName, int baseline, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(true);

                var status = RocketLearningCatalog.ReadStatus(roleName);
                if (status is null)
                {
                    SetUhdStatus($"等待 Rocket 学习库…请确认游戏已启动、原版「{roleName}」在画面中。", isError: false);
                    continue;
                }

                var delta = status.UhdReady - baseline;
                var progress = delta > 0 ? $"已新增 {delta} 条 UHD" : "尚未新增 UHD";
                SetUhdStatus(
                    $"学习中：{roleName} uhd_ready={status.UhdReady}（{progress}）。"
                    + "让角色保持在画面中；退出游戏和 Rocket 后点「完成学习并导入」。",
                    isError: false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception e)
        {
            _logger.Debug(e, "[UHD学习] 轮询学习状态失败");
        }
    }

    private void StopUhdLearningPolling()
    {
        try
        {
            _uhdLearningCts?.Cancel();
            _uhdLearningCts?.Dispose();
        }
        catch
        {
            // 取消本身失败不影响后续恢复
        }

        _uhdLearningCts = null;
    }

    private void RestoreUhdLearningMods()
    {
        if (_uhdLearningModList is null)
            return;

        var originallyEnabled = _uhdLearningOriginallyEnabledIds.ToHashSet();
        foreach (var entry in _uhdLearningModList.Mods.ToList())
        {
            var shouldBeEnabled = originallyEnabled.Contains(entry.Id);
            try
            {
                if (shouldBeEnabled && !entry.IsEnabled)
                    _uhdLearningModList.EnableMod(entry.Id);
                else if (!shouldBeEnabled && entry.IsEnabled)
                    _uhdLearningModList.DisableMod(entry.Id);
            }
            catch (Exception e)
            {
                _logger.Warning(e, "[UHD学习] 恢复 Mod {Id} 失败", entry.Id);
            }
        }

        _uhdLearningOriginallyEnabledIds = [];
        RefreshFixTargets();
    }

    private void ClearUhdLearningState()
    {
        _uhdLearningRole = null;
        _uhdLearningModList = null;
        _uhdLearningOriginallyEnabledIds = [];
        _uhdLearningTargetPaths = [];
        _uhdLearningBaselineUhd = 0;
    }

    /// <summary>刷新“当前配置是否已应用”的状态行；进修复页时调用一次。</summary>
    [RelayCommand]
    private async Task RefreshUhdStatusAsync()
    {
        if (IsUhdBusy)
            return;

        try
        {
            // 档位：自动模式下按已安装目录推断；多档位并存时要求用户按官方启动器选择。
            var detection = await _uhdRepairService.DetectTierAsync();
            var tierNote = detection.Kind switch
            {
                WuwaTierDetectionKind.Detected when UhdTierIndex == 0 && detection.Tier is { } detected =>
                    AutoPickTier(detected),
                WuwaTierDetectionKind.Detected when detection.Tier is { } detected &&
                                                     detection.Tier != SelectedUhdTier =>
                    $"注意：已安装档位是 {detected.ToDisplayName()}，与当前选择 {SelectedUhdTier.ToDisplayName()} 不一致，请与官方启动器保持一致。 ",
                WuwaTierDetectionKind.Ambiguous =>
                    $"检测到多个已安装档位（{string.Join("、", detection.Installed.Select(t => t.ToDisplayName()))}），"
                    + "请按官方启动器当前选择手动指定。 ",
                WuwaTierDetectionKind.NotInstalled => "未检测到已安装的资源档位目录，请手动选择。 ",
                WuwaTierDetectionKind.NotFound => "未能定位 Client\\Content，无法自动检测档位，请手动选择。 ",
                _ => string.Empty
            };

            var paths = await _uhdRepairService.ResolveConfigPathsAsync();
            if (paths is null)
            {
                SetUhdStatus(tierNote + "未找到游戏配置目录（Client\\Saved\\Config\\WindowsNoEditor），请确认游戏路径。",
                    isError: true);
                return;
            }

            var applied = await _uhdRepairService.IsAppliedAsync();
            var configNote = applied
                ? "配置修复：已应用。若仍有贴图错误，请再执行「执行 UHD 修复」。"
                : "配置修复：未应用。UHD 档位建议先应用配置，再做哈希修复。";

            SetUhdStatus(tierNote + configNote, isError: false);
        }
        catch (Exception e)
        {
            _logger.Debug(e, "[浮窗修复] 读取 UHD 配置状态失败");
            SetUhdStatus("读取配置状态失败：" + e.Message, isError: true);
        }
    }

    /// <summary>自动模式识别到唯一档位时落到设置里；返回一句给状态行用的说明。</summary>
    private string AutoPickTier(WuwaResourceTier tier)
    {
        UhdTierIndex = tier.ToIndex();
        return $"已自动识别资源档位：{tier.ToDisplayName()}。 ";
    }

    private void SetUhdStatus(string? message, bool isError)
    {
        UhdStatusMessage = message;
        IsUhdStatusError = isError;
    }

    private async void OnFixRunCompleted(object? sender, WuwaFixRunSummary summary)
    {
        try
        {
            SetFixMessage(summary.ToSummaryText(), isError: summary.Failed > 0);
            OnPropertyChanged(nameof(CanStartFix));
            OnPropertyChanged(nameof(CanEditFixOptions));
            OnPropertyChanged(nameof(CanRunUhdActions));

            if (_uhdHashFixPending)
            {
                _uhdHashFixPending = false;
                SetUhdStatus(summary.ToSummaryText(), isError: summary.Failed > 0);
            }

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
        OnPropertyChanged(nameof(CanStartUhdLearning));
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