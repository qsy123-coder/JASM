using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;
using GIMI_ModManager.Core.Entities.Mods.SkinMod;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModHandling;

/// <summary>批量补齐的进度（按 Mod 列表，一个角色一次）。</summary>
public sealed record InGameMenuBatchProgress(int Done, int Total, string CharacterName);

/// <summary>批量补齐里单个 Mod 的结果。</summary>
public sealed record InGameMenuBatchItem(
    Guid ModId,
    string ModName,
    string CharacterName,
    InGameMenuGenerationStatus Status,
    string Reason);

public sealed record InGameMenuBatchResult(
    int Generated,
    int Skipped,
    int Failed,
    IReadOnlyList<InGameMenuBatchItem> Items);

/// <summary>
/// 游戏内切换菜单（<c>JASM_Menu.ini</c>）的应用层服务：生成、移除、批量补齐。
///
/// 规则（来自需求确认）：同一个 Mod 列表里只允许「当前生效」的那一件带菜单；
/// 多件同时启用时一律跳过，避免多套菜单叠在屏幕上。已经有作者可点击菜单的 Mod 由生成器跳过。
///
/// 生成是**同步文件 IO + GDI 文字渲染**，调用方应放到后台线程；批量入口自己串行执行。
/// </summary>
public sealed class InGameMenuService(
    ISkinManagerService skinManagerService,
    ILocalSettingsService localSettingsService)
{
    private static readonly ILogger Logger = Log.ForContext<InGameMenuService>();

    /// <summary>批量补齐同一时间只跑一份，避免用户在设置页连点。</summary>
    private readonly SemaphoreSlim _batchGate = new(1, 1);

    public async Task<InGameMenuSettings> ReadSettingsAsync()
    {
        var settings = await localSettingsService
            .ReadSettingAsync<InGameMenuSettings>(InGameMenuSettings.Key, SettingScope.App)
            .ConfigureAwait(false);

        return settings ?? new InGameMenuSettings();
    }

    public Task SaveSettingsAsync(InGameMenuSettings settings)
        => localSettingsService.SaveSettingAsync(InGameMenuSettings.Key, settings, SettingScope.App);

    /// <summary>这个 Mod 目录里是否已经有 JASM 生成的菜单。</summary>
    public bool HasGeneratedMenu(ISkinMod mod) => InGameMenuGenerator.IsGenerated(mod.FullPath);

    /// <summary>菜单是否存在且由当前生成器版本产出（旧版本菜单需要重建）。</summary>
    public bool HasCurrentMenu(ISkinMod mod) => InGameMenuGenerator.IsUpToDate(mod.FullPath);

    /// <summary>为一个 Mod 生成菜单。调用方负责后台线程。</summary>
    public InGameMenuGenerationResult GenerateForMod(ISkinMod mod, InGameMenuSettings settings)
        => InGameMenuGenerator.Generate(mod.FullPath, ToOptions(settings, mod));

    /// <summary>读取这个 Mod 的所有可解析行（用于「自定义菜单行」对话框）。</summary>
    public IReadOnlyList<ModIniToggleRow> ReadRows(ISkinMod mod)
        => ModIniToggleParser.ParseDirectory(mod.FullPath);

    /// <summary>读取这个 Mod 的行布局偏好；没有配置时返回空布局。</summary>
    public InGameMenuRowLayout GetRowLayout(ISkinMod mod, InGameMenuSettings settings)
        => settings.RowLayouts.TryGetValue(LayoutKey(mod), out var layout)
            ? layout
            : new InGameMenuRowLayout();

    /// <summary>保存行布局并落盘。</summary>
    public async Task SaveRowLayoutAsync(ISkinMod mod, InGameMenuRowLayout layout)
    {
        var settings = await ReadSettingsAsync().ConfigureAwait(false);
        settings.RowLayouts[LayoutKey(mod)] = layout;
        await SaveSettingsAsync(settings).ConfigureAwait(false);
    }

    /// <summary>移除这个 Mod 的 JASM 菜单（只删自己认得的产物）。</summary>
    public bool RemoveForMod(ISkinMod mod) => InGameMenuGenerator.Remove(mod.FullPath);

    /// <summary>
    /// 扫描全部 Mod 列表，给「当前生效」的 Mod 补菜单。
    /// 已经是当前生成器版本的菜单直接跳过；旧版本菜单（例如 v2 的鼠标命中缺陷）会自动重建。
    /// </summary>
    public async Task<InGameMenuBatchResult> GenerateForAllAsync(
        IProgress<InGameMenuBatchProgress>? progress = null,
        bool regenerate = false,
        CancellationToken cancellationToken = default)
    {
        await _batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = await ReadSettingsAsync().ConfigureAwait(false);
            if (!settings.Enabled)
            {
                Logger.Information("[InGameMenu] 已关闭，批量补齐跳过");
                return new InGameMenuBatchResult(0, 0, 0, []);
            }

            var items = new List<InGameMenuBatchItem>();
            var lists = skinManagerService.CharacterModLists.ToList();
            var done = 0;

            foreach (var list in lists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                done++;

                var characterName = list.Character.DisplayName;
                progress?.Report(new InGameMenuBatchProgress(done, lists.Count, characterName));

                var enabled = list.Mods.Where(entry => entry.IsEnabled).Select(entry => entry.Mod).ToList();

                if (enabled.Count == 0)
                    continue;

                if (enabled.Count > 1)
                {
                    foreach (var mod in enabled)
                    {
                        items.Add(new InGameMenuBatchItem(mod.Id, mod.Name, characterName,
                            InGameMenuGenerationStatus.SkippedMultipleEnabledMods,
                            "同一角色启用了多件 Mod，只给当前生效的那一件生成菜单"));
                    }

                    continue;
                }

                var target = enabled[0];

                // 常驻前置/后处理类 Mod 没有 $object_detected：菜单无法按角色门控，
                // 会在任何场景一直显示并与角色菜单叠加，批量补齐直接跳过。
                if (!InGameMenuGenerator.HasObjectDetectedVariable(target.FullPath))
                {
                    items.Add(new InGameMenuBatchItem(target.Id, target.Name, characterName,
                        InGameMenuGenerationStatus.SkippedNoObjectDetected,
                        "常驻 Mod（无 $object_detected），菜单无法按角色门控，已跳过"));
                    continue;
                }

                if (!regenerate && HasCurrentMenu(target))
                {
                    items.Add(new InGameMenuBatchItem(target.Id, target.Name, characterName,
                        InGameMenuGenerationStatus.SkippedAlreadyGenerated, "菜单已存在且为当前版本"));
                    continue;
                }

                InGameMenuGenerationResult result;
                try
                {
                    result = GenerateForMod(target, settings);
                }
                catch (Exception e)
                {
                    Logger.Warning(e, "[InGameMenu] 生成失败：{Mod}", target.FullPath);
                    result = new InGameMenuGenerationResult(
                        InGameMenuGenerationStatus.Failed, null, null, 0, 0, e.Message);
                }

                items.Add(new InGameMenuBatchItem(target.Id, target.Name, characterName, result.Status, result.Reason));

                if (result.Status == InGameMenuGenerationStatus.Generated)
                    Logger.Information("[InGameMenu] 已生成：{Mod}（{Reason}）", target.Name, result.Reason);
            }

            return new InGameMenuBatchResult(
                items.Count(i => i.Status == InGameMenuGenerationStatus.Generated),
                items.Count(i => i.Status != InGameMenuGenerationStatus.Generated
                                 && i.Status != InGameMenuGenerationStatus.Failed),
                items.Count(i => i.Status == InGameMenuGenerationStatus.Failed),
                items);
        }
        finally
        {
            _batchGate.Release();
        }
    }

    private static InGameMenuOptions ToOptions(InGameMenuSettings settings, ISkinMod mod)
    {
        var layout = settings.RowLayouts.TryGetValue(LayoutKey(mod), out var configured)
            ? configured
            : null;

        return new InGameMenuOptions
        {
            OpenKeyTokens = string.IsNullOrWhiteSpace(settings.OpenKeyTokens) ? "F7" : settings.OpenKeyTokens,
            OpenKeyDisplay = string.IsNullOrWhiteSpace(settings.OpenKeyDisplay) ? "F7" : settings.OpenKeyDisplay,
            MaxRowsPerPage = Math.Clamp(settings.MaxRowsPerPage, 1, 20),
            RowOrder = layout?.Order ?? [],
            HiddenRows = layout?.Hidden ?? [],
            Theme = MenuThemes.Get(settings.MenuTheme),
        };
    }

    /// <summary>布局设置的键：Mod 目录名（不含 DISABLED_ 前缀，避免启停后配置失效）。</summary>
    private static string LayoutKey(ISkinMod mod)
        => ModFolderHelpers.GetFolderNameWithoutDisabledPrefix(
            Path.GetFileName(mod.FullPath.TrimEnd(Path.DirectorySeparatorChar)));
}
