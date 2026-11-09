using System.Security.Cryptography;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace GIMI_ModManager.Core.Services.OilGloss;

/// <summary>
/// 把随包分发的 KatFXMatcap 本体（<c>Matcap.ini</c> + <c>Oil/*.dds</c>）复制进 Mod 根目录。
///
/// 「识别并接管」：Mod 里已经有本体的（作者预装，或用户以前手动放过）不复制、不覆盖；
/// 只有完全找不到本体时才落盘，并把写过的文件与哈希记进 manifest，撤销时只删自己写的。
/// </summary>
public static class OilGlossBodyProvisioner
{
    /// <summary>
    /// JASM 历史版本写入的 <c>Matcap.ini</c> 哈希（有 <c>ps-t0 = ResourceEmpty_Normal</c> 的旧本体）。
    /// 旧 manifest 可能因为早期 <c>BodyProvidedByJasm</c> 判断错误而丢失所有权；
    /// 哈希能证明这个文件确实是 JASM 写的，可以安全升级。
    /// </summary>
    private static readonly HashSet<string> OutdatedJasmBodyHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        // BodyVersion 1：加了 [ResourceEmpty_Normal]，但仍替换 ps-t0
        "C2B053270286C00534FFF6B2B6025A749ADE0846705A215ABD86F47FD56C4738",
        // BodyVersion 2：已移除 ps-t0，但强度物理键上限仍是 1.0
        "8BC7CFD52F2C386A9A76BD073C20F4E73760D90F0CE150ECF0A476186DE9A230",
        // BodyVersion 3：强度物理键上限 2.0，但 * / - 仍可把油光关成 0
        "B6154801759542C0696D174D07896A0F32AA2A97892EB75B10384EB7134DD794",
    };

    /// <summary>Mod 里是否已有 KatFXMatcap 本体。</summary>
    public static bool HasBody(string modFolderPath)
    {
        if (!Directory.Exists(modFolderPath))
            return false;

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
                if (text.Contains("namespace = " + OilGlossProtocol.BodyNamespace, StringComparison.OrdinalIgnoreCase)
                    || text.Contains(@"CommandList\" + OilGlossProtocol.BodyNamespace + @"\OilSkin",
                        StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// 确保本体就位。已有 → 什么都不写；没有 → 按 <paramref name="assets"/> 复制到 Mod 根目录。
    /// </summary>
    public static OilGlossBodyProvisionResult Ensure(string modFolderPath, IOilGlossAssetSource assets)
    {
        if (!Directory.Exists(modFolderPath))
            return Fail("Mod 目录不存在");

        if (HasBody(modFolderPath))
        {
            // 现有本体是不是 JASM 当前版本？决定新 manifest 的 BodyProvidedByJasm，
            // 从而决定以后要不要做版本升级。
            return new OilGlossBodyProvisionResult
            {
                Success = true,
                AlreadyPresent = true,
                BodyProvidedByJasm = MatchesEmbeddedBody(modFolderPath, assets),
            };
        }

        if (assets.AssetPaths.Count == 0)
            return Fail("内置本体资源为空");

        var written = new List<OilGlossAssetRecord>();

        try
        {
            foreach (var relativePath in assets.AssetPaths)
            {
                var normalized = Normalize(relativePath);
                var target = Path.Combine(modFolderPath, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target))
                    continue;

                if (!TryWriteAsset(assets, normalized, target, out var record))
                    return Fail("缺少内置资源：" + normalized);

                written.Add(record);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail(e.Message);
        }

        return new OilGlossBodyProvisionResult
        {
            Success = true,
            AlreadyPresent = false,
            BodyProvidedByJasm = true,
            WrittenFiles = written,
        };
    }

    /// <summary>
    /// 把 JASM 写过的旧版本体升级到 <see cref="OilGlossProtocol.BodyVersion"/>。
    ///
    /// 只覆盖「当前哈希与 manifest 记录一致」的文件（即 JASM 写的、用户没改过）；
    /// 用户改过的文件保留，作者自带的本体（<c>BodyProvidedByJasm = false</c>）整体跳过。
    /// 游戏占用文件时返回失败，调用方可下次重试。
    /// </summary>
    public static OilGlossBodyUpgradeResult Upgrade(
        string modFolderPath,
        IOilGlossAssetSource assets,
        OilGlossManifest manifest)
    {
        if (!Directory.Exists(modFolderPath))
            return FailUpgrade("Mod 目录不存在");

        manifest.BodyFiles ??= [];

        var knownOutdated = IsOutdatedKnownJasmBody(modFolderPath);

        if (!manifest.BodyProvidedByJasm && !knownOutdated)
            return new OilGlossBodyUpgradeResult
            {
                Success = true,
                Skipped = true,
                Reason = "本体不是 JASM 写入的，跳过升级",
            };

        // 早期升级/清单可能只记录了 Matcap.ini，其余本体文件没进 BodyFiles，
        // 撤销时会留下孤儿 DDS；这种情况下也要走一遍补全。
        var needsBackfill = manifest.BodyProvidedByJasm
                            && manifest.BodyFiles.Count < assets.AssetPaths.Count;

        // 已知旧 JASM 本体不看 manifest 版本：早期清单可能把 BodyVersion 写成了当前值。
        if (manifest.BodyVersion >= OilGlossProtocol.BodyVersion && !knownOutdated && !needsBackfill)
            return new OilGlossBodyUpgradeResult { Success = true, Skipped = true, Reason = "本体已是最新" };

        var recorded = manifest.BodyFiles
            .GroupBy(file => Normalize(file.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        // 旧清单丢了 BodyFiles：用当前文件的哈希作为记录，让下面的哈希校验通过并覆盖它。
        if (knownOutdated && !recorded.ContainsKey(OilGlossProtocol.BodyIniFileName))
        {
            var bodyPath = Path.Combine(modFolderPath, OilGlossProtocol.BodyIniFileName);
            recorded[OilGlossProtocol.BodyIniFileName] = new OilGlossAssetRecord
            {
                RelativePath = OilGlossProtocol.BodyIniFileName,
                Sha256 = HashFile(bodyPath),
            };
        }

        var files = new List<OilGlossAssetRecord>();
        var written = new List<OilGlossAssetRecord>();
        var preserved = new List<string>();

        try
        {
            foreach (var relativePath in assets.AssetPaths)
            {
                var normalized = Normalize(relativePath);
                var target = Path.Combine(modFolderPath, normalized.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(target))
                {
                    if (!TryWriteAsset(assets, normalized, target, out var created))
                        return FailUpgrade("缺少内置资源：" + normalized);

                    written.Add(created);
                    files.Add(created);
                    continue;
                }

                // 没有 manifest 记录：内容与内嵌资源一致才视为 JASM 写的，纳入清单
                // 以便撤销时能删掉；不一致（作者自己的文件）保持不动。
                if (!recorded.TryGetValue(normalized, out var previous))
                {
                    using var stream = assets.OpenRead(normalized);
                    if (stream is not null)
                    {
                        using var buffer = new MemoryStream();
                        stream.CopyTo(buffer);
                        var assetHash = Convert.ToHexString(SHA256.HashData(buffer.ToArray()));
                        if (string.Equals(HashFile(target), assetHash, StringComparison.OrdinalIgnoreCase))
                        {
                            files.Add(new OilGlossAssetRecord
                            {
                                RelativePath = normalized,
                                Sha256 = assetHash,
                            });
                        }
                    }

                    continue;
                }

                if (!string.Equals(HashFile(target), previous.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // 用户改过：保留原记录（撤销时同样会跳过它）
                    preserved.Add(normalized);
                    files.Add(previous);
                    continue;
                }

                if (!TryWriteAsset(assets, normalized, target, out var updated))
                    return FailUpgrade("缺少内置资源：" + normalized);

                if (!string.Equals(updated.Sha256, previous.Sha256, StringComparison.OrdinalIgnoreCase))
                    written.Add(updated);
                files.Add(updated);
            }

            // 保留清单里但新资源中没有的旧记录，撤销逻辑仍按原哈希处理
            foreach (var previous in manifest.BodyFiles)
            {
                var normalized = Normalize(previous.RelativePath);
                if (!files.Any(file => string.Equals(Normalize(file.RelativePath), normalized,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    files.Add(previous);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return FailUpgrade(e.Message);
        }

        return new OilGlossBodyUpgradeResult
        {
            Success = true,
            Files = files,
            WrittenFiles = written,
            PreservedFiles = preserved,
        };
    }

    /// <summary>
    /// 删除 JASM 写过的本体文件；内容被用户改过的保留，避免误删。
    /// 撤销与「开启失败回滚」共用。
    /// </summary>
    public static void RemoveWrittenFiles(string modFolderPath, IReadOnlyList<OilGlossAssetRecord> files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(modFolderPath, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                continue;

            if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Delete(path);
        }

        // Oil 目录空了就一起删掉，避免留下空壳
        var oilDirectory = Path.Combine(modFolderPath, "Oil");
        if (Directory.Exists(oilDirectory) && !Directory.EnumerateFileSystemEntries(oilDirectory).Any())
            Directory.Delete(oilDirectory);
    }

    /// <summary>把资源写成目标文件；内容相同则不写盘（游戏占用时少一次失败）。</summary>
    private static bool TryWriteAsset(
        IOilGlossAssetSource assets,
        string relativePath,
        string target,
        out OilGlossAssetRecord record)
    {
        record = new OilGlossAssetRecord();

        using var stream = assets.OpenRead(relativePath);
        if (stream is null)
            return false;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        record = new OilGlossAssetRecord { RelativePath = relativePath, Sha256 = hash };

        if (File.Exists(target)
            && string.Equals(HashFile(target), hash, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        WriteAtomically(target, bytes);
        return true;
    }

    private static void WriteAtomically(string target, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = target + ".jasm.tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>现有 <c>Matcap.ini</c> 是否是 JASM 历史版本写入的旧本体。</summary>
    public static bool IsOutdatedKnownJasmBody(string modFolderPath)
    {
        var path = Path.Combine(modFolderPath, OilGlossProtocol.BodyIniFileName);
        if (!File.Exists(path))
            return false;

        try
        {
            return OutdatedJasmBodyHashes.Contains(HashFile(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>现有 <c>Matcap.ini</c> 是否与内嵌本体逐字节一致（即由 JASM 写入）。</summary>
    private static bool MatchesEmbeddedBody(string modFolderPath, IOilGlossAssetSource assets)
    {
        var target = Path.Combine(modFolderPath, OilGlossProtocol.BodyIniFileName);
        if (!File.Exists(target))
            return false;

        try
        {
            using var stream = assets.OpenRead(OilGlossProtocol.BodyIniFileName);
            if (stream is null)
                return false;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray().AsSpan().SequenceEqual(File.ReadAllBytes(target));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string HashFile(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static OilGlossBodyProvisionResult Fail(string reason)
        => new() { Success = false, Reason = reason };

    private static OilGlossBodyUpgradeResult FailUpgrade(string reason)
        => new() { Success = false, Reason = reason };

}