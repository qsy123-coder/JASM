using System.Reflection;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;
using GIMI_ModManager.Core.GamesService;
using GIMI_ModManager.Core.Services.OilGloss;
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
    InGameMenuService inGameMenuService)
{
    private static readonly ILogger Logger = Log.ForContext<OilGlossService>();

    /// <summary>当前游戏是否支持油光（只有鸣潮）。</summary>
    public bool IsSupported => string.Equals(gameService.GameShortName, "WuWa", StringComparison.OrdinalIgnoreCase);

    public OilGlossStatus GetStatus(ISkinMod mod) => OilGlossPatcher.GetStatus(mod.FullPath);

    public OilGlossScanResult Scan(ISkinMod mod) => OilGlossScanner.Scan(mod.FullPath);

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

        if (result.Status == OilGlossApplyStatus.Applied)
            await RegenerateMenuIfPresentAsync(mod, cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>撤销油光，并在菜单已生成时重建（让三行油光消失）。</summary>
    public async Task<OilGlossUndoResult> DisableAsync(ISkinMod mod, CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(() => OilGlossPatcher.Undo(mod.FullPath), cancellationToken)
            .ConfigureAwait(false);

        if (result.Success)
            await RegenerateMenuIfPresentAsync(mod, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task RegenerateMenuIfPresentAsync(ISkinMod mod, CancellationToken cancellationToken)
    {
        if (!inGameMenuService.HasGeneratedMenu(mod))
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