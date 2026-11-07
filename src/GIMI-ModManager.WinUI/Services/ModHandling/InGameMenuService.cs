using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
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

    /// <summary>为一个 Mod 生成菜单。调用方负责后台线程。</summary>
    public InGameMenuGenerationResult GenerateForMod(ISkinMod mod, InGameMenuSettings settings)
        => InGameMenuGenerator.Generate(mod.FullPath, ToOptions(settings));

    /// <summary>移除这个 Mod 的 JASM 菜单（只删自己认得的产物）。</summary>
    public bool RemoveForMod(ISkinMod mod) => InGameMenuGenerator.Remove(mod.FullPath);

    /// <summary>
    /// 扫描全部 Mod 列表，给「当前生效」且还没有菜单的 Mod 补菜单。
    /// 已经在磁盘上有菜单的 Mod 直接跳过（批量不会重复生成纹理）。
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

                if (!regenerate && HasGeneratedMenu(target))
                {
                    items.Add(new InGameMenuBatchItem(target.Id, target.Name, characterName,
                        InGameMenuGenerationStatus.SkippedAlreadyGenerated, "菜单已存在"));
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

    private static InGameMenuOptions ToOptions(InGameMenuSettings settings) => new()
    {
        OpenKeyTokens = string.IsNullOrWhiteSpace(settings.OpenKeyTokens) ? "F8" : settings.OpenKeyTokens,
        OpenKeyDisplay = string.IsNullOrWhiteSpace(settings.OpenKeyDisplay) ? "F8" : settings.OpenKeyDisplay,
        MaxRowsPerPage = Math.Clamp(settings.MaxRowsPerPage, 1, 20),
    };
}
