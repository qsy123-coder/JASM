using GIMI_ModManager.Core.Contracts.Services;
using GIMI_ModManager.Core.Services.WuwaUhd;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Injection;

/// <summary>
/// 生成原生映射 DLL（<c>Jasm.UhdMapper.dll</c>，Phase 2）的数据契约
/// <c>%LOCALAPPDATA%\JASM\UhdRuntime\active-mod.json</c>。
///
/// 内容 = 当前角色**所有已启用 mod** 的「HD hash → 资源段 → DDS 路径」。
/// 游戏以 UHD 档位运行时，DLL 用这份清单把 mod 的 HD 覆盖和游戏实际绑定的 UHD hash 配对。
///
/// 只在「内置注入启动」路径上写：没有注入就没有 DLL 消费它。
/// </summary>
public sealed class UhdActiveModService
{
    private readonly ISkinManagerService _skinManagerService;
    private readonly ILogger _logger;

    public UhdActiveModService(ISkinManagerService skinManagerService, ILogger logger)
    {
        _skinManagerService = skinManagerService;
        _logger = logger.ForContext<UhdActiveModService>();
    }

    public static string ManifestPath => UhdActiveModStore.ActiveModPath;

    /// <summary>当前角色的显示名 + 已启用 mod 的根目录（启动前自动补齐 UHD 段用）。</summary>
    public sealed record Selection(string RoleName, IReadOnlyList<string> EnabledModPaths);

    /// <summary>
    /// 解析「当前角色 + 已启用 mod」。角色未选、没有 mod 列表、全部停用时返回 null。
    /// 与 <see cref="WriteForCharacter"/> 使用同一套筛选规则，但不动清单文件。
    /// </summary>
    public Selection? ResolveSelection(string? characterInternalName)
    {
        if (string.IsNullOrWhiteSpace(characterInternalName))
            return null;

        var modList = _skinManagerService.GetCharacterModListOrDefault(characterInternalName);
        if (modList is null)
            return null;

        var enabledModPaths = modList.Mods
            .Where(entry => entry.IsEnabled)
            .Select(entry => entry.Mod.FullPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray();

        return enabledModPaths.Length == 0
            ? null
            : new Selection(modList.Character.DisplayName, enabledModPaths);
    }

    /// <summary>
    /// 为角色写入清单，返回写入路径；找不到角色 / 没有已启用 mod 时清空并返回 null。
    /// 任何 IO 失败都只记日志，绝不让启动流程失败。
    /// </summary>
    public string? WriteForCharacter(string? characterInternalName)
    {
        if (string.IsNullOrWhiteSpace(characterInternalName))
        {
            UhdActiveModStore.Clear();
            return null;
        }

        var modList = _skinManagerService.GetCharacterModListOrDefault(characterInternalName);
        if (modList is null)
        {
            UhdActiveModStore.Clear();
            return null;
        }

        var enabledMods = modList.Mods
            .Where(entry => entry.IsEnabled)
            .Select(entry => (entry.Mod.Name, entry.Mod.FullPath))
            .Where(mod => !string.IsNullOrWhiteSpace(mod.FullPath))
            .ToArray();

        // 全部停用时清单里没有可映射的贴图；清掉旧文件，避免 DLL 继续用上一个 mod 的映射
        if (enabledMods.Length == 0)
        {
            UhdActiveModStore.Clear();
            return null;
        }

        var roleName = modList.Character.DisplayName;
        var roleInternalName = modList.Character.InternalName.ToString();
        var aliases = TryLoadAliases(roleName);

        var manifest = UhdActiveModManifestBuilder.BuildFromDirectory(
            roleName, roleInternalName, enabledMods, aliases);

        try
        {
            var path = UhdActiveModStore.Write(manifest);
            _logger.Information(
                "[UHD映射] 已写入 active-mod.json：role={Role} mods={Mods} textures={Textures} path={Path}",
                roleName, manifest.Mods.Count, manifest.Mods.Sum(m => m.Textures.Count), path);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(e, "[UHD映射] 写入 active-mod.json 失败");
            return null;
        }
    }

    public void Clear() => UhdActiveModStore.Clear();

    /// <summary>Rocket 学习库里的已知 UHD 配对（可没有：DLL 会自己学）。角色名对不上就返回 null。</summary>
    private IReadOnlyDictionary<string, IReadOnlyCollection<string>>? TryLoadAliases(string roleName)
    {
        var aliasesPath = Path.Combine(RoleHashAliasLibrary.DefaultDirectory,
            RoleHashAliasLibrary.AliasesFileName);

        if (!File.Exists(aliasesPath))
            return null;

        try
        {
            var library = RoleHashAliasLibrary.Load(aliasesPath);
            var mappings = RoleHashAliasLibrary.GetForRole(library, roleName);
            return mappings.Count == 0 ? null : mappings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Debug(e, "[UHD映射] 读取 Rocket 学习库失败，清单将不含预置 UHD 配对");
            return null;
        }
    }
}