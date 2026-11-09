using System.Globalization;
using System.Reflection;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Contracts.Services;
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
    UserPreferencesService userPreferencesService,
    ISkinManagerService skinManagerService)
{
    private static readonly ILogger Logger = Log.ForContext<OilGlossService>();

    /// <summary>
    /// 本体升级 / 旧补丁重打都会改写 Mod 文件与 manifest；串行化，避免详情页、浮窗、
    /// 开启操作并发写同一份。
    /// </summary>
    private readonly object _stateLock = new();

    /// <summary>本体里的强度变量（全局，所有油光 Mod 共用）。</summary>
    private const string OilStrengthVariable = @"$\katfxmatcap\oilstrength";

    /// <summary>本体里的贴图样式变量（全局，所有油光 Mod 共用；0 = 关）。</summary>
    private const string OilSkinVariable = @"$\katfxmatcap\oilskin";

    /// <summary>贴图默认值：MC6（本体 ini 的默认值）。</summary>
    private const string DefaultOilSkin = "2";

    /// <summary>本次会话是否已经检查过全局油光变量（打开详情页/浮窗时只做一次）。</summary>
    private bool _bodyVariablesChecked;

    /// <summary>当前游戏是否支持油光（只有鸣潮）。</summary>
    public bool IsSupported => string.Equals(gameService.GameShortName, "WuWa", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 启动时调用：库里有已开启油光的 Mod 时，检查并复位全局 <c>$oilSkin</c> / <c>$oilstrength</c>。
    /// 只扫描 <c>JASM_Oil/manifest.json</c>；没有油光 Mod 时完全不碰 <c>d3dx_user.ini</c>。
    /// </summary>
    public async Task EnsureGlobalBodyVariablesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || !HasAnyOilManifest())
            return;

        await ResetBodyVariablesAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool HasAnyOilManifest()
    {
        try
        {
            var root = skinManagerService.ActiveModsFolderPath;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return false;

            return Directory.EnumerateFiles(root, OilGlossProtocol.ManifestFileName, SearchOption.AllDirectories)
                .Any(path => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)),
                    OilGlossProtocol.StateDirectoryName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public OilGlossStatus GetStatus(ISkinMod mod)
    {
        var status = OilGlossPatcher.GetStatus(mod.FullPath);

        // 旧版补丁（身体/衣物两组、包含 Component0–2）先按新规则重打；
        // 旧版本体（ps-t0 替换）再升级。两步都只在打开详情页/浮窗刷新时触发一次。
        status = RepatchIfOutdated(mod, status);
        UpgradeBodyIfNeeded(mod, status.Manifest);

        // 原版热键（* / -）会把全局 $oilSkin / $oilStrength 存成 0。
        // 这两个变量是所有油光 Mod 共用的，一旦为 0，菜单照常显示但画面没有任何油光。
        // 每次启动/打开油光 Mod 时检查一次，只在 0/缺失时复位，不动用户选的样式/强度。
        if (!_bodyVariablesChecked && status.State == OilGlossState.Applied)
        {
            _bodyVariablesChecked = true;
            _ = ResetBodyVariablesAsync(CancellationToken.None);
        }

        return status;
    }

    /// <summary>
    /// 旧补丁升级：撤销原补丁 → 用当前扫描规则重打。
    ///
    /// 只处理 <see cref="OilGlossState.Applied"/> 且 <c>PatchVersion</c> 落后的 manifest；
    /// Conflict/Broken 一律不动（用户改过或产物缺失，撤销可能覆盖用户改动）。
    /// </summary>
    private OilGlossStatus RepatchIfOutdated(ISkinMod mod, OilGlossStatus status)
    {
        if (status.State != OilGlossState.Applied
            || status.Manifest is not { } manifest
            || manifest.PatchVersion >= OilGlossProtocol.PatchVersion)
        {
            return status;
        }

        lock (_stateLock)
        {
            var current = OilGlossPatcher.GetStatus(mod.FullPath);
            if (current.State != OilGlossState.Applied
                || current.Manifest is not { } currentManifest
                || currentManifest.PatchVersion >= OilGlossProtocol.PatchVersion)
            {
                return current;
            }

            Logger.Information("[OilGloss] 升级旧补丁: {Mod}", mod.Name);

            var undo = OilGlossPatcher.Undo(mod.FullPath);
            if (!undo.Success)
            {
                Logger.Warning("[OilGloss] 旧补丁升级失败(撤销): {Mod} {Reason}", mod.Name, undo.Reason);
                return current;
            }

            var body = OilGlossBodyProvisioner.Ensure(mod.FullPath, EmbeddedOilGlossAssetSource.Instance);
            if (!body.Success)
            {
                Logger.Warning("[OilGloss] 旧补丁升级失败(本体): {Mod} {Reason}", mod.Name, body.Reason);
                return OilGlossPatcher.GetStatus(mod.FullPath);
            }

            var applied = OilGlossPatcher.Apply(mod.FullPath, body: body);

            // 游戏占用旧本体时 Undo 删不掉、Ensure 也换不掉（BodyProvidedByJasm=false）。
            // 旧 manifest 明确记录过这是 JASM 写的，就保留所有权与旧版本，
            // 让下一次 GetStatus 在游戏退出后继续升级，而不是把旧本体当成作者的。
            if (applied.Manifest is { } newManifest
                && !body.BodyProvidedByJasm
                && (currentManifest.BodyProvidedByJasm
                    || OilGlossBodyProvisioner.IsOutdatedKnownJasmBody(mod.FullPath)))
            {
                newManifest.BodyProvidedByJasm = true;
                // 本体还是旧的，强制下一次 GetStatus 继续升级
                newManifest.BodyVersion = 0;
                newManifest.BodyFiles = currentManifest.BodyFiles ?? [];

                try
                {
                    OilGlossPatcher.WriteManifest(mod.FullPath, newManifest);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Logger.Warning(e, "[OilGloss] 旧补丁升级后回写清单失败: {Mod}", mod.Name);
                }
            }

            // 新规则下没有任何可包网格（例如纯发型 Mod）：撤销后保持未开启，
            // 并清掉这次刚复制的本体，别在 Mod 目录留孤儿文件。
            if (applied.Status is OilGlossApplyStatus.Failed or OilGlossApplyStatus.NoDrawcalls
                && body.WrittenFiles.Count > 0)
            {
                try
                {
                    OilGlossBodyProvisioner.RemoveWrittenFiles(mod.FullPath, body.WrittenFiles);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Logger.Warning(e, "[OilGloss] 旧补丁升级后清理本体失败: {Mod}", mod.Name);
                }
            }

            Logger.Information("[OilGloss] 旧补丁升级完成({Mod}): {Status}", mod.Name, applied.Status);

            // 旧菜单里可能还留着「身体油光 / 衣物油光」两行；重打后行模型只剩
            // 总开关 + 强度，菜单必须重建（没有作者行时会被移除）。
            _ = RegenerateMenuAsync(mod, createIfMissing: false, CancellationToken.None);

            return OilGlossPatcher.GetStatus(mod.FullPath);
        }
    }

    public OilGlossScanResult Scan(ISkinMod mod) => OilGlossScanner.Scan(mod.FullPath);

    /// <summary>读取全局油光强度（0..<see cref="OilGlossProtocol.MaxStrength"/>，默认 0.7）。</summary>
    public async Task<double> GetStrengthAsync(CancellationToken cancellationToken = default)
    {
        var settings = await localSettingsService
            .ReadSettingAsync<OilGlossSettings>(OilGlossSettings.Key, SettingScope.App)
            .ConfigureAwait(false);

        return Math.Clamp(settings?.Strength ?? OilGlossProtocol.DefaultStrength, 0,
            OilGlossProtocol.MaxStrength);
    }

    /// <summary>
    /// 保存全局油光强度：写 JASM 设置 + 写 3DMigoto 的 <c>d3dx_user.ini</c>
    /// （<c>$\katfxmatcap\oilstrength</c>）。游戏需要 F10 重新读取后生效。
    /// </summary>
    public async Task<bool> SaveStrengthAsync(double strength, CancellationToken cancellationToken = default)
    {
        var value = Math.Clamp(strength, 0, OilGlossProtocol.MaxStrength);

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
            UpgradeBodyIfNeeded(mod, OilGlossPatcher.GetStatus(mod.FullPath).Manifest);

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
        {
            await ResetBodyVariablesAsync(cancellationToken).ConfigureAwait(false);
            await RegenerateMenuAsync(mod, createIfMissing: true, cancellationToken).ConfigureAwait(false);
        }

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

        // 先校验选择，再动旧补丁：没有任何选中网格时不能先把原补丁撤销掉。
        var selected = draws.Where(d => d.Group is not null).ToList();
        if (selected.Count == 0)
        {
            return new OilGlossApplyResult
            {
                Status = OilGlossApplyStatus.NoDrawcalls,
                Reason = "没有选择任何网格",
            };
        }

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

        var scan = new OilGlossScanResult
        {
            Drawcalls = draws,
            Body = selected.OrderByDescending(d => d.IndexCount).First(),
            Cloth = null,
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
            await ResetBodyVariablesAsync(cancellationToken).ConfigureAwait(false);
            await RegenerateMenuAsync(mod, createIfMissing: createMenuIfMissing, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 重打补丁后把总开关复位为「开」。
    /// 这个变量是 persist 的，3DMigoto 会把上一次 F7 里的选择存进 d3dx_user.ini。
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
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.Warning(e, "[OilGloss] 复位油光开关失败");
        }
    }

    /// <summary>
    /// 复位所有油光 Mod 共用的 <c>$\katfxmatcap\oilSkin</c> / <c>oilstrength</c>。
    ///
    /// 原版 KatFXMatcap 的 <c>*</c> / <c>-</c> 热键会把这两个 persist 变量存成 0；
    /// 一旦为 0，F7 菜单里的油光行照常显示，但画面没有任何油光 —— 这是「有菜单没效果」
    /// 最常见的原因。只在变量缺失或 &lt;= 0 时复位，不覆盖用户选择的贴图/强度。
    /// 写入 d3dx_user.ini 后需要 F10 或重启游戏才会被 3DMigoto 重新读取。
    /// </summary>
    private async Task ResetBodyVariablesAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
            return;

        try
        {
            var skin = await userPreferencesService
                .GetGlobalVariableAsync(OilSkinVariable, cancellationToken)
                .ConfigureAwait(false);
            if (!IsPositiveNumber(skin))
            {
                await userPreferencesService
                    .SetGlobalVariableAsync(OilSkinVariable, DefaultOilSkin, cancellationToken)
                    .ConfigureAwait(false);
            }

            var strengthText = await userPreferencesService
                .GetGlobalVariableAsync(OilStrengthVariable, cancellationToken)
                .ConfigureAwait(false);
            if (!IsPositiveNumber(strengthText))
            {
                var strength = await GetStrengthAsync(cancellationToken).ConfigureAwait(false);
                if (strength <= 0)
                    strength = OilGlossProtocol.DefaultStrength;

                await userPreferencesService
                    .SetGlobalVariableAsync(OilStrengthVariable,
                        strength.ToString("0.###", CultureInfo.InvariantCulture), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            // 维护任务：失败只记日志，不能因为 d3dx_user.ini 读写出问题影响详情页/浮窗
            Logger.Warning(e, "[OilGloss] 复位全局油光变量失败");
        }
    }

    private static bool IsPositiveNumber(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0;

    /// <summary>
    /// 把 JASM 写过的旧版本体升级到当前版本。
    ///
    /// 只覆盖 manifest 里 <c>BodyProvidedByJasm = true</c> 且当前哈希与记录一致的文件；
    /// 作者自带本体、用户改过的文件都不会被覆盖。失败（游戏占用等）留给下次状态刷新重试。
    /// </summary>
    private void UpgradeBodyIfNeeded(ISkinMod mod, OilGlossManifest? manifest)
    {
        if (manifest is null)
            return;

        lock (_stateLock)
        {
            // 锁内重新读一次，避免把并发写入的新 manifest（例如刚打完补丁）覆盖回旧内容
            var current = OilGlossPatcher.GetStatus(mod.FullPath).Manifest;
            if (current is null)
                return;

            try
            {
                // Upgrade 自己判断：作者本体 / 已是最新会返回 Skipped；
                // 已知旧 JASM 本体即使 manifest 丢失所有权也会升级。
                var upgrade = OilGlossBodyProvisioner.Upgrade(
                    mod.FullPath, EmbeddedOilGlossAssetSource.Instance, current);

                if (!upgrade.Success)
                {
                    Logger.Warning("[OilGloss] 本体升级失败({Mod}): {Reason}", mod.Name, upgrade.Reason);
                    return;
                }

                if (upgrade.Skipped)
                    return;

                current.BodyProvidedByJasm = true;
                current.BodyFiles = upgrade.Files.ToList();
                current.BodyVersion = OilGlossProtocol.BodyVersion;
                OilGlossPatcher.WriteManifest(mod.FullPath, current);

                if (upgrade.PreservedFiles.Count > 0)
                {
                    Logger.Warning("[OilGloss] 本体升级保留了用户修改的文件({Mod}): {Files}",
                        mod.Name, string.Join(", ", upgrade.PreservedFiles));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 游戏运行时会占用 Matcap.ini；下次刷新/开启时再试
                Logger.Warning(e, "[OilGloss] 本体升级失败: {Mod}", mod.Name);
            }
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
            var emptyShell = result.Status is InGameMenuGenerationStatus.SkippedNoRows
                                 or InGameMenuGenerationStatus.SkippedNoClickableRows
                             && !inGameMenuService.HasMenuRows(mod);

            // 作者本身有可点击菜单：撤销油光后生成器会跳过。此时 JASM 菜单只是油光补丁
            // 带出来的第二份菜单（可能只剩失效的油光行），一并删掉。
            var oilRemoved = OilGlossPatcher.GetStatus(mod.FullPath).Manifest is null;
            var staleOilMenu = result.Status == InGameMenuGenerationStatus.SkippedExistingClickableMenu
                               && oilRemoved;

            if (emptyShell || staleOilMenu)
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