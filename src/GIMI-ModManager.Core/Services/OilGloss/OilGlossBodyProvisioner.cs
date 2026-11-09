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
            return new OilGlossBodyProvisionResult { Success = true, AlreadyPresent = true };

        if (assets.AssetPaths.Count == 0)
            return Fail("内置本体资源为空");

        var written = new List<OilGlossAssetRecord>();

        try
        {
            foreach (var relativePath in assets.AssetPaths)
            {
                var target = Path.Combine(modFolderPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target))
                    continue;

                using var stream = assets.OpenRead(relativePath);
                if (stream is null)
                    return Fail("缺少内置资源：" + relativePath);

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + ".jasm.tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, target, overwrite: true);

                written.Add(new OilGlossAssetRecord
                {
                    RelativePath = relativePath,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                });
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
            WrittenFiles = written,
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

    private static OilGlossBodyProvisionResult Fail(string reason)
        => new() { Success = false, Reason = reason };
}