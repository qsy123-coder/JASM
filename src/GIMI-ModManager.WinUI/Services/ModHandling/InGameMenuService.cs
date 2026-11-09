using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;
using GIMI_ModManager.Core.Entities.Mods.SkinMod;
using GIMI_ModManager.Core.Services.OilGloss;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModHandling;

/// <summary>批量补齐的进度（按角色列表计数；<paramref name="ModName"/> 为当前正在处理的 Mod）。</summary>
public sealed record InGameMenuBatchProgress(int Done, int Total, string CharacterName, string? ModName = null);

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

    /// <summary>
    /// 单次菜单写入的串行闸。批量补齐、启用钩子、详情页按钮可能同时触发同一个/相邻 Mod 的生成，
    /// 并发删目录 + 写文件会互相踩（表现为资产目录只剩 shader/manifest）。这里保证一次只写一个。
    /// </summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

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

    /// <summary>
    /// 这个 Mod 是否有可放进菜单的按键行（与生成器的判定一致：<c>[Key*]</c> + 可循环变量）。
    /// 没有的话生成器只会跳过，菜单也不存在 —— 调用方应提前告诉用户「无法打开菜单」。
    /// </summary>
    public bool HasMenuRows(ISkinMod mod)
        => ModIniToggleParser.ParseDirectory(mod.FullPath).Any(row => row.IsClickable);

    /// <summary>为一个 Mod 生成菜单。调用方负责后台线程；内部串行化，避免并发写同一目录。</summary>
    public InGameMenuGenerationResult GenerateForMod(ISkinMod mod, InGameMenuSettings settings)
    {
        _writeGate.Wait();
        try
        {
            return InGameMenuGenerator.Generate(mod.FullPath, ToOptions(settings, mod));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 启用 / 禁用 Mod 之后调用：给这个角色「当前唯一启用」的那件补/更新菜单。
    ///
    /// 为什么必须在这里补：启用/禁用是改文件夹名（加/去 DISABLED_ 前缀），
    /// <c>CharacterModList</c> 在改名期间会临时关掉 watcher，所以<b>不会</b>触发 ModsChanged；
    /// 如果不主动补，用户「启用新 Mod → 直接开游戏」时那份 Mod 要么没有菜单、
    /// 要么还是旧版本菜单，F7 看起来就不生效。多件启用 / 未启用的情况交给设置页批量补齐。
    /// </summary>
    public async Task RefreshAfterModToggleAsync(ICharacterModList modList)
    {
        try
        {
            var settings = await ReadSettingsAsync().ConfigureAwait(false);
            if (!settings.Enabled)
                return;

            var enabled = modList.Mods.Where(entry => entry.IsEnabled).Select(entry => entry.Mod).ToList();
            if (enabled.Count != 1)
                return;

            var target = enabled[0];

            // 常驻前置/后处理类没有 $object_detected：菜单无法按角色门控，生成器会跳过
            if (!InGameMenuGenerator.HasObjectDetectedVariable(target.FullPath))
                return;

            if (HasCurrentMenu(target))
                return;

            var result = GenerateForMod(target, settings);
            if (result.Status == InGameMenuGenerationStatus.Generated)
                Logger.Information("[InGameMenu] 启用后已生成：{Mod}（{Reason}）", target.Name, result.Reason);
            else
                Logger.Information("[InGameMenu] 启用后未生成：{Mod}（{Status}: {Reason}）",
                    target.Name, result.Status, result.Reason);
        }
        catch (Exception e)
        {
            // 菜单是附加能力：失败只记日志，不能让启用 Mod 本身失败
            Logger.Warning(e, "[InGameMenu] 启用 Mod 后生成菜单失败");
        }
    }

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
    /// 扫描全部 Mod 列表补菜单。默认只给「当前生效」的 Mod 补；
    /// <paramref name="includeDisabledMods"/> 为 true 时（设置页手动「为所有 Mod 补齐」）
    /// 连 DISABLED_ 的 Mod 一起生成——启用后立刻就有现成菜单，不用等下一次启动补齐。
    /// 已经是当前生成器版本的菜单直接跳过；旧版本菜单会自动重建。
    /// </summary>
    public async Task<InGameMenuBatchResult> GenerateForAllAsync(
        IProgress<InGameMenuBatchProgress>? progress = null,
        bool regenerate = false,
        CancellationToken cancellationToken = default,
        bool includeDisabledMods = false)
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

                var enabled = list.Mods.Where(entry => entry.IsEnabled).Select(entry => entry.Mod).ToList();

                List<ISkinMod> targets;
                if (includeDisabledMods)
                {
                    // 手动「为所有 Mod 补齐」：DISABLED_ 的也一起生成。菜单与 Mod 同目录，
                    // 启用时只是去掉前缀、整目录改名，菜单会跟着走；游戏启动时就已就位。
                    targets = list.Mods.Select(entry => entry.Mod).ToList();
                }
                else if (enabled.Count == 0)
                {
                    continue;
                }
                else if (enabled.Count > 1)
                {
                    foreach (var mod in enabled)
                    {
                        items.Add(new InGameMenuBatchItem(mod.Id, mod.Name, characterName,
                            InGameMenuGenerationStatus.SkippedMultipleEnabledMods,
                            "同一角色启用了多件 Mod，只给当前生效的那一件生成菜单"));
                    }

                    continue;
                }
                else
                {
                    targets = [enabled[0]];
                }

                foreach (var target in targets)
                {
                    // 逐 Mod 报进度：一个角色下可能有上百个 Mod，只按角色报会看起来「卡死」
                    progress?.Report(new InGameMenuBatchProgress(done, lists.Count, characterName, target.Name));

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

                    items.Add(new InGameMenuBatchItem(target.Id, target.Name, characterName, result.Status,
                        result.Reason));

                    if (result.Status == InGameMenuGenerationStatus.Generated)
                        Logger.Information("[InGameMenu] 已生成：{Mod}（{Reason}）", target.Name, result.Reason);
                }
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

        // 随包分发的字体：所有用户字形一致（Assets 随发布产物复制）
        var bundledFont = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "zh-cn2.ttf");
        var hasBundledFont = File.Exists(bundledFont);

        // 油光补丁存在时，把「总开关 / 身体 / 衣物」三行固定注入菜单最前面。
        // 用 manifest 而不是补丁文件反推：变量的命名空间与是否有衣物组都记在那里。
        var oilStatus = OilGlossPatcher.GetStatus(mod.FullPath);
        IReadOnlyList<ModIniToggleRow> oilRows =
            oilStatus.Manifest is not null
                ? OilGlossMenuRows.Create(oilStatus.Manifest)
                : [];

        return new InGameMenuOptions
        {
            ExtraRows = oilRows,
            OpenKeyTokens = string.IsNullOrWhiteSpace(settings.OpenKeyTokens) ? "F7" : settings.OpenKeyTokens,
            OpenKeyDisplay = string.IsNullOrWhiteSpace(settings.OpenKeyDisplay) ? "F7" : settings.OpenKeyDisplay,
            MaxRowsPerPage = Math.Clamp(settings.MaxRowsPerPage, 1, 20),
            RowOrder = layout?.Order ?? [],
            HiddenRows = layout?.Hidden ?? [],
            Theme = MenuThemes.Get(settings.MenuTheme),
            ReferenceAssetsDirectory = settings.ReferenceAssetsDirectory ?? string.Empty,
            FontFilePath = hasBundledFont ? bundledFont : string.Empty,
            FontFamily = hasBundledFont ? "HYWenHei-85W" : string.Empty,
        };
    }

    /// <summary>布局设置的键：Mod 目录名（不含 DISABLED_ 前缀，避免启停后配置失效）。</summary>
    private static string LayoutKey(ISkinMod mod)
        => ModFolderHelpers.GetFolderNameWithoutDisabledPrefix(
            Path.GetFileName(mod.FullPath.TrimEnd(Path.DirectorySeparatorChar)));
}