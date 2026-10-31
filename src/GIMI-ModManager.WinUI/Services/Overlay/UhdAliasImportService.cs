using System.Text;
using GIMI_ModManager.Core.Services.WuwaUhd;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>导入 Rocket RoleHash 学习结果的结果。</summary>
public sealed record UhdAliasImportResult(
    bool Success,
    string Message,
    int FilesChanged,
    int SectionsAdded,
    int ModsSkipped,
    IReadOnlyList<string> ChangedFiles)
{
    public static UhdAliasImportResult Fail(string message) => new(false, message, 0, 0, 0, []);
}

/// <summary>
/// 把 Rocket 新版 <c>Lod0_texture.dll</c> 学到的 UHD→HD 映射，离线补进 mod 的 ini。
///
/// **为什么不注入 Rocket 的 DLL**：那套 RoleHash 是运行时“钉住”某个 mod 贴图的，和 JASM 的
/// DISABLED_ 切换会互相打架。这里只消费它写下的学习结果，生成静态的
/// <c>[TextureOverrideTexture*_UHD_*]</c> 段；日常启动不加载 Rocket 组件。
///
/// 学习数据有两个来源，读取时合并：Rocket 的
/// <c>%LOCALAPPDATA%\Rocket\Wuwa\AutoLearning\RoleHash.aliases.tsv</c>
/// 与 JASM 自研学习结果 <see cref="UhdLearningStore.LibraryPath"/>。
/// 后者不依赖 Rocket（HD/UHD 两次观察配对，见 <see cref="UhdLearningStore"/>）。
/// </summary>
public sealed class UhdAliasImportService
{
    private readonly ILogger _logger;

    public UhdAliasImportService(ILogger logger)
    {
        _logger = logger.ForContext<UhdAliasImportService>();
    }

    public static string AliasesFilePath =>
        Path.Combine(RoleHashAliasLibrary.DefaultDirectory, RoleHashAliasLibrary.AliasesFileName);

    /// <summary>是否有可用学习数据（Rocket aliases 或 JASM 自学习结果）、该角色有多少条映射。</summary>
    public (bool LibraryExists, int RoleMappings) Inspect(string roleName)
    {
        var exists = File.Exists(AliasesFilePath) || File.Exists(UhdLearningStore.LibraryPath);
        if (!exists)
            return (false, 0);

        try
        {
            var mappings = UhdLearningStore.LoadMappingsForRole(roleName);
            return (true, mappings.Count);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(e, "[UHD映射] 读取 UHD 学习数据失败");
            return (false, 0);
        }
    }

    /// <summary>
    /// 对 <paramref name="modPaths"/> 里的每个 ini 生成 UHD 覆盖段。只改有命中的文件；
    /// 每个被改文件先备份为 <c>&lt;ini&gt;.BEFORE_&lt;时间戳&gt;_uhd.bak</c>。
    /// </summary>
    public async Task<UhdAliasImportResult> ApplyAsync(
        string roleName,
        IReadOnlyList<string> modPaths,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(roleName))
            return UhdAliasImportResult.Fail("没有选中角色。");

        if (modPaths.Count == 0)
            return UhdAliasImportResult.Fail("至少选择一个 Mod。");

        IReadOnlyDictionary<string, IReadOnlyCollection<string>> mappings;
        try
        {
            // Rocket aliases 与 JASM 自学习结果合并；两者都缺时为空。
            mappings = UhdLearningStore.LoadMappingsForRole(roleName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(e, "[UHD映射] 读取 UHD 学习数据失败");
            return UhdAliasImportResult.Fail("读取 UHD 学习数据失败：" + e.Message);
        }

        if (mappings.Count == 0)
        {
            return UhdAliasImportResult.Fail(
                $"没有「{roleName}」的 UHD 学习数据。请先用新版 Rocket 学一次该角色，或使用 JASM 自带学习。");
        }

        var changedFiles = new List<string>();
        var sectionsAdded = 0;
        var modsSkipped = 0;

        foreach (var modPath in modPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(modPath))
            {
                modsSkipped++;
                continue;
            }

            var modChanged = false;
            foreach (var iniPath in EnumerateIniFiles(modPath))
            {
                string original;
                try
                {
                    original = await File.ReadAllTextAsync(iniPath, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _logger.Warning(e, "[UHD映射] 读取 {Path} 失败", iniPath);
                    continue;
                }

                var generated = UhdOverrideGenerator.Generate(original, mappings);
                if (!generated.HasChanges)
                    continue;

                try
                {
                    Backup(iniPath, original);
                    WriteAtomic(iniPath, generated.Text);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _logger.Error(e, "[UHD映射] 写入 {Path} 失败", iniPath);
                    modsSkipped++;
                    continue;
                }

                changedFiles.Add(iniPath);
                sectionsAdded += generated.AddedSections.Count;
                modChanged = true;
                _logger.Information("[UHD映射] {Path} 新增 {Count} 个 UHD 段", iniPath,
                    generated.AddedSections.Count);
            }

            if (!modChanged)
                modsSkipped++;
        }

        if (changedFiles.Count == 0)
        {
            return new UhdAliasImportResult(false,
                $"学习库有 {mappings.Count} 条「{roleName}」映射，但没有匹配到所选 Mod 的 HD hash（可能这个 mod 没在 Rocket 学习时启用）。",
                0, 0, modsSkipped, []);
        }

        return new UhdAliasImportResult(true,
            $"已为 {changedFiles.Count} 个 ini 新增 {sectionsAdded} 个 UHD 覆盖段。请按 F10 重载。",
            changedFiles.Count, sectionsAdded, modsSkipped, changedFiles);
    }

    private static IEnumerable<string> EnumerateIniFiles(string modPath)
    {
        try
        {
            return Directory.EnumerateFiles(modPath, "*.ini", SearchOption.TopDirectoryOnly)
                .Where(p => !p.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Backup(string iniPath, string content)
    {
        var backup = $"{iniPath}.BEFORE_{DateTime.Now:yyyyMMdd_HHmmss}_uhd.bak";
        File.WriteAllText(backup, content, new UTF8Encoding(false));
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".jasm-uhd.tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}