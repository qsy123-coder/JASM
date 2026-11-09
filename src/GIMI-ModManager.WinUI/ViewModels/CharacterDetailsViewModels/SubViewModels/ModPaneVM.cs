using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkitWrapper;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Entities;
using GIMI_ModManager.Core.Entities.Mods.Contract;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services.OilGloss;
using GIMI_ModManager.WinUI.Services;
using GIMI_ModManager.WinUI.Services.AppManagement;
using GIMI_ModManager.WinUI.Models.Settings;
using GIMI_ModManager.WinUI.Services.ModHandling;
using GIMI_ModManager.WinUI.Services.Notifications;
using GIMI_ModManager.WinUI.Services.Overlay;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Serilog;

namespace GIMI_ModManager.WinUI.ViewModels.CharacterDetailsViewModels.SubViewModels;

public sealed partial class ModPaneVM(
    ISkinManagerService skinManagerService,
    NotificationManager notificationService,
    ModSettingsService modSettingsService,
    ImageHandlerService imageHandlerService,
    InGameMenuService inGameMenuService,
    OilGlossService oilGlossService)
    : ObservableRecipient, IRecipient<ModChangedMessage>
{
    private readonly ILogger _logger = Log.ForContext<ModPaneVM>();
    private readonly ISkinManagerService _skinManagerService = skinManagerService;
    private readonly NotificationManager _notificationService = notificationService;
    private readonly ModSettingsService _modSettingsService = modSettingsService;
    private readonly ImageHandlerService _imageHandlerService = imageHandlerService;
    private readonly InGameMenuService _inGameMenuService = inGameMenuService;
    private readonly OilGlossService _oilGlossService = oilGlossService;
    private readonly ILanguageLocalizer _localizer = App.GetService<ILanguageLocalizer>();

    private readonly AsyncLock _loadModLock = new();
    private CancellationToken _cancellationToken = new();
    private DispatcherQueue _dispatcherQueue = null!;
    public bool IsInitialized { get; private set; }
    public BusySetter BusySetter { get; set; } = null!;


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotReadOnly))]
    private bool _isReadOnly = true;

    [ObservableProperty] private bool _isEditingModName;

    public bool IsNotReadOnly => !IsReadOnly;

    /// <summary>当前 Mod 的游戏内菜单状态文案（空 = 不显示）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInGameMenuStatus))]
    private string _inGameMenuStatusText = string.Empty;

    [ObservableProperty] private bool _isInGameMenuGenerated;
    [ObservableProperty] private bool _isInGameMenuBusy;

    public bool HasInGameMenuStatus => !string.IsNullOrEmpty(InGameMenuStatusText);

    /// <summary>当前游戏是否支持油光（仅鸣潮）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleOilGloss))]
    private bool _isOilGlossSupported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleOilGloss))]
    [NotifyPropertyChangedFor(nameof(OilGlossToggleLabel))]
    [NotifyCanExecuteChangedFor(nameof(ToggleOilGlossCommand))]
    private bool _isOilGlossApplied;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleOilGlossCommand))]
    private bool _isOilGlossBusy;

    /// <summary>油光状态文案；空 = 不显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOilGlossStatus))]
    private string _oilGlossStatusText = string.Empty;

    public bool HasOilGlossStatus => !string.IsNullOrEmpty(OilGlossStatusText);

    /// <summary>按钮区域是否显示（仅支持的游戏 + 已加载 Mod）。</summary>
    public bool CanToggleOilGloss => IsOilGlossSupported && IsModLoaded;

    /// <summary>详情页唯一的油光按钮：按当前状态在「开启 / 撤销」之间切换。</summary>
    public string OilGlossToggleLabel => IsOilGlossApplied ? "撤销油光" : "开启油光";

    private Guid? _loadedModId;
    private CharacterSkinEntry? _loadedMod;

    [MemberNotNullWhen(true, nameof(_loadedModId), nameof(_loadedMod))]
    public bool IsModLoaded => _loadedModId != null && ModModel.IsLoaded && _loadedMod != null;

    [ObservableProperty] private ModPaneFieldsVm _modModel = new();


    public bool QueueLoadMod(Guid? modId, bool force = false) => _channel.Writer.TryWrite(new LoadModMessage { ModId = modId, Force = force });


    private readonly Channel<LoadModMessage> _channel = Channel.CreateBounded<LoadModMessage>(
        new BoundedChannelOptions(1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private async Task ModLoaderLoopAsync()
    {
        // Runs on the UI thread
        await foreach (var loadModMessage in _channel.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (_cancellationToken.IsCancellationRequested)
                break;
            using var _ = await LockAsync().ConfigureAwait(false);
            IsReadOnly = true;
            IsEditingModName = false;
            try
            {
                if (loadModMessage.ModId is null)
                {
                    await UnloadModAsync();
                    NotifyAllCommands();
                    OnPropertyChanged(nameof(IsModLoaded));
                    continue;
                }

                await LoadModAsync(loadModMessage.ModId.Value, loadModMessage.Force);
                NotifyAllCommands();
                OnPropertyChanged(nameof(IsModLoaded));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _notificationService.ShowNotification(_localizer.GetLocalizedStringOrDefault("ModPaneVM_ErrorLoadingModTitle", defaultValue: "Error loading mod"), e.Message, null);
            }
        }
    }

    private async Task LoadModAsync(Guid modId, bool force)
    {
        if (modId == _loadedModId && force == false)
            return;

        var modPaneData = await Task.Run(async () =>
        {
            var modEntry = _skinManagerService.GetModEntryById(modId);
            if (modEntry == null)
                return null;

            var mod = modEntry.Mod;

            var modSettings =
                await mod.Settings.TryReadSettingsAsync(useCache: false, cancellationToken: _cancellationToken);

            if (modSettings is null)
                return null;

            ICollection<KeySwapSection>? keySwaps = null;
            try
            {
                if (mod.KeySwaps is not null)
                    keySwaps = (await mod.KeySwaps.ReadKeySwapConfiguration(_cancellationToken)).ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _notificationService.ShowNotification(string.Format(_localizer.GetLocalizedStringOrDefault("ModPaneVM_FailedToLoadKeyswapsTitle", defaultValue: "Failed to load keyswaps for mod {0}"), mod.GetDisplayName()), e.Message, null);
            }

            // 扫描所有 ini 文件提取按键绑定
            var keyBindingGroups = Core.Entities.Mods.Helpers.ModIniKeyBindingParser.ParseAllKeyBindings(mod.FullPath);
            _logger.Information("[ModPaneVM] 按键绑定解析完成: {Groups} 个文件, {Total} 条绑定",
                keyBindingGroups.Count, keyBindingGroups.Sum(g => g.Bindings.Count));
            foreach (var g in keyBindingGroups)
            foreach (var b in g.Bindings)
                _logger.Information("[ModPaneVM]   [{File}] {Section} → '{Key}' (arrow={Arrow})",
                    g.IniFileRelativePath, b.SectionName, b.KeyValue, b.IsArrowKey);

            return new { modEntry, modSettings, keySwaps, keyBindingGroups };
        }, _cancellationToken);

        if (modPaneData is null)
            return;


        _loadedMod = modPaneData.modEntry;
        ModModel = ModPaneFieldsVm.FromModEntry(modPaneData.modEntry, modPaneData.modSettings,
            modPaneData.keySwaps ?? [], modPaneData.keyBindingGroups);
        ModModel.PropertyChanged += ModModel_PropertyChanged;
        _loadedModId = modId;
        IsReadOnly = false;
        RefreshInGameMenuStatus();
        RefreshOilGlossStatus();
    }

    private void ModModel_PropertyChanged(object? sender, PropertyChangedEventArgs e) => SaveModSettingsCommand.NotifyCanExecuteChanged();
    private void BusySetter_HardBusyChanged(object? sender, EventArgs eventArgs) => NotifyAllCommands();

    private Task UnloadModAsync()
    {
        _loadedModId = null;
        _loadedMod = null;
        IsInGameMenuGenerated = false;
        InGameMenuStatusText = string.Empty;
        IsOilGlossApplied = false;
        OilGlossStatusText = string.Empty;
        if (ModModel.IsLoaded)
            ModModel.PropertyChanged -= ModModel_PropertyChanged;
        ModModel = new ModPaneFieldsVm();
        return Task.CompletedTask;
    }

    /// <summary>刷新「详情页 → 游戏内菜单」的状态。Mod 未加载时清空。</summary>
    private void RefreshInGameMenuStatus()
    {
        if (!IsModLoaded)
        {
            IsInGameMenuGenerated = false;
            InGameMenuStatusText = string.Empty;
            return;
        }

        IsInGameMenuGenerated = _inGameMenuService.HasGeneratedMenu(_loadedMod.Mod);
        InGameMenuStatusText = IsInGameMenuGenerated ? "已生成游戏内菜单（F7 打开，↑/↓ 选择，F9 切换）" : string.Empty;
    }

    /// <summary>刷新「详情页 → 油光」的状态。只有鸣潮显示入口。</summary>
    private void RefreshOilGlossStatus()
    {
        if (!IsModLoaded)
        {
            IsOilGlossSupported = false;
            IsOilGlossApplied = false;
            OilGlossStatusText = string.Empty;
            OnPropertyChanged(nameof(CanToggleOilGloss));
            return;
        }

        IsOilGlossSupported = _oilGlossService.IsSupported;
        var status = _oilGlossService.GetStatus(_loadedMod.Mod);
        IsOilGlossApplied = status.State is OilGlossState.Applied or OilGlossState.Conflict;
        OilGlossStatusText = status.State switch
        {
            OilGlossState.Applied => status.Manifest is { HasClothGroup: true }
                ? "已开启（身体 / 衣物）"
                : "已开启（仅身体）",
            OilGlossState.Broken => "补丁不完整：" + status.Reason,
            OilGlossState.Conflict => "补丁文件被修改：" + status.Reason,
            _ => "未开启",
        };

        OnPropertyChanged(nameof(CanToggleOilGloss));
        OnPropertyChanged(nameof(OilGlossToggleLabel));
        ToggleOilGlossCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 让游戏重新读取 Mod ini（F10）。油光补丁与菜单都是磁盘文件，
    /// 不刷新的话游戏里看不到任何变化。
    /// </summary>
    private async Task RequestGameRefreshAsync()
    {
        try
        {
            await App.GetService<OverlayRefreshCoordinator>().RequestRefreshAsync();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[OilGloss] 请求游戏刷新失败");
        }
    }

    private bool CanToggleOilGlossNow() => CanToggleOilGloss && !IsOilGlossBusy;

    /// <summary>
    /// 详情页唯一的油光按钮：未开启时开启，已开启时撤销。
    /// 拆成两个按钮会让人分不清该点哪个，这里按状态自动切换动作。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleOilGlossNow))]
    private async Task ToggleOilGlossAsync()
    {
        if (IsOilGlossApplied)
            await DisableOilGlossAsync();
        else
            await EnableOilGlossAsync();
    }

    /// <summary>
    /// 逐块识别油光分组：每一块都能「试亮」——只给这一块上油光并自动刷新游戏，
    /// 回游戏看哪块变亮就知道它是身体还是头发，再决定分组。
    /// 试亮只是临时改写补丁，点「取消」会恢复打开窗口前的分组。
    /// </summary>
    [RelayCommand]
    private async Task CustomizeOilGroupsAsync()
    {
        if (!IsModLoaded || IsOilGlossBusy)
            return;

        var mod = _loadedMod.Mod;

        // 试亮会反复重打补丁，先把「打开前」的状态记下来，取消时按它恢复。
        var beforeStatus = _oilGlossService.GetStatus(mod);
        if (beforeStatus.State is OilGlossState.Conflict or OilGlossState.Broken)
        {
            OilGlossStatusText = "补丁状态异常，请先撤销油光再调整：" + beforeStatus.Reason;
            return;
        }

        var wasApplied = beforeStatus.State is OilGlossState.Applied;
        IReadOnlyList<OilGlossDrawcall> previousGroups =
            wasApplied ? ReadAppliedGroups(mod, beforeStatus) : [];

        IsOilGlossBusy = true;
        try
        {
            var scan = await Task.Run(() => _oilGlossService.Scan(mod),
                _cancellationToken);

            // 按大小排序并用「第 N 大块」称呼——索引号对用户没有意义。
            var draws = scan.Drawcalls
                .OrderByDescending(d => d.IndexCount)
                .ThenBy(d => d.IniRelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.StartIndex)
                .ToList();

            if (draws.Count == 0)
            {
                OilGlossStatusText = "没有可调整的网格";
                return;
            }

            var choices = new List<(OilGlossDrawcall Draw, ComboBox Box)>();
            var trialButtons = new List<Button>();
            var trialBusy = false;
            var anyTrial = false;

            var trialStatus = new TextBlock
            {
                Text = "点「试亮」只会给这一块上油光并自动刷新游戏；回游戏看哪块变亮，就是它。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "不知道哪块是身体？逐块点「试亮」，回游戏看哪块变亮。"
                       + "确认后选分组：身体 / 衣物 / 不包（头发选「不包」）。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
            panel.Children.Add(trialStatus);

            void SetTrialButtonsEnabled(bool enabled)
            {
                foreach (var button in trialButtons)
                    button.IsEnabled = enabled;
            }

            for (var i = 0; i < draws.Count; i++)
            {
                var draw = draws[i];
                var rank = i + 1;

                var combo = new ComboBox
                {
                    ItemsSource = new[] { "不包", "身体", "衣物" },
                    // 已经开过油光时以补丁里的实际分组为准，否则「取消→再打开→应用」
                    // 会把用户之前的手动分组覆盖回自动分组。
                    SelectedIndex = (wasApplied ? FindAppliedGroup(previousGroups, draw) : draw.Group) switch
                    {
                        OilGlossGroup.Body => 1,
                        OilGlossGroup.Cloth => 2,
                        _ => 0,
                    },
                    MinWidth = 96,
                };

                var label = new TextBlock
                {
                    Text = $"第 {rank} 大块 · {draw.IndexCount / 3:N0} 面",
                    Width = 200,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                ToolTipService.SetToolTip(label,
                    $"{draw.IniRelativePath} · {draw.SectionName}"
                    + (draw.IsHeadLike ? "（名字像头发）" : string.Empty)
                    + (draw.VisibleByDefault == false ? "（默认隐藏）" : string.Empty));

                var trialButton = new Button { Content = "试亮", FontSize = 12 };
                trialButtons.Add(trialButton);

                trialButton.Click += async (_, _) =>
                {
                    if (trialBusy)
                        return;

                    trialBusy = true;
                    anyTrial = true;
                    trialStatus.Text = $"正在试亮第 {rank} 大块…";
                    SetTrialButtonsEnabled(false);
                    try
                    {
                        var trial = await _oilGlossService.TrialBodyAsync(mod, draw, _cancellationToken);
                        if (trial.Status is OilGlossApplyStatus.Applied or OilGlossApplyStatus.AlreadyApplied)
                        {
                            trialStatus.Text = $"现在只有第 {rank} 大块带油光，回游戏看看它是不是身体。";
                            await RequestGameRefreshAsync();
                        }
                        else
                        {
                            trialStatus.Text = "试亮失败：" + trial.Reason;
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Warning(e, "[OilGloss] 试亮失败");
                        trialStatus.Text = "试亮失败：" + e.Message;
                    }
                    finally
                    {
                        trialBusy = false;
                        SetTrialButtonsEnabled(true);
                    }
                };

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(label);
                row.Children.Add(trialButton);
                row.Children.Add(combo);
                panel.Children.Add(row);
                choices.Add((draw, combo));
            }

            var dialog = new ContentDialog
            {
                Title = "逐块识别身体",
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 420,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                },
                PrimaryButtonText = "应用",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            // 试亮进行中不允许关窗，否则「恢复原分组」会和正在写的补丁打架。
            dialog.Closing += (_, args) =>
            {
                if (trialBusy)
                    args.Cancel = true;
            };

            var windowManagerService = App.GetService<IWindowManagerService>();
            var dialogResult = await windowManagerService.ShowDialogAsync(dialog);

            if (dialogResult == ContentDialogResult.Primary)
            {
                var selected = choices.Select(c => c.Draw with
                {
                    Group = c.Box.SelectedIndex switch
                    {
                        1 => OilGlossGroup.Body,
                        2 => OilGlossGroup.Cloth,
                        _ => (OilGlossGroup?)null,
                    },
                }).ToList();

                var applied = await _oilGlossService.ApplyCustomGroupsAsync(mod, selected, _cancellationToken);
                if (applied.Status is OilGlossApplyStatus.Applied or OilGlossApplyStatus.AlreadyApplied)
                {
                    RefreshOilGlossStatus();
                    RefreshInGameMenuStatus();
                    _notificationService.ShowNotification("已按选好的分组重打油光", "按 F7 查看效果", null);
                    await RequestGameRefreshAsync();
                }
                else
                {
                    OilGlossStatusText = "应用分组失败：" + applied.Reason;
                }

                return;
            }

            // 取消：试亮改写过补丁的话，恢复打开窗口前的分组。
            if (anyTrial)
                await RestoreOilGroupsAfterCustomizeAsync(mod, wasApplied, previousGroups);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[OilGloss] 逐块识别身体失败");
            OilGlossStatusText = "逐块识别失败：" + e.Message;
        }
        finally
        {
            IsOilGlossBusy = false;
        }
    }

    /// <summary>
    /// 读取当前补丁里每块网格的分组。新清单直接有记录；
    /// 旧清单没有这项，退回当前 Mod 的自动分组（至少能还原成「最大块 = 身体」）。
    /// </summary>
    private IReadOnlyList<OilGlossDrawcall> ReadAppliedGroups(ISkinMod mod, OilGlossStatus status)
    {
        if (status.Manifest?.AppliedDrawcalls is { Count: > 0 } recorded)
        {
            return recorded.Select(d => new OilGlossDrawcall
            {
                IniRelativePath = d.IniRelativePath,
                SectionName = d.SectionName,
                LineIndex = 0,
                IndexCount = d.IndexCount,
                StartIndex = d.StartIndex,
                Group = d.Group,
            }).ToList();
        }

        return _oilGlossService.Scan(mod).Drawcalls
            .Where(d => d.Group is not null)
            .ToList();
    }

    /// <summary>在已应用的分组里找同一块网格（路径 + 段落 + 索引数 + 起始索引）。</summary>
    private static OilGlossGroup? FindAppliedGroup(
        IReadOnlyList<OilGlossDrawcall> applied,
        OilGlossDrawcall draw)
    {
        var match = applied.FirstOrDefault(p =>
            string.Equals(p.IniRelativePath, draw.IniRelativePath, StringComparison.OrdinalIgnoreCase)
            && p.SectionName == draw.SectionName
            && p.IndexCount == draw.IndexCount
            && p.StartIndex == draw.StartIndex);

        return match?.Group;
    }

    /// <summary>「逐块识别」取消后恢复：原本开过就按原分组重打，原本没开就撤销。</summary>
    private async Task RestoreOilGroupsAfterCustomizeAsync(
        ISkinMod mod,
        bool wasApplied,
        IReadOnlyList<OilGlossDrawcall> previousGroups)
    {
        if (!wasApplied || previousGroups.Count == 0)
        {
            // 原来没开过，或读不出原分组：撤销掉试亮写进去的补丁，别让它留在 Mod 里。
            await _oilGlossService.DisableAsync(mod, _cancellationToken);
        }
        else
        {
            var restored = await _oilGlossService.RestoreGroupsAsync(mod, previousGroups, _cancellationToken);
            if (restored.Status is not (OilGlossApplyStatus.Applied or OilGlossApplyStatus.AlreadyApplied))
            {
                OilGlossStatusText = "恢复原分组失败：" + restored.Reason;
                return;
            }
        }

        RefreshOilGlossStatus();
        RefreshInGameMenuStatus();
        await RequestGameRefreshAsync();
    }

    /// <summary>详情页「开启油光」：复制本体 → 自动分组 → 打补丁 → 重建已有菜单。</summary>
    private async Task EnableOilGlossAsync()
    {
        if (!IsModLoaded)
            return;

        var mod = _loadedMod.Mod;
        IsOilGlossBusy = true;
        try
        {
            var result = await _oilGlossService.EnableAsync(mod, _cancellationToken);

            if (result.Status is OilGlossApplyStatus.Applied or OilGlossApplyStatus.AlreadyApplied)
            {
                RefreshOilGlossStatus();
                RefreshInGameMenuStatus();
                if (result.Status == OilGlossApplyStatus.Applied)
                {
                    var hint = _inGameMenuService.HasGeneratedMenu(mod)
                        ? "按 F7 打开游戏内菜单即可切换身体 / 衣物油光"
                        : "生成游戏内菜单后，可在 F7 里切换身体 / 衣物油光";
                    _notificationService.ShowNotification("油光已开启", hint, null);
                    await RequestGameRefreshAsync();
                }
            }
            else
            {
                IsOilGlossApplied = false;
                OilGlossStatusText = (result.Status == OilGlossApplyStatus.NoDrawcalls ? "没有可包油光的网格：" : "开启失败：")
                                     + result.Reason;
            }
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[OilGloss] 详情页开启油光失败");
            OilGlossStatusText = "开启失败：" + e.Message;
        }
        finally
        {
            IsOilGlossBusy = false;
        }
    }

    /// <summary>详情页「撤销油光」：还原作者 ini、删除 JASM 产物、重建菜单。</summary>
    private async Task DisableOilGlossAsync()
    {
        if (!IsModLoaded)
            return;

        var mod = _loadedMod.Mod;
        IsOilGlossBusy = true;
        try
        {
            var result = await _oilGlossService.DisableAsync(mod, _cancellationToken);
            if (result.Success)
            {
                RefreshOilGlossStatus();
                RefreshInGameMenuStatus();
                _notificationService.ShowNotification("油光已撤销", "作者 ini 已还原，油光菜单行会在菜单重建后消失", null);
                await RequestGameRefreshAsync();
            }
            else
            {
                var conflictDetail = result.Conflicts.Count > 0
                    ? "（" + string.Join(", ", result.Conflicts) + "）"
                    : string.Empty;
                OilGlossStatusText = "撤销失败：" + result.Reason + conflictDetail;
            }
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[OilGloss] 详情页撤销油光失败");
            OilGlossStatusText = "撤销失败：" + e.Message;
        }
        finally
        {
            IsOilGlossBusy = false;
        }
    }

    /// <summary>详情页的「生成游戏内菜单」。已有作者可点击菜单时会被生成器跳过。</summary>
    [RelayCommand]
    private async Task GenerateInGameMenuAsync()
    {
        if (!IsModLoaded || IsInGameMenuBusy)
            return;

        IsInGameMenuBusy = true;
        try
        {
            var settings = await _inGameMenuService.ReadSettingsAsync();
            var mod = _loadedMod.Mod;
            var result = await Task.Run(() => _inGameMenuService.GenerateForMod(mod, settings));

            InGameMenuStatusText = result.Status switch
            {
                InGameMenuGenerationStatus.Generated => "菜单已生成（F7 打开，↑/↓ 选择，F9 切换）",
                InGameMenuGenerationStatus.SkippedExistingClickableMenu => "作者已自带可点击菜单，已跳过",
                InGameMenuGenerationStatus.SkippedNoRows => "没有可用的 [Key*] 段落，已跳过",
                InGameMenuGenerationStatus.SkippedNoClickableRows => "没有可切换的循环变量，已跳过",
                _ => "生成失败：" + result.Reason,
            };

            IsInGameMenuGenerated = _inGameMenuService.HasGeneratedMenu(mod);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[InGameMenu] 详情页生成菜单失败");
            InGameMenuStatusText = "生成失败：" + e.Message;
        }
        finally
        {
            IsInGameMenuBusy = false;
        }
    }

    /// <summary>详情页的「移除游戏内菜单」。只删 JASM 自己生成的产物。</summary>
    [RelayCommand]
    private void RemoveInGameMenu()
    {
        if (!IsModLoaded)
            return;

        try
        {
            _inGameMenuService.RemoveForMod(_loadedMod.Mod);
            IsInGameMenuGenerated = false;
            InGameMenuStatusText = "菜单已移除";
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[InGameMenu] 详情页移除菜单失败");
            InGameMenuStatusText = "移除失败：" + e.Message;
        }
    }


    /// <summary>
    /// 详情页的「自定义菜单行」：调整顺序与显隐，保存后立即重建这个 Mod 的菜单。
    /// </summary>
    [RelayCommand]
    private async Task CustomizeInGameMenuAsync()
    {
        if (!IsModLoaded || IsInGameMenuBusy)
            return;

        var mod = _loadedMod.Mod;
        IsInGameMenuBusy = true;

        try
        {
            var rows = await Task.Run(() => _inGameMenuService.ReadRows(mod));
            if (rows.Count == 0)
            {
                InGameMenuStatusText = "没有可自定义的 [Key*] 行";
                return;
            }

            var settings = await _inGameMenuService.ReadSettingsAsync();
            var layout = _inGameMenuService.GetRowLayout(mod, settings);
            var hidden = new HashSet<string>(layout.Hidden, StringComparer.OrdinalIgnoreCase);

            var ordered = new List<ModIniToggleRow>();
            foreach (var key in layout.Order)
            {
                var row = rows.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
                if (row is not null && !ordered.Contains(row))
                    ordered.Add(row);
            }

            foreach (var row in rows)
            {
                if (!ordered.Contains(row))
                    ordered.Add(row);
            }

            var list = new ListView
            {
                Height = 300,
                SelectionMode = ListViewSelectionMode.Single
            };

            void Refresh(int select)
            {
                list.ItemsSource = ordered
                    .Select((row, index) =>
                        $"{index + 1}. {(hidden.Contains(row.Key) ? "✗" : "✓")} {row.Label}" +
                        (row.IsClickable ? string.Empty : "（不可点击）"))
                    .ToList();

                if (ordered.Count > 0)
                    list.SelectedIndex = Math.Clamp(select, 0, ordered.Count - 1);
            }

            var visibleBox = new CheckBox { Content = "在菜单中显示", IsEnabled = false };
            var upButton = new Button { Content = "↑ 上移", IsEnabled = false };
            var downButton = new Button { Content = "↓ 下移", IsEnabled = false };

            list.SelectionChanged += (_, _) =>
            {
                var index = list.SelectedIndex;
                var has = index >= 0 && index < ordered.Count;
                visibleBox.IsEnabled = has;
                upButton.IsEnabled = has && index > 0;
                downButton.IsEnabled = has && index < ordered.Count - 1;
                if (has)
                    visibleBox.IsChecked = !hidden.Contains(ordered[index].Key);
            };

            visibleBox.Click += (_, _) =>
            {
                var index = list.SelectedIndex;
                if (index < 0 || index >= ordered.Count)
                    return;

                var key = ordered[index].Key;
                if (visibleBox.IsChecked == true)
                    hidden.Remove(key);
                else
                    hidden.Add(key);

                Refresh(index);
            };

            upButton.Click += (_, _) =>
            {
                var index = list.SelectedIndex;
                if (index <= 0)
                    return;

                (ordered[index - 1], ordered[index]) = (ordered[index], ordered[index - 1]);
                Refresh(index - 1);
            };

            downButton.Click += (_, _) =>
            {
                var index = list.SelectedIndex;
                if (index < 0 || index >= ordered.Count - 1)
                    return;

                (ordered[index + 1], ordered[index]) = (ordered[index], ordered[index + 1]);
                Refresh(index + 1);
            };

            Refresh(0);

            var dialog = new ContentDialog
            {
                Title = "自定义游戏内菜单行",
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "选择一行后可上移/下移或隐藏；✓ = 显示，✗ = 隐藏。",
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 12
                        },
                        list,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children = { visibleBox, upButton, downButton }
                        }
                    }
                },
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            var windowManagerService = App.GetService<IWindowManagerService>();
            if (await windowManagerService.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
                return;

            var newLayout = new InGameMenuRowLayout
            {
                Order = ordered.Select(row => row.Key).ToList(),
                Hidden = hidden.ToList()
            };

            await _inGameMenuService.SaveRowLayoutAsync(mod, newLayout);
            var updated = await _inGameMenuService.ReadSettingsAsync();
            var result = await Task.Run(() => _inGameMenuService.GenerateForMod(mod, updated));

            InGameMenuStatusText = result.Status == InGameMenuGenerationStatus.Generated
                ? "菜单已按自定义重建"
                : "保存成功，但重建失败：" + result.Reason;
        }
        catch (Exception e)
        {
            _logger.Warning(e, "[InGameMenu] 自定义菜单行失败");
            InGameMenuStatusText = "自定义失败：" + e.Message;
        }
        finally
        {
            IsInGameMenuBusy = false;
        }
    }

    private readonly record struct LoadModMessage
    {
        public Guid? ModId { get; init; }
        public bool Force { get; init; }
    }

    public void Receive(ModChangedMessage message)
    {
        if (!IsModLoaded)
            return;

        if (message.SkinEntry.Id != _loadedModId)
            return;

        if (message.sender == this)
            return;

        QueueLoadMod(message.SkinEntry.Id, true);
    }

    public Task OnNavigatedToAsync(DispatcherQueue dispatcherQueue, CancellationToken navigationCt)
    {
        _dispatcherQueue = dispatcherQueue;
        _cancellationToken = navigationCt;
        _ = _dispatcherQueue.EnqueueAsync(ModLoaderLoopAsync);
        Messenger.RegisterAll(this);
        BusySetter.HardBusyChanged += BusySetter_HardBusyChanged;
        IsInitialized = true;
        return Task.CompletedTask;
    }

    public void OnNavigatedFrom()
    {
        _channel.Writer.TryComplete();
        Messenger.UnregisterAll(this);
        try
        {
            _loadModLock.Dispose();
        }
        catch (Exception e)
        {
            _logger.Warning(e, "Failed to dispose of load mod lock");
        }
    }

    private bool DefaultCanExecute => IsModLoaded && IsNotReadOnly && BusySetter.IsNotHardBusy;

    #region Commands

    private bool CanSetModIniFile() => DefaultCanExecute;

    [RelayCommand(CanExecute = nameof(CanSetModIniFile))]
    private async Task SetModIniFileAsync()
    {
        if (!IsModLoaded) return;
        try
        {
            var modFolderPath = _loadedMod.Mod.FullPath;

            var dataPackage = new DataPackage();
            dataPackage.SetText(modFolderPath);
            Clipboard.SetContent(dataPackage);

            _notificationService.ShowNotification(_localizer.GetLocalizedStringOrDefault("ModPaneVM_ModFolderPathCopiedTitle", defaultValue: "Mod folder path copied to clipboard"), "", TimeSpan.FromSeconds(3));
        }
        catch (Exception e)
        {
            _logger.Error(e, "An error occured while trying to copy mod folder path to clipboard when setting .ini");
        }


        var filePicker = new FileOpenPicker();
        filePicker.SettingsIdentifier = "IniFilerPicker";
        filePicker.FileTypeFilter.Add(".ini");
        filePicker.CommitButtonText = _localizer.GetLocalizedStringOrDefault("ModPaneVM_Set", defaultValue: "Set");
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);
        var file = await filePicker.PickSingleFileAsync();

        if (file is null)
        {
            _logger.Debug("User cancelled file picker.");
            return;
        }

        var result = await Task.Run(() => _modSettingsService.SetModIniAsync(_loadedMod.Id, file.Path));


        if (result.Notification is not null)
            _notificationService.ShowNotification(result.Notification);

        _loadedMod.Mod.ClearCache();

        Messenger.Send(new ModChangedMessage(this, _loadedMod, null));
        QueueLoadMod(_loadedModId, true);
    }

    private bool CanClearSetModIniFile() => DefaultCanExecute;

    [RelayCommand(CanExecute = nameof(CanClearSetModIniFile))]
    private async Task ClearSetModIniFileAsync()
    {
        await CommandWrapper(async () =>
        {
            if (!IsModLoaded) return;

            var autoDetect = ModModel.IgnoreMergedIni;
            var result = await Task.Run(() =>
                _modSettingsService.SetModIniAsync(_loadedModId.Value, string.Empty, autoDetect), _cancellationToken);


            if (result.Notification is not null)
                _notificationService.ShowNotification(result.Notification);

            _loadedMod.Mod.ClearCache();

            Messenger.Send(new ModChangedMessage(this, _loadedMod, null));
            QueueLoadMod(_loadedModId, true);
        }).ConfigureAwait(false);
    }

    private bool CanPickImageUri() => DefaultCanExecute;

    [RelayCommand(CanExecute = nameof(CanPickImageUri))]
    private async Task PickImageUriAsync()
    {
        if (!IsModLoaded) return;
        var filePicker = new FileOpenPicker();
        filePicker.CommitButtonText = _localizer.GetLocalizedStringOrDefault("ModPaneVM_SetImage", defaultValue: "Set Image");
        filePicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        filePicker.SettingsIdentifier = "ImagePicker";
        foreach (var supportedImageExtension in Constants.SupportedImageExtensions)
            filePicker.FileTypeFilter.Add(supportedImageExtension);


        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);

        var file = await filePicker.PickSingleFileAsync();

        if (file == null) return;
        var imageUri = new Uri(file.Path);
        ModModel.ImageUri = imageUri;
    }

    private bool CanPasteImage() => DefaultCanExecute;

    [RelayCommand(CanExecute = nameof(CanPasteImage))]
    private async Task PasteImageFromClipboardAsync()
    {
        await CommandWrapper(async () =>
        {
            if (!IsModLoaded) return;

            var clipboardHasValidImageResult = await _imageHandlerService.ClipboardContainsImageAsync();

            if (!clipboardHasValidImageResult.Result)
            {
                _notificationService.ShowNotification(_localizer.GetLocalizedStringOrDefault("ModPaneVM_ClipboardNoValidImageTitle", defaultValue: "Clipboard does not contain a valid image"), "", null);
                return;
            }

            var imagePath = await _imageHandlerService.GetImageFromClipboardAsync(clipboardHasValidImageResult.DataPackage);

            if (imagePath == null)
            {
                _notificationService.ShowNotification(_localizer.GetLocalizedStringOrDefault("ModPaneVM_CouldNotRetrieveImageTitle", defaultValue: "Could not retrieve image from clipboard"), "", null);
                return;
            }

            ModModel.ImageUri = imagePath;
        }).ConfigureAwait(false);
    }

    private bool CanCopyImageToClipboard() => DefaultCanExecute;

    [RelayCommand(CanExecute = nameof(CanCopyImageToClipboard))]
    private async Task CopyImageToClipboardAsync()
    {
        await CommandWrapper(async () =>
        {
            if (!File.Exists(ModModel.ImageUri.LocalPath))
                return;

            var file = await StorageFile.GetFileFromPathAsync(ModModel.ImageUri.LocalPath);
            if (file is null)
                return;


            await ImageHandlerService.CopyImageToClipboardAsync(file).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }


    private bool CanClearImage() => DefaultCanExecute && ModModel.ImageUri != ImageHandlerService.StaticPlaceholderImageUri;

    [RelayCommand(CanExecute = nameof(CanClearImage))]
    private void ClearImage()
    {
        if (!IsModLoaded) return;
        ModModel.ImageUri = ImageHandlerService.StaticPlaceholderImageUri;
    }

    private bool CanSaveModSettings() => DefaultCanExecute && ModModel.AnyChanges;

    [RelayCommand(CanExecute = nameof(CanSaveModSettings))]
    private async Task SaveModSettingsAsync()
    {
        await CommandWrapper(async () =>
        {
            if (!CanSaveModSettings()) return;

            var existingModSettings = await _loadedMod!.Mod.Settings.ReadSettingsAsync();

            var updateRequest = new UpdateSettingsRequest();

            if (ModModel.IsImageUriChanged)
                updateRequest.SetImagePath = ModModel.ImageUri;

            if (ModModel.IsModDisplayNameChanged)
                updateRequest.SetCustomName = ModModel.ModDisplayName;

            if (ModModel.IsModUrlChanged)
                updateRequest.SetModUrl = Uri.TryCreate(ModModel.ModUrl, UriKind.Absolute, out var url) ? url : null;

            Result<ModSettings>? result = null;
            Exception? savingKeySwapException = null;

            await Task.Run(async () =>
            {
                if (updateRequest.AnyUpdates)
                {
                    result = await _modSettingsService.SaveSettingsAsync(_loadedModId.Value, updateRequest).ConfigureAwait(false);
                }

                if (!_loadedMod.Mod.Settings.HasMergedIni && !ModModel.KeySwaps.Any()
                    || _loadedMod.Mod.KeySwaps is null ||
                    !ModModel.IsKeySwapsChanged ||
                    existingModSettings.IgnoreMergedIni)
                    return;

                // TODO: Will need to redo keyswap handling at some point doing a quick solution here
                var keySwapSections = new List<KeySwapSection>();

                foreach (var modModelSkinModKeySwap in ModModel.KeySwaps)
                {
                    var variants = int.TryParse(modModelSkinModKeySwap.VariationsCount, out var variantsCount)
                        ? variantsCount
                        : -1;

                    var keySwapSection = new KeySwapSection()
                    {
                        SectionName = modModelSkinModKeySwap.SectionKey,
                        ForwardKey = modModelSkinModKeySwap.ForwardHotkey,
                        BackwardKey = modModelSkinModKeySwap.BackwardHotkey,
                        Variants = variants == -1 ? null : variants,
                        Type = modModelSkinModKeySwap.Type ?? "Unknown"
                    };

                    keySwapSections.Add(keySwapSection);
                }

                try
                {
                    await _loadedMod.Mod.KeySwaps.SaveKeySwapConfiguration(keySwapSections).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    savingKeySwapException = e;
                    _logger.Error(e, "An error occured trying to save keyswaps for mod {ModPath}", _loadedMod.Mod.FullPath);
                }
            });

            if (result?.Notification is not null && savingKeySwapException is null)
                _notificationService.ShowNotification(result.Notification);

            if (savingKeySwapException is not null)
                _notificationService.ShowNotification(_localizer.GetLocalizedStringOrDefault("ModPaneVM_FailedToSaveKeySwapsTitle", defaultValue: "Failed to save key swaps"), savingKeySwapException.Message, null);

            _cancellationToken.ThrowIfCancellationRequested();


            Messenger.Send(new ModChangedMessage(this, _loadedMod, null));
            QueueLoadMod(_loadedModId, true);
        }).ConfigureAwait(false);
    }

    private bool CanOpenModFolder() => DefaultCanExecute;

    [RelayCommand(CanExecute = nameof(CanOpenModFolder))]
    private async Task OpenModFolderAsync()
    {
        await CommandWrapper(async () =>
        {
            if (!IsModLoaded) return;
            await Windows.System.Launcher.LaunchFolderAsync(
                await StorageFolder.GetFolderFromPathAsync(_loadedMod.Mod.FullPath));
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private void ToggleEditingModName()
    {
        IsEditingModName = !IsEditingModName;
    }

    #endregion

    #region DragAndDropHandlers

    public bool CanSetImageFromDragDropWeb(Uri? url) => DescribeImageDropRefusalForWeb(url) is null;

    /// <summary>链接那条路「为什么收不了」；收得下返回 <c>null</c>。理由见 <see cref="DescribeImageDropBusyRefusal"/>。</summary>
    public string? DescribeImageDropRefusalForWeb(Uri? url)
    {
        if (DescribeImageDropBusyRefusal() is { } busy)
            return busy;

        if (url is null || !url.IsAbsoluteUri)
            return "这一拖里没有可用的链接。";

        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
            return "封面图只接 http/https 的链接。";

        if (!Constants.SupportedImageExtensions.Contains(Path.GetExtension(url.AbsolutePath)))
            return "这个链接不是 JASM 认的图片格式。";

        return null;
    }

    public async Task SetImageFromDragDropWeb(Uri uri)
    {
        await CommandWrapper(async () =>
        {
            var image = await _imageHandlerService.DownloadImageAsync(uri, _cancellationToken);
            ModModel.ImageUri = new Uri(image.Path);
        }, true, useDefaultExceptionHandler: true).ConfigureAwait(false);
    }

    public bool CanSetImageFromDragDropStorageItem(IReadOnlyList<IStorageItem> storageItems)
        => DescribeImageDropRefusalForFile(storageItems) is null;

    /// <summary>文件那条路「为什么收不了」；收得下返回 <c>null</c>。</summary>
    public string? DescribeImageDropRefusalForFile(IReadOnlyList<IStorageItem>? storageItems)
    {
        if (DescribeImageDropBusyRefusal() is { } busy)
            return busy;

        if (storageItems is null || storageItems.Count == 0)
            return "这一拖里没有文件。";

        if (storageItems.Count > 1)
            return "封面图一次只能拖一张。";

        var file = storageItems[0];

        // 虚拟文件（网盘占位、压缩软件窗口里的条目）路径看着像样、内容读不到，
        // 拖进来会在读取那一步才炸 —— 不如这里就说清楚
        if (!File.Exists(file.Path))
            return "这个文件读不到，可能来自网盘的占位文件或压缩软件的临时目录。"
                   + "请先把图片存到本地文件夹，再从那里拖。";

        if (!Constants.SupportedImageExtensions.Contains(Path.GetExtension(file.Name)))
            return $"「{file.Name}」不是 JASM 认的图片格式。";

        return null;
    }

    /// <summary>
    /// 「现在不让拖」那几种情形的话。返回 <c>null</c> = 没这条限制，继续往下判。
    ///
    /// <para>
    /// <b>为什么要说这句话</b>：XAML 的拖放不接受就是不设 <c>AcceptedOperation</c>，用户那边只剩禁止光标，
    /// 而松手连 Drop 事件都不会来 —— 界面上没有一个字说明原因。没选中 Mod、只读、正忙、格式不对
    /// 这四件毫不相干的事，在用户眼里长得一模一样。
    /// </para>
    /// </summary>
    private string? DescribeImageDropBusyRefusal()
    {
        if (!IsModLoaded)
            return "先在左边选中一个 Mod，再往这里拖封面图。";

        if (IsReadOnly)
            return "这个 Mod 当前是只读的，改不了封面。";

        if (BusySetter.IsHardBusy)
            return "JASM 正忙（还有安装或移动任务在跑），等它做完再拖。";

        return null;
    }

    public async Task SetImageFromDragDropFile(IReadOnlyList<IStorageItem> storageItems)
    {
        await CommandWrapper(() =>
        {
            var file = storageItems.First();

            var filePath = new Uri(file.Path);

            ModModel.ImageUri = filePath;
            return Task.CompletedTask;
        }, true, useDefaultExceptionHandler: true).ConfigureAwait(false);
    }

    #endregion

    private async Task CommandWrapper(Func<Task> command, bool hardBusy = false, Action<Exception>? uncaughtErrorHandler = null,
        bool useDefaultExceptionHandler = false, [CallerMemberName] string commandName = "")
    {
        try
        {
            using var _ = await LockAsync().ConfigureAwait(false);
            using var busy = hardBusy ? BusySetter.StartHardBusy() : BusySetter.StartSoftBusy();

            await command();
        }
        catch (TaskCanceledException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (useDefaultExceptionHandler)
            {
                _logger.Error(e, "An error occured while executing command {CommandName}", commandName);
                _notificationService.ShowNotification(string.Format(_localizer.GetLocalizedStringOrDefault("ModPaneVM_ErrorRunningCommandTitle", defaultValue: "An error occured running command {0}"), commandName), e.Message, null);
                return;
            }

            if (uncaughtErrorHandler is null) throw;

            var ex = ExceptionDispatchInfo.Capture(e);
            uncaughtErrorHandler(ex.SourceException);
        }
    }

    private async Task CommandWrapper(Action command, bool hardBusy = false)
    {
        try
        {
            using var _ = await LockAsync().ConfigureAwait(false);
            using var busy = hardBusy ? BusySetter.StartHardBusy() : BusySetter.StartSoftBusy();
            command();
        }
        catch (TaskCanceledException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<IDisposable> LockAsync() =>
        await _loadModLock.LockAsync(cancellationToken: _cancellationToken).ConfigureAwait(false);

    private IRelayCommand[]? _viewModelCommands;

    private void NotifyAllCommands()
    {
        if (_viewModelCommands is null)
        {
            var commands = new List<IRelayCommand>();
            foreach (var propertyInfo in GetType()
                         .GetProperties()
                         .Where(p => p.PropertyType.IsAssignableTo(typeof(IRelayCommand))))
            {
                var value = propertyInfo.GetValue(this);

                if (value is IRelayCommand relayCommand)
                    commands.Add(relayCommand);
            }

            _viewModelCommands = commands.ToArray();
        }

        _viewModelCommands.ForEach(c => c.NotifyCanExecuteChanged());
    }
}

public partial class ModPaneFieldsVm : ObservableObject
{
    public bool IsLoaded { get; private init; }
    public ModPaneFieldsVm? UnchangedValue { get; private init; }

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private Uri _imageUri = ImageHandlerService.StaticPlaceholderImageUri;
    public bool IsImageUriChanged => ImageUri != UnchangedValue?.ImageUri;
    [ObservableProperty] private string _modDisplayName = string.Empty;
    public bool IsModDisplayNameChanged => ModDisplayName != UnchangedValue?.ModDisplayName;
    [ObservableProperty] private string _modUrl = string.Empty;
    public bool IsModUrlChanged => ModUrl != UnchangedValue?.ModUrl;
    [ObservableProperty] private string? _modIniPath = null;
    public bool IsModIniPathChanged => ModIniPath != UnchangedValue?.ModIniPath;
    [ObservableProperty] private bool _ignoreMergedIni = true;
    public bool IsIgnoreMergedIniChanged => IgnoreMergedIni != UnchangedValue?.IgnoreMergedIni;

    public ObservableCollection<ModPaneFieldsKeySwapVm> KeySwaps { get; } = new();
    public bool IsKeySwapsChanged => AnyKeySwapChanges();

    /// <summary>所有 ini 文件中的按键绑定（非 KeySwap 段落），按文件分组</summary>
    public ObservableCollection<Core.Entities.Mods.Helpers.ModIniKeyBindingGroup> KeyBindingGroups { get; } = new();

    /// <summary>True if this mod has any key bindings to display (either KeyBindingGroups or KeySwaps)</summary>
    public bool HasKeyBindings => KeyBindingGroups.Any(g => g.Bindings.Count > 0)
                                  || (KeySwaps.Count > 0
                                      && KeySwaps.Any(k => !string.IsNullOrWhiteSpace(k.ForwardHotkey)
                                                           || !string.IsNullOrWhiteSpace(k.BackwardHotkey)));

    /// <summary>True if this mod has NO key bindings at all</summary>
    public bool HasNoKeyBindings => !HasKeyBindings;

    public string IsKeySwapManagementEnabled => (!IgnoreMergedIni).ToString().ToLower();

    private ModPaneFieldsVm(CharacterSkinEntry modEntry, ModSettings modSettings,
        IEnumerable<KeySwapSection> keySwaps,
        List<Core.Entities.Mods.Helpers.ModIniKeyBindingGroup> keyBindingGroups)
    {
        IsEnabled = modEntry.IsEnabled;
        ImageUri = modSettings.ImagePath ?? ImageHandlerService.StaticPlaceholderImageUri;
        ModDisplayName = modEntry.Mod.GetDisplayName();
        ModUrl = modSettings.ModUrl?.ToString() ?? "";
        ModIniPath = modSettings.MergedIniPath?.ToString();
        IgnoreMergedIni = modSettings.IgnoreMergedIni;

        foreach (var keySwap in keySwaps)
        {
            KeySwaps.Add(new ModPaneFieldsKeySwapVm()
            {
                ForwardHotkey = keySwap.ForwardKey,
                BackwardHotkey = keySwap.BackwardKey,
                SectionKey = keySwap.SectionName,
                Type = keySwap.Type,
                VariationsCount = keySwap.Variants?.ToString() ?? "Unknown"
            });

            KeySwaps.Last().PropertyChanged += (_, e) =>
            {
                OnPropertyChanged(nameof(KeySwaps));
                OnPropertyChanged(nameof(HasKeyBindings));
            };
        }

        foreach (var group in keyBindingGroups)
            KeyBindingGroups.Add(group);

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(AnyChanges))
                OnPropertyChanged(nameof(AnyChanges));
        };
    }

    public ModPaneFieldsVm()
    {
    }

    public static ModPaneFieldsVm FromModEntry(CharacterSkinEntry modEntry, ModSettings modSettings,
        ICollection<KeySwapSection> keySwaps,
        List<Core.Entities.Mods.Helpers.ModIniKeyBindingGroup> keyBindingGroups)
    {
        return new ModPaneFieldsVm(modEntry, modSettings, keySwaps, keyBindingGroups)
        {
            UnchangedValue = new ModPaneFieldsVm(modEntry, modSettings, keySwaps, keyBindingGroups),
            IsLoaded = true
        };
    }

    public bool AnyChanges
    {
        get
        {
            var anyChanges = false;

            if (UnchangedValue is null)
                return false;

            anyChanges |= IsEnabled != UnchangedValue.IsEnabled;
            anyChanges |= ImageUri != UnchangedValue.ImageUri;
            anyChanges |= ModDisplayName != UnchangedValue.ModDisplayName;
            anyChanges |= ModUrl != UnchangedValue.ModUrl;
            anyChanges |= ModIniPath != UnchangedValue.ModIniPath;
            anyChanges |= IgnoreMergedIni != UnchangedValue.IgnoreMergedIni;

            if (KeySwaps.Count != UnchangedValue.KeySwaps.Count)
                return true;

            if (anyChanges)
                return true;

            anyChanges |= AnyKeySwapChanges();

            return anyChanges;
        }
    }

    private bool AnyKeySwapChanges()
    {
        var anyChanges = false;

        if (UnchangedValue is null)
            return false;

        if (UnchangedValue.IgnoreMergedIni)
            return false;

        if (KeySwaps.Count != UnchangedValue.KeySwaps.Count)
            return true;

        for (var i = 0; i < KeySwaps.Count; i++)
        {
            var oldKeySwap = UnchangedValue.KeySwaps[i];
            var newKeySwap = KeySwaps[i];

            anyChanges |= (oldKeySwap.ForwardHotkey ?? "") != (newKeySwap.ForwardHotkey ?? "");
            anyChanges |= (oldKeySwap.BackwardHotkey ?? "") != (newKeySwap.BackwardHotkey ?? "");
        }

        return anyChanges;
    }
}

[DebuggerDisplay("Section: {_sectionKey} - {_forwardHotkey} - {_backwardHotkey}")]
public partial class ModPaneFieldsKeySwapVm : ObservableObject
{
    [ObservableProperty] private string _sectionKey = string.Empty;

    [ObservableProperty] private string? _condition;
    [ObservableProperty] private string? _forwardHotkey;
    [ObservableProperty] private string? _backwardHotkey;
    [ObservableProperty] private string? _type;
    [ObservableProperty] private string _variationsCount = "Unknown";

    /// <summary>
    /// 只给界面展示用的段名：把含方括号的原始段名翻译成中文，如 "[KeySwapTextures]" → "切换贴图"。
    /// 注意不要改成直接本地化 <see cref="SectionKey"/> —— 它是回写 ini 的数据源
    /// （见 ModPaneVM 保存逻辑），翻译后会写坏 mod 的 ini。
    /// </summary>
    public string SectionKeyDisplay =>
        Core.Entities.Mods.Helpers.KeyBindingLabelLocalizer.LocalizeSectionName(SectionKey);
}