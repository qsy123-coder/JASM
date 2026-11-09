using System.Globalization;
using System.Reflection;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.Services;
using GIMI_ModManager.Core.Services.OilGloss;
using GIMI_ModManager.WinUI.Contracts.Services;
using GIMI_ModManager.WinUI.Models.Settings;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.ModHandling;

/// <summary>
/// 详情页「油光」入口的应用层服务：本体资源、补丁、状态，以及补丁后的菜单重建。
///
/// 只支持鸣潮（WWMI）：KatFXMatcap 的贴图槽与 shader 正则按鸣潮适配，
/// 其它游戏即便结构相似也不保证效果，因此入口直接不显示。
/// </summary>
public sealed class OilGlossService(
    IGameService gameService,
    InGameMenuService inGameMenuService,
    ILocalSettingsService localSettingsService,
    UserPreferencesService userPreferencesService)
{
    private static readonly ILogger Logger = Log.ForContext<OilGlossService>();

    /// <summary>本体里的强度变量（全局，所有油光 Mod 共用）。</summary>
    private const string OilStrengthVariable = @"$\katfxmatcap\oilstrength";

    /// <summary>当前游戏是否支持油光（只有鸣潮）。</summary>
    public bool IsSupported => string.Equals(gameService.GameShortName, "WuWa", StringComparison.OrdinalIgnoreCase);

    public OilGlossStatus GetStatus(ISkinMod mod) => OilGlossPatcher.GetStatus(mod.FullPath);

    public OilGlossScanResult Scan(ISkinMod mod) => OilGlossScanner.Scan(mod.FullPath);

    /// <summary>读取全局油光强度（0..1，默认 0.7）。</summary>
    public async Task<double> GetStrengthAsync(CancellationToken cancellationToken = default)
    {
        var settings = await localSettingsService
            .ReadSettingAsync<OilGlossSettings>(OilGlossSettings.Key, SettingScope.App)
            .ConfigureAwait(false);

        return Math.Clamp(settings?.Strength ?? 0.7, 0, 1);
    }

    /// <summary>
    /// 保存全局油光强度：写 JASM 设置 + 写 3DMigoto 的 <c>d3dx_user.ini</c>
    /// （<c>$\katfxmatcap\oilstrength</c>）。游戏需要 F10 重新读取后生效。
    /// </summary>
    public async Task<bool> SaveStrengthAsync(double strength, CancellationToken cancellationToken = default)
    {
        var value = Math.Clamp(strength, 0, 1);

        var settings = await localSettingsService
                           .ReadSettingAsync<OilGlossSettings>(OilGlossSettings.Key, SettingScope.App)
                           .ConfigureAwait(false)
                       ?? new OilGlossSettings();

        settings.Strength = value;
        await localSettingsService.SaveSettingAsync(OilGlossSettings.Key, settings, SettingScope.App)
            .ConfigureAwait(false);

        return await userPreferencesService
            .SetGlobalVariableAsync(OilStrengthVariable,
                value.ToString("0.###", CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// 开启油光：复制本体（已有则接管）→ 自动分组 → 打补丁 → 若菜单已生成则重建。
    /// 文件 IO 与菜单生成都是同步阻塞操作，调用方应放到后台线程。
    /// </summary>
    public async Task<OilGlossApplyResult> EnableAsync(ISkinMod mod, CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return new OilGlossApplyResult
            {
                Status = OilGlossApplyStatus.Failed,
                Reason = "油光只支持鸣潮（WWMI）",
            };
        }

        var result = await Task.Run(() =>
        {
            var body = OilGlossBodyProvisioner.Ensure(mod.FullPath, EmbeddedOilGlossAssetSource.Instance);
            if (!body.Success)
            {
                return new OilGlossApplyResult
                {
                    Status = OilGlossApplyStatus.Failed,
                    Reason = body.Reason,
                };
            }

            var applied = OilGlossPatcher.Apply(mod.FullPath, body: body);

            // 补丁没写成（没有 drawcall / 被拒绝 / IO 失败）时，把这次刚复制的本体也撤掉，
            // 不给用户的 Mod 目录留下孤儿文件。
            if (applied.Status is OilGlossApplyStatus.Failed or OilGlossApplyStatus.NoDrawcalls
                && body.WrittenFiles.Count > 0)
            {
                try
                {
                    OilGlossBodyProvisioner.RemoveWrittenFiles(mod.FullPath, body.WrittenFiles);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Logger.Warning(e, "[OilGloss] 清理未完成补丁的本体失败: {Mod}", mod.Name);
                }
            }

            return applied;
        }, cancellationToken).ConfigureAwait(false);

        // 加油光后**必须**有菜单：三行油光只在菜单里。哪怕这个 Mod 完全没有 [Key*]，
        // 也顺手生成一份只含油光行的菜单，用户不用再手动点「生成菜单」。
        if (result.Status == OilGlossApplyStatus.Applied)
            await RegenerateMenuAsync(mod, createIfMissing: true, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>撤销油光，并在菜单已生成时重建（让三行油光消失）。</summary>
    public async Task<OilGlossUndoResult> DisableAsync(ISkinMod mod, CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(() => OilGlossPatcher.Undo(mod.FullPath), cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
            await RegenerateMenuAsync(mod, createIfMissing: false, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// 按用户在详情页选好的分组重新打补丁（可只包部分网格，或从发型 Mod 里挑出身体）。
    /// </summary>
    public Task<OilGlossApplyResult> ApplyCustomGroupsAsync(
        ISkinMod mod,
        IReadOnlyList<OilGlossDrawcall> draws,
        CancellationToken cancellationToken = default)
        => ApplyGroupsAsync(mod, draws, createMenuIfMissing: true, cancellationToken);

    /// <summary>
    /// 逐块识别的「试亮」：只给这一块上油光（归到身体组），其余网格全部不包。
    /// 不新建菜单（试亮可能只是临时看一看），由调用方刷新游戏（F10）。
    /// </summary>
    public Task<OilGlossApplyResult> TrialBodyAsync(
        ISkinMod mod,
        OilGlossDrawcall draw,
        CancellationToken cancellationToken = default)
        => ApplyGroupsAsync(mod, [draw with { Group = OilGlossGroup.Body }],
            createMenuIfMissing: false, cancellationToken);

    /// <summary>按记录下来的分组恢复补丁（「逐块识别」取消时用）。</summary>
    public Task<OilGlossApplyResult> RestoreGroupsAsync(
        ISkinMod mod,
        IReadOnlyList<OilGlossDrawcall> draws,
        CancellationToken cancellationToken = default)
        => ApplyGroupsAsync(mod, draws, createMenuIfMissing: false, cancellationToken);

    private async Task<OilGlossApplyResult> ApplyGroupsAsync(
        ISkinMod mod,
        IReadOnlyList<OilGlossDrawcall> draws,
        bool createMenuIfMissing,
        CancellationToken cancellationToken)
    {
        if (!IsSupported)
            return new OilGlossApplyResult { Status = OilGlossApplyStatus.Failed, Reason = "油光只支持鸣潮（WWMI）" };

        var status = OilGlossPatcher.GetStatus(mod.FullPath);
        if (status.State is OilGlossState.Applied or OilGlossState.Conflict or OilGlossState.Broken)
        {
            var undo = await Task.Run(() => OilGlossPatcher.Undo(mod.FullPath), cancellationToken)
                .ConfigureAwait(false);
            if (!undo.Success)
            {
                return new OilGlossApplyResult
                {
                    Status = OilGlossApplyStatus.Failed,
                    Reason = "撤销原补丁失败：" + undo.Reason,
                };
            }
        }

        var bodyDraw = draws.Where(d => d.Group == OilGlossGroup.Body)
            .OrderByDescending(d => d.IndexCount).FirstOrDefault();
        var clothDraw = draws.Where(d => d.Group == OilGlossGroup.Cloth)
            .OrderByDescending(d => d.IndexCount).FirstOrDefault();

        if (bodyDraw is null && clothDraw is null)
        {
            return new OilGlossApplyResult
            {
                Status = OilGlossApplyStatus.NoDrawcalls,
                Reason = "没有选择任何网格",
            };
        }

        var scan = new OilGlossScanResult
        {
            Drawcalls = draws,
            Body = bodyDraw,
            Cloth = clothDraw,
        };

        var result = await Task.Run(() =>
        {
            var provision = OilGlossBodyProvisioner.Ensure(mod.FullPath, EmbeddedOilGlossAssetSource.Instance);
            if (!provision.Success)
            {
                return new OilGlossApplyResult
                {
                    Status = OilGlossApplyStatus.Failed,
                    Reason = provision.Reason,
                };
            }

            return OilGlossPatcher.Apply(mod.FullPath, scan, provision);
        }, cancellationToken).ConfigureAwait(false);

        if (result.Status == OilGlossApplyStatus.Applied)
        {
            await ResetGroupTogglesAsync(result.Manifest, cancellationToken).ConfigureAwait(false);
            await RegenerateMenuAsync(mod, createIfMissing: createMenuIfMissing, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 重打补丁后把开关复位成「总开关开、身体开、衣物关」。
    /// 这三个变量是 persist 的，3DMigoto 会把上一次 F7 里的选择存进 d3dx_user.ini；
    /// 如果之前把衣物打开过，重新开启油光后头发/脸部（常被分到衣物组）会继续变色。
    /// 游戏没在运行时写不进去也不影响：变量缺失时用 JASM_Oil.ini 里的默认值。
    /// </summary>
    private async Task ResetGroupTogglesAsync(OilGlossManifest? manifest, CancellationToken cancellationToken)
    {
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Namespace))
            return;

        var prefix = @"$\" + manifest.Namespace + @"\";
        try
        {
            await userPreferencesService
                .SetGlobalVariableAsync(prefix + OilGlossProtocol.MasterVariable, "1", cancellationToken)
                .ConfigureAwait(false);
            await userPreferencesService
                .SetGlobalVariableAsync(prefix + OilGlossProtocol.BodyVariable, "1", cancellationToken)
                .ConfigureAwait(false);
            await userPreferencesService
                .SetGlobalVariableAsync(prefix + OilGlossProtocol.ClothVariable, "0", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(e, "[OilGloss] 复位油光分组开关失败");
        }
    }

    private async Task RegenerateMenuAsync(ISkinMod mod, bool createIfMissing, CancellationToken cancellationToken)
    {
        if (!createIfMissing && !inGameMenuService.HasGeneratedMenu(mod))
            return;

        try
        {
            var settings = await inGameMenuService.ReadSettingsAsync().ConfigureAwait(false);
            var result = await Task.Run(() => inGameMenuService.GenerateForMod(mod, settings), cancellationToken)
                .ConfigureAwait(false);

            // 有些 Mod 的菜单完全靠油光三行撑着（没有任何作者 [Key*]）。
            // 撤销油光后生成器会因「没有行」而跳过，旧菜单文件会留下三行指向已删除变量，
            // 所以这里把这种空壳菜单直接移除。
            if (result.Status is InGameMenuGenerationStatus.SkippedNoRows
                    or InGameMenuGenerationStatus.SkippedNoClickableRows
                && !inGameMenuService.HasMenuRows(mod))
            {
                await Task.Run(() => inGameMenuService.RemoveForMod(mod), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 补丁本身已经成功，菜单重建失败不应回滚补丁；下次批量补齐会再试
            Logger.Warning(e, "[OilGloss] 补丁后重建菜单失败: {Mod}", mod.Name);
        }
    }

    /// <summary>随包分发的本体资源（内嵌在主程序集里，SingleFile 版也能读）。</summary>
    private sealed class EmbeddedOilGlossAssetSource : IOilGlossAssetSource
    {
        private const string ResourcePrefix = "JASM.OilGloss.";

        public static readonly EmbeddedOilGlossAssetSource Instance = new();

        private static readonly Lazy<IReadOnlyList<string>> LazyPaths = new(() =>
            typeof(EmbeddedOilGlossAssetSource).Assembly
                .GetManifestResourceNames()
                .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                .Select(name => name[ResourcePrefix.Length..].Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList());

        public IReadOnlyList<string> AssetPaths => LazyPaths.Value;

        public Stream? OpenRead(string relativePath)
            => Assembly.GetExecutingAssembly().GetManifestResourceStream(
                ResourcePrefix + relativePath.Replace('/', '\\'));
    }
}