using System.Text;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;
using GIMI_ModManager.Core.Services.OilGloss;

namespace JASM.Tests;

/// <summary>
/// 油光补丁：包裹位置 / 幂等 / 备份与撤销 / 编码保留 / 拒绝覆盖非 JASM 文件。
/// </summary>
public class OilGlossPatcherTests : IDisposable
{
    private static readonly string[] AssetPaths = ["Matcap.ini", "Oil/test.dds"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jasm-oil-patch-" + Guid.NewGuid().ToString("N"));

    public OilGlossPatcherTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ApplyWrapsOnlyTheDrawCallAndWritesSharedIni()
    {
        var mod = CreateMod(TwoDrawCallIni);
        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        Assert.True(body.Success);

        var result = OilGlossPatcher.Apply(mod, body: body);

        Assert.Equal(OilGlossApplyStatus.Applied, result.Status);
        Assert.True(File.Exists(Path.Combine(mod, OilGlossProtocol.OilIniFileName)));
        Assert.True(File.Exists(Path.Combine(mod, "mod.ini" + OilGlossProtocol.BackupSuffix)));

        var text = File.ReadAllText(Path.Combine(mod, "mod.ini"));
        var skin = text.IndexOf(OilGlossProtocol.OilSkinCommand, StringComparison.Ordinal);
        var draw = text.IndexOf("drawindexed = 900", StringComparison.Ordinal);
        var clear = text.IndexOf(OilGlossProtocol.OilClearCommand, StringComparison.Ordinal);

        Assert.True(skin >= 0 && draw > skin && clear > draw, text);

        // 每条 drawcall 前后各一对（总开关 + 分组），两条 drawcall 共 8 行 if
        Assert.Equal(8, CountOccurrences(text, "if $\\"));
        Assert.Contains("oil_body", text);
        Assert.Contains("oil_cloth", text);
    }

    [Fact]
    public void ApplyIsIdempotent()
    {
        var mod = CreateMod(TwoDrawCallIni);
        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        OilGlossPatcher.Apply(mod, body: body);

        var second = OilGlossPatcher.Apply(mod);

        Assert.Equal(OilGlossApplyStatus.AlreadyApplied, second.Status);
        Assert.Equal(8, CountOccurrences(File.ReadAllText(Path.Combine(mod, "mod.ini")), "if $\\"));
    }

    [Fact]
    public void UndoRestoresOriginalBytesAndRemovesJasmArtifacts()
    {
        var mod = CreateMod(TwoDrawCallIni);
        var iniPath = Path.Combine(mod, "mod.ini");
        var original = File.ReadAllBytes(iniPath);

        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        OilGlossPatcher.Apply(mod, body: body);

        var undo = OilGlossPatcher.Undo(mod);

        Assert.True(undo.Success, undo.Reason);
        Assert.Equal(original, File.ReadAllBytes(iniPath));
        Assert.False(File.Exists(Path.Combine(mod, OilGlossProtocol.OilIniFileName)));
        Assert.False(File.Exists(iniPath + OilGlossProtocol.BackupSuffix));
        Assert.False(File.Exists(Path.Combine(mod, "Matcap.ini")));
        Assert.False(Directory.Exists(Path.Combine(mod, "Oil")));
        Assert.Equal(OilGlossState.NotApplied, OilGlossPatcher.GetStatus(mod).State);
    }

    [Fact]
    public void UndoRefusesWhenAuthorFileChangedAfterPatch()
    {
        var mod = CreateMod(TwoDrawCallIni);
        var iniPath = Path.Combine(mod, "mod.ini");
        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        OilGlossPatcher.Apply(mod, body: body);

        File.AppendAllText(iniPath, "\r\n; user edit\r\n");

        var undo = OilGlossPatcher.Undo(mod);

        Assert.False(undo.Success);
        Assert.Contains("mod.ini", undo.Conflicts);
        Assert.Contains("; user edit", File.ReadAllText(iniPath));
        Assert.Equal(OilGlossState.Conflict, OilGlossPatcher.GetStatus(mod).State);
    }

    [Fact]
    public void ApplyPreservesGb18030EncodingAndUndoRestoresBytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(54936);
        const string content = """
            namespace = TestMod
            ; 中文注释不能乱码

            [TextureOverrideComponent0]
            drawindexed = 900, 0, 0
            """;

        var mod = Path.Combine(_root, "GbMod");
        Directory.CreateDirectory(mod);
        var iniPath = Path.Combine(mod, "mod.ini");
        var original = gbk.GetBytes(content);
        File.WriteAllBytes(iniPath, original);

        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        var result = OilGlossPatcher.Apply(mod, body: body);

        Assert.Equal(OilGlossApplyStatus.Applied, result.Status);
        Assert.Contains("中文注释不能乱码", gbk.GetString(File.ReadAllBytes(iniPath)));
        Assert.Contains(OilGlossProtocol.OilSkinCommand, gbk.GetString(File.ReadAllBytes(iniPath)));

        Assert.True(OilGlossPatcher.Undo(mod).Success);
        Assert.Equal(original, File.ReadAllBytes(iniPath));
    }

    [Fact]
    public void DoesNotOverwriteAuthorProvidedBody()
    {
        var mod = CreateMod(TwoDrawCallIni);
        File.WriteAllText(Path.Combine(mod, "Matcap.ini"), "namespace = KatFXMatcap\r\n");

        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());

        Assert.True(body.Success);
        Assert.True(body.AlreadyPresent);
        Assert.Empty(body.WrittenFiles);
        Assert.False(File.Exists(Path.Combine(mod, "Oil", "test.dds")));
    }

    [Fact]
    public void RefusesToOverwriteNonGeneratedOilIni()
    {
        var mod = CreateMod(TwoDrawCallIni);
        File.WriteAllText(Path.Combine(mod, OilGlossProtocol.OilIniFileName), "; someone else's file\r\n");

        var result = OilGlossPatcher.Apply(mod);

        Assert.Equal(OilGlossApplyStatus.Failed, result.Status);
        Assert.Contains("拒绝覆盖", result.Reason);
    }

    [Fact]
    public void RefusesToDoubleWrapExistingExternalOilPatch()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            run = CommandList\KatFXMatcap\OilSkin
            drawindexed = 900, 0, 0
            run = CommandList\KatFXMatcap\Clear
            """);

        var result = OilGlossPatcher.Apply(mod);

        Assert.Equal(OilGlossApplyStatus.Failed, result.Status);
        Assert.Contains("已有的油光包裹", result.Reason);
    }

    [Fact]
    public void StatusIsBrokenWhenBackupDisappears()
    {
        var mod = CreateMod(TwoDrawCallIni);
        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        OilGlossPatcher.Apply(mod, body: body);

        File.Delete(Path.Combine(mod, "mod.ini" + OilGlossProtocol.BackupSuffix));

        Assert.Equal(OilGlossState.Broken, OilGlossPatcher.GetStatus(mod).State);
    }

    [Fact]
    public void UndoWorksAfterMenuGeneratorAddsAsciiNamespace()
    {
        var mod = CreateMod("""
            [TextureOverrideComponent0]
            drawindexed = 900, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 500, 900, 0
            """);
        var iniPath = Path.Combine(mod, "mod.ini");
        var original = File.ReadAllBytes(iniPath);

        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        OilGlossPatcher.Apply(mod, body: body);

        // 模拟菜单生成器在补丁之后补 namespace（真实 Mod 上发生过，导致撤销误判冲突）
        IniFileText.TryInsertNamespace(iniPath, "JasmTEST1234");
        Assert.Equal(OilGlossState.Applied, OilGlossPatcher.GetStatus(mod).State);

        var undo = OilGlossPatcher.Undo(mod);

        Assert.True(undo.Success, undo.Reason);
        Assert.Equal(original, File.ReadAllBytes(iniPath));
    }

    [Fact]
    public void UndoAcceptsManifestHashThatIncludesNamespace()
    {
        var mod = CreateMod("""
            [TextureOverrideComponent0]
            drawindexed = 900, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 500, 900, 0
            """);
        var iniPath = Path.Combine(mod, "mod.ini");

        // namespace 在补丁之前就存在 → 补丁文件的“原样字节哈希”包含它
        IniFileText.TryInsertNamespace(iniPath, "JasmBEFORE123");
        var original = File.ReadAllBytes(iniPath);

        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        OilGlossPatcher.Apply(mod, body: body);

        // 模拟旧 manifest：记录的是包含 namespace 的原样哈希
        var status = OilGlossPatcher.GetStatus(mod);
        var oldHash = status.Manifest!.PatchedFiles[0].PatchedSha256;
        var exactHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(iniPath)));
        var manifestPath = Path.Combine(mod, OilGlossProtocol.StateDirectoryName, OilGlossProtocol.ManifestFileName);
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace(oldHash, exactHash));

        Assert.Equal(OilGlossState.Applied, OilGlossPatcher.GetStatus(mod).State);
        var undo = OilGlossPatcher.Undo(mod);
        Assert.True(undo.Success, undo.Reason);
        Assert.Equal(original, File.ReadAllBytes(iniPath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private const string TwoDrawCallIni = """
        namespace = TestMod

        [TextureOverrideComponent0]
        hash = 1
        drawindexed = 900, 0, 0

        [TextureOverrideComponent1]
        drawindexed = 500, 900, 0
        """;

    private string CreateMod(string iniContent)
    {
        var mod = Path.Combine(_root, "Mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.ini"), iniContent, new UTF8Encoding(false));
        return mod;
    }

    private static IOilGlossAssetSource CreateAssets()
    {
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Matcap.ini"] = Encoding.UTF8.GetBytes("namespace = KatFXMatcap\r\n[CommandListOilSkin]\r\n"),
            ["Oil/test.dds"] = [1, 2, 3, 4],
        };

        return new DictionaryAssetSource(bytes);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private sealed class DictionaryAssetSource(IReadOnlyDictionary<string, byte[]> assets) : IOilGlossAssetSource
    {
        public IReadOnlyList<string> AssetPaths => assets.Keys.ToList();

        public Stream? OpenRead(string relativePath)
            => assets.TryGetValue(relativePath, out var bytes) ? new MemoryStream(bytes) : null;
    }
}