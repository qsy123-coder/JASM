using System.Security.Cryptography;
using System.Text;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;
using Newtonsoft.Json;

namespace GIMI_ModManager.Core.Services.OilGloss;

/// <summary>
/// 给作者 ini 加「油光门控」、维护共享变量文件与撤销备份。
///
/// 每个被选中的 drawcall 变成：
/// <code>
/// if $\JasmOilXXXX\jasm_oil_on
/// if $\JasmOilXXXX\oil_body
/// run = CommandList\KatFXMatcap\OilSkin
/// endif
/// endif
/// drawindexed = ...
/// if $\JasmOilXXXX\jasm_oil_on
/// if $\JasmOilXXXX\oil_body
/// run = CommandList\KatFXMatcap\Clear
/// endif
/// endif
/// </code>
///
/// 3DMigoto 的 <c>if</c> 不支持 <c>&amp;&amp;</c>，所以总开关与分组开关必须嵌套；
/// 且油光命令**只包两条 run，不包 drawindexed** —— 关掉油光时网格照常绘制。
/// </summary>
public static class OilGlossPatcher
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>读取当前补丁状态。判据以 manifest 为准，并校验产物是否还在。</summary>
    public static OilGlossStatus GetStatus(string modFolderPath)
    {
        var manifestPath = GetManifestPath(modFolderPath);
        var oilIniPath = Path.Combine(modFolderPath, OilGlossProtocol.OilIniFileName);

        if (!File.Exists(manifestPath))
        {
            return File.Exists(oilIniPath)
                ? new OilGlossStatus { State = OilGlossState.Broken, Reason = "存在 JASM_Oil.ini 但缺少状态清单" }
                : new OilGlossStatus { State = OilGlossState.NotApplied };
        }

        OilGlossManifest? manifest;
        try
        {
            manifest = JsonConvert.DeserializeObject<OilGlossManifest>(File.ReadAllText(manifestPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new OilGlossStatus { State = OilGlossState.Broken, Reason = "状态清单无法读取：" + e.Message };
        }

        if (manifest is null || manifest.ManifestVersion != OilGlossProtocol.CurrentVersion)
            return new OilGlossStatus { State = OilGlossState.Broken, Reason = "状态清单版本不兼容" };

        if (!File.Exists(oilIniPath))
            return new OilGlossStatus { State = OilGlossState.Broken, Manifest = manifest, Reason = "JASM_Oil.ini 已丢失" };

        foreach (var file in manifest.PatchedFiles)
        {
            var path = Path.Combine(modFolderPath, file.RelativePath);
            var backup = Path.Combine(modFolderPath, file.BackupRelativePath);
            if (!File.Exists(path) || !File.Exists(backup))
            {
                return new OilGlossStatus
                {
                    State = OilGlossState.Broken,
                    Manifest = manifest,
                    Reason = "补丁文件或备份已丢失：" + file.RelativePath,
                };
            }

            // 补丁后被外部改过：备份还在，但撤销会覆盖用户的改动 → 标记冲突
            if (!MatchesPatchedFile(path, file.PatchedSha256))
            {
                return new OilGlossStatus
                {
                    State = OilGlossState.Conflict,
                    Manifest = manifest,
                    Reason = "补丁文件在补丁后被修改：" + file.RelativePath,
                };
            }
        }

        return new OilGlossStatus { State = OilGlossState.Applied, Manifest = manifest };
    }

    /// <summary>
    /// 写入补丁。调用方应先确保本体已就位（<see cref="OilGlossBodyProvisioner"/>）。
    /// <paramref name="scan"/> 为 null 时现场扫描。
    /// </summary>
    public static OilGlossApplyResult Apply(
        string modFolderPath,
        OilGlossScanResult? scan = null,
        OilGlossBodyProvisionResult? body = null)
    {
        if (!Directory.Exists(modFolderPath))
            return Failed("Mod 目录不存在");

        if (body is { Success: false })
            return Failed("本体复制失败：" + body.Reason);

        var oilIniPath = Path.Combine(modFolderPath, OilGlossProtocol.OilIniFileName);
        if (File.Exists(oilIniPath))
        {
            var existing = IniFileText.ReadAllText(oilIniPath);
            if (!existing.Contains(OilGlossProtocol.GeneratedMarker, StringComparison.Ordinal))
                return Failed($"{OilGlossProtocol.OilIniFileName} 已存在且不是 JASM 生成的，拒绝覆盖");
        }

        var status = GetStatus(modFolderPath);
        if (status.State == OilGlossState.Applied)
        {
            return new OilGlossApplyResult
            {
                Status = OilGlossApplyStatus.AlreadyApplied,
                Reason = "油光补丁已存在",
                Manifest = status.Manifest,
            };
        }

        // 不完整 / 被外部改过的状态不能直接重打：无法确定哪些文件是我们包的，
        // 继续写只会产生一份「没有备份可还原」的假 manifest。
        if (status.State is OilGlossState.Broken or OilGlossState.Conflict)
        {
            return Failed("补丁状态不完整（" + status.Reason + "）。请先撤销；若撤销不可用，删除 Mod 根目录的 "
                          + OilGlossProtocol.OilIniFileName + "、JASM_Oil\\ 与 *.jasm_oil.bak 后再试");
        }

        scan ??= OilGlossScanner.Scan(modFolderPath);
        // 允许只包衣物（用户把身体块改成「不包」但保留衣物）：只要有一块选中就能打补丁。
        if (!scan.Drawcalls.Any(d => d.Group is not null))
        {
            return new OilGlossApplyResult
            {
                Status = OilGlossApplyStatus.NoDrawcalls,
                Reason = string.IsNullOrWhiteSpace(scan.Reason) ? "没有选择任何网格" : scan.Reason,
            };
        }

        var ns = OilGlossProtocol.CreateNamespace(modFolderPath);

        // 已经有人把 OilSkin/Clear 包进 drawcall（split_oil.py 或手动），再包一次会叠两层油光。
        // 本版不做「改写已有包裹」的接管，直接给可行动的原因。
        if (HasExternalOilPatch(modFolderPath, ns))
        {
            return Failed("检测到已有的油光包裹（split_oil.py 或手动），本版不自动接管；请先撤销原补丁");
        }
        var selected = scan.Drawcalls
            .Where(d => d.Group is not null)
            .GroupBy(d => d.IniRelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var patchedFiles = new List<OilGlossPatchedFile>();
        var createdBackups = new List<(string Original, string Backup)>();

        try
        {
            foreach (var group in selected)
            {
                var relativePath = group.Key;
                var path = Path.Combine(modFolderPath, relativePath);
                if (!File.Exists(path))
                    continue;

                var text = IniFileText.ReadAllText(path);
                // 已经打过（例如 split_oil.py 或上一次 JASM 补丁）→ 不重复嵌套
                if (text.Contains(ns, StringComparison.Ordinal))
                    continue;

                var newLine = IniFileText.UsesCrLf(text) ? "\r\n" : "\n";
                var lines = text.Split(newLine).ToList();

                var fresh = OilGlossScanner.ScanFile(path, relativePath);
                var targets = ResolveTargets(group.ToList(), fresh);
                if (targets.Count == 0)
                    continue;

                foreach (var target in targets.OrderByDescending(t => t.LineIndex))
                {
                    InsertGates(lines, target, ns, newLine);
                }

                var backupPath = path + OilGlossProtocol.BackupSuffix;
                if (!File.Exists(backupPath))
                {
                    File.Copy(path, backupPath);
                    createdBackups.Add((path, backupPath));
                }

                IniFileText.WriteAllTextPreservingEncoding(path, string.Join(newLine, lines));

                patchedFiles.Add(new OilGlossPatchedFile
                {
                    RelativePath = relativePath,
                    BackupRelativePath = Path.GetRelativePath(modFolderPath, backupPath),
                    PatchedSha256 = IniFileText.ComputeContentHash(path),
                });
            }

            File.WriteAllText(oilIniPath, OilGlossProtocol.BuildOilIniText(ns), Utf8NoBom);

            var manifest = new OilGlossManifest
            {
                ManifestVersion = OilGlossProtocol.CurrentVersion,
                GeneratorVersion = OilGlossProtocol.CurrentVersion,
                BodyVersion = OilGlossProtocol.BodyVersion,
                PatchVersion = OilGlossProtocol.PatchVersion,
                Namespace = ns,
                BodyProvidedByJasm = body?.BodyProvidedByJasm ?? false,
                BodyFiles = body?.WrittenFiles.ToList() ?? [],
                PatchedFiles = patchedFiles,
                // 记下每块网格的分组：「试亮」取消时要按它恢复，而不是退回自动分组。
                AppliedDrawcalls = scan.Drawcalls
                    .Where(d => d.Group is not null)
                    .Select(d => new OilGlossAppliedDrawcall
                    {
                        IniRelativePath = d.IniRelativePath,
                        SectionName = d.SectionName,
                        IndexCount = d.IndexCount,
                        StartIndex = d.StartIndex,
                        Group = d.Group!.Value,
                    })
                    .ToList(),
                HasBodyGroup = scan.Drawcalls.Any(d => d.Group is not null),
                HasClothGroup = false,
                AppliedAtUtc = DateTime.UtcNow,
            };

            WriteManifest(modFolderPath, manifest);

            return new OilGlossApplyResult
            {
                Status = OilGlossApplyStatus.Applied,
                Manifest = manifest,
                BodyCount = scan.Drawcalls.Count(d => d.Group is not null),
                ClothCount = 0,
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 中途失败时把这次新建的备份还原回去，避免留下「改了一半、没有清单」的状态
            foreach (var (original, backup) in createdBackups)
            {
                try
                {
                    File.Copy(backup, original, overwrite: true);
                    File.Delete(backup);
                }
                catch (IOException)
                {
                }
            }

            try
            {
                if (File.Exists(oilIniPath))
                    File.Delete(oilIniPath);
            }
            catch (IOException)
            {
            }

            return Failed(e.Message);
        }
    }

    /// <summary>
    /// 撤销补丁。任一文件在补丁后被外部修改过就整次中止，绝不覆盖用户改动。
    /// </summary>
    public static OilGlossUndoResult Undo(string modFolderPath)
    {
        var status = GetStatus(modFolderPath);
        if (status.Manifest is null)
        {
            return new OilGlossUndoResult { Success = false, Reason = status.State == OilGlossState.NotApplied ? "没有油光补丁" : status.Reason };
        }

        var manifest = status.Manifest;
        var conflicts = new List<string>();

        foreach (var file in manifest.PatchedFiles)
        {
            var path = Path.Combine(modFolderPath, file.RelativePath);
            var backup = Path.Combine(modFolderPath, file.BackupRelativePath);

            if (!File.Exists(path) || !File.Exists(backup)
                || !MatchesPatchedFile(path, file.PatchedSha256))
            {
                conflicts.Add(file.RelativePath);
            }
        }

        if (conflicts.Count > 0)
        {
            return new OilGlossUndoResult
            {
                Success = false,
                Conflicts = conflicts,
                Reason = "以下文件在补丁后被修改过，已中止撤销",
            };
        }

        var restored = new List<string>();
        try
        {
            foreach (var file in manifest.PatchedFiles)
            {
                var path = Path.Combine(modFolderPath, file.RelativePath);
                var backup = Path.Combine(modFolderPath, file.BackupRelativePath);
                File.Copy(backup, path, overwrite: true);
                File.Delete(backup);
                restored.Add(file.RelativePath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new OilGlossUndoResult { Success = false, RestoredFiles = restored, Reason = e.Message };
        }

        // 到这里补丁已经失效（作者 ini 已还原）。本体文件（Matcap.ini / Oil/）
        // 在游戏运行时会被 3DMigoto 占用，删不掉不应让整次撤销失败——
        // 残留的本体文件不影响外观，下次重打/撤销会再清理。
        try
        {
            DeleteProvisionedBodyFiles(modFolderPath, manifest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 忽略：游戏占用
        }

        try
        {
            var oilIniPath = Path.Combine(modFolderPath, OilGlossProtocol.OilIniFileName);
            if (File.Exists(oilIniPath))
                File.Delete(oilIniPath);

            var stateDirectory = Path.Combine(modFolderPath, OilGlossProtocol.StateDirectoryName);
            if (Directory.Exists(stateDirectory))
                Directory.Delete(stateDirectory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new OilGlossUndoResult { Success = false, RestoredFiles = restored, Reason = e.Message };
        }

        return new OilGlossUndoResult { Success = true, RestoredFiles = restored };
    }

    /// <summary>
    /// 把计划里的目标与现场重新扫描的结果对上：同样段落、同样索引数、同样起始索引。
    /// 防止扫描后文件被改，行号偏移导致包错 drawcall。
    /// </summary>
    private static List<OilGlossDrawcall> ResolveTargets(
        IReadOnlyList<OilGlossDrawcall> planned,
        IReadOnlyList<OilGlossDrawcall> fresh)
    {
        var result = new List<OilGlossDrawcall>();
        var used = new HashSet<int>();

        foreach (var target in planned)
        {
            var match = fresh.FirstOrDefault(d =>
                !used.Contains(d.LineIndex)
                && d.SectionName == target.SectionName
                && d.IndexCount == target.IndexCount
                && d.StartIndex == target.StartIndex);

            if (match is null)
                continue;

            used.Add(match.LineIndex);
            result.Add(match with { Group = target.Group });
        }

        return result;
    }

    private static void InsertGates(List<string> lines, OilGlossDrawcall target, string ns, string newLine)
    {
        var indent = new string(lines[target.LineIndex].TakeWhile(char.IsWhiteSpace).ToArray());

        // 只有一个总开关：官方做法按 drawcall 包，一个 drawcall 常同时含身体和衣物，
        // 再分身体/衣物两个变量只会做出「关了衣物身体还亮」的假开关。
        var master = "if $\\" + ns + "\\" + OilGlossProtocol.MasterVariable;

        var before = new[]
        {
            indent + master,
            indent + OilGlossProtocol.OilSkinCommand,
            indent + "endif",
        };

        var after = new[]
        {
            indent + master,
            indent + OilGlossProtocol.OilClearCommand,
            indent + "endif",
        };

        // 先插前面的，再按新的行号插后面的（drawindexed 被整体后移 before.Length 行）
        lines.InsertRange(target.LineIndex, before);
        lines.InsertRange(target.LineIndex + before.Length + 1, after);
    }

    /// <summary>
    /// 是否已有别人做的油光包裹：文件里出现 OilSkin 命令、但不含我们的命名空间。
    /// 我们自己生成的 JASM_Oil.ini 只有变量，不包含命令，所以不会误判。
    /// </summary>
    private static bool HasExternalOilPatch(string modFolderPath, string ourNamespace)
    {
        foreach (var file in Directory.EnumerateFiles(modFolderPath, "*.ini", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, OilGlossProtocol.OilIniFileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "JASM_Menu.ini", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var text = IniFileText.ReadAllText(file);
                if (text.Contains(OilGlossProtocol.OilSkinCommand, StringComparison.OrdinalIgnoreCase)
                    && !text.Contains(ourNamespace, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return false;
    }

    private static void DeleteProvisionedBodyFiles(string modFolderPath, OilGlossManifest manifest)
        => OilGlossBodyProvisioner.RemoveWrittenFiles(modFolderPath, manifest.BodyFiles);

    /// <summary>写回状态清单（本体升级后需要更新 <c>BodyFiles</c> / <c>BodyVersion</c>）。</summary>
    public static void WriteManifest(string modFolderPath, OilGlossManifest manifest)
    {
        var directory = Path.Combine(modFolderPath, OilGlossProtocol.StateDirectoryName);
        Directory.CreateDirectory(directory);
        var json = JsonConvert.SerializeObject(manifest, Formatting.Indented);
        File.WriteAllText(Path.Combine(directory, OilGlossProtocol.ManifestFileName), json, Utf8NoBom);
    }

    /// <summary>
    /// 补丁文件是否仍是当初写入的内容。
    /// 兼容两种记录方式：旧 manifest 的哈希可能包含、也可能不包含随后补上的 JASM namespace；
    /// 新 manifest 记录的是忽略 namespace 的哈希。原样哈希与剥离 namespace 后的哈希任一匹配即算未改。
    /// </summary>
    private static bool MatchesPatchedFile(string path, string expected)
        => string.Equals(ComputeSha256(path), expected, StringComparison.OrdinalIgnoreCase)
           || string.Equals(IniFileText.ComputeContentHash(path), expected, StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string GetManifestPath(string modFolderPath)
        => Path.Combine(modFolderPath, OilGlossProtocol.StateDirectoryName, OilGlossProtocol.ManifestFileName);


    private static OilGlossApplyResult Failed(string reason)
        => new() { Status = OilGlossApplyStatus.Failed, Reason = reason };
}