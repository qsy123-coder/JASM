using System.Security.Cryptography;
using System.Text;
using GIMI_ModManager.Core.Services.OilGloss;

namespace JASM.Tests;

/// <summary>
/// 旧版本体（BodyVersion &lt; 当前）的升级：只换 JASM 写过的文件，保留用户改动。
/// </summary>
public class OilGlossBodyUpgradeTests : IDisposable
{
    private const string OldMatcap = "namespace = KatFXMatcap\r\n; old body with ps-t0\r\n";
    private const string NewMatcap = "namespace = KatFXMatcap\r\n; fixed body without ps-t0\r\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jasm-oil-upgrade-" + Guid.NewGuid().ToString("N"));

    public OilGlossBodyUpgradeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UpgradesJasmProvidedOldBody()
    {
        var mod = CreateMod();
        File.WriteAllText(Path.Combine(mod, "Matcap.ini"), OldMatcap, new UTF8Encoding(false));

        var manifest = Manifest(bodyVersion: 1, providedByJasm: true,
            new OilGlossAssetRecord { RelativePath = "Matcap.ini", Sha256 = Hash(OldMatcap) });

        var result = OilGlossBodyProvisioner.Upgrade(mod, CreateAssets(), manifest);

        Assert.True(result.Success, result.Reason);
        Assert.False(result.Skipped);
        Assert.Equal(NewMatcap, File.ReadAllText(Path.Combine(mod, "Matcap.ini")));
        Assert.Contains(result.WrittenFiles, file => file.RelativePath == "Matcap.ini");
        Assert.Contains(result.Files, file => file.RelativePath == "Matcap.ini" && file.Sha256 == Hash(NewMatcap));
    }

    [Fact]
    public void PreservesUserModifiedBody()
    {
        var mod = CreateMod();
        const string userBody = "namespace = KatFXMatcap\r\n; user edited\r\n";
        File.WriteAllText(Path.Combine(mod, "Matcap.ini"), userBody, new UTF8Encoding(false));

        var manifest = Manifest(bodyVersion: 1, providedByJasm: true,
            new OilGlossAssetRecord { RelativePath = "Matcap.ini", Sha256 = Hash(OldMatcap) });

        var result = OilGlossBodyProvisioner.Upgrade(mod, CreateAssets(), manifest);

        Assert.True(result.Success, result.Reason);
        Assert.Equal(userBody, File.ReadAllText(Path.Combine(mod, "Matcap.ini")));
        Assert.Contains("Matcap.ini", result.PreservedFiles);
        Assert.Contains(result.Files, file => file.RelativePath == "Matcap.ini" && file.Sha256 == Hash(OldMatcap));
    }

    [Fact]
    public void SkipsAuthorProvidedBody()
    {
        var mod = CreateMod();
        File.WriteAllText(Path.Combine(mod, "Matcap.ini"), OldMatcap, new UTF8Encoding(false));

        var manifest = Manifest(bodyVersion: 1, providedByJasm: false,
            new OilGlossAssetRecord { RelativePath = "Matcap.ini", Sha256 = Hash(OldMatcap) });

        var result = OilGlossBodyProvisioner.Upgrade(mod, CreateAssets(), manifest);

        Assert.True(result.Success);
        Assert.True(result.Skipped);
        Assert.Equal(OldMatcap, File.ReadAllText(Path.Combine(mod, "Matcap.ini")));
    }

    [Fact]
    public void BackfillsMissingBodyFileRecords()
    {
        var mod = CreateMod();
        File.WriteAllText(Path.Combine(mod, "Matcap.ini"), NewMatcap, new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(mod, "Oil"));
        File.WriteAllBytes(Path.Combine(mod, "Oil", "new.dds"), [1, 2, 3, 4]);

        // 当前版本，但清单只记了 Matcap.ini：其余文件撤销时会变成孤儿
        var manifest = Manifest(OilGlossProtocol.BodyVersion, providedByJasm: true,
            new OilGlossAssetRecord { RelativePath = "Matcap.ini", Sha256 = Hash(NewMatcap) });

        var result = OilGlossBodyProvisioner.Upgrade(mod, CreateAssets(), manifest);

        Assert.True(result.Success, result.Reason);
        Assert.False(result.Skipped);
        Assert.Contains(result.Files, file => file.RelativePath == "Oil/new.dds");
        Assert.Empty(result.WrittenFiles);
    }

    [Fact]
    public void SkipsAlreadyCurrentBody()
    {
        var mod = CreateMod();
        File.WriteAllText(Path.Combine(mod, "Matcap.ini"), OldMatcap, new UTF8Encoding(false));
        Directory.CreateDirectory(Path.Combine(mod, "Oil"));
        File.WriteAllBytes(Path.Combine(mod, "Oil", "new.dds"), [1, 2, 3, 4]);

        var manifest = Manifest(OilGlossProtocol.BodyVersion, providedByJasm: true,
            new OilGlossAssetRecord { RelativePath = "Matcap.ini", Sha256 = Hash(OldMatcap) },
            new OilGlossAssetRecord { RelativePath = "Oil/new.dds", Sha256 = HashBytes([1, 2, 3, 4]) });

        var result = OilGlossBodyProvisioner.Upgrade(mod, CreateAssets(), manifest);

        Assert.True(result.Skipped);
        Assert.Equal(OldMatcap, File.ReadAllText(Path.Combine(mod, "Matcap.ini")));
    }

    [Fact]
    public void AddsMissingAssetAndRecordsIt()
    {
        var mod = CreateMod();
        File.WriteAllText(Path.Combine(mod, "Matcap.ini"), OldMatcap, new UTF8Encoding(false));

        var manifest = Manifest(bodyVersion: 1, providedByJasm: true,
            new OilGlossAssetRecord { RelativePath = "Matcap.ini", Sha256 = Hash(OldMatcap) });

        var result = OilGlossBodyProvisioner.Upgrade(mod, CreateAssets(), manifest);

        Assert.True(result.Success, result.Reason);
        Assert.True(File.Exists(Path.Combine(mod, "Oil", "new.dds")));
        Assert.Contains(result.Files, file => file.RelativePath == "Oil/new.dds");
    }

    [Fact]
    public void EnsureRecognizesExistingEmbeddedBodyAsJasmProvided()
    {
        var mod = CreateMod();
        File.WriteAllText(Path.Combine(mod, "Matcap.ini"), NewMatcap, new UTF8Encoding(false));

        var provision = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());

        Assert.True(provision.Success);
        Assert.True(provision.AlreadyPresent);
        Assert.True(provision.BodyProvidedByJasm);
    }

    [Fact]
    public void EnsureTreatsForeignBodyAsNotJasmProvided()
    {
        var mod = CreateMod();
        File.WriteAllText(Path.Combine(mod, "Matcap.ini"), "namespace = KatFXMatcap\r\n; author\r\n",
            new UTF8Encoding(false));

        var provision = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());

        Assert.True(provision.Success);
        Assert.True(provision.AlreadyPresent);
        Assert.False(provision.BodyProvidedByJasm);
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

    private string CreateMod()
    {
        var mod = Path.Combine(_root, "Mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mod);

        // HasBody() 通过 namespace = KatFXMatcap 判断本体已存在
        File.WriteAllText(Path.Combine(mod, "mod.ini"), "namespace = TestMod\n[TextureOverrideComponent3]\ndrawindexed = 900, 0, 0\n",
            new UTF8Encoding(false));
        return mod;
    }

    private static OilGlossManifest Manifest(int bodyVersion, bool providedByJasm, params OilGlossAssetRecord[] files)
        => new()
        {
            ManifestVersion = OilGlossProtocol.CurrentVersion,
            GeneratorVersion = OilGlossProtocol.CurrentVersion,
            BodyVersion = bodyVersion,
            BodyProvidedByJasm = providedByJasm,
            BodyFiles = files.ToList(),
        };

    private static IOilGlossAssetSource CreateAssets()
        => new DictionaryAssetSource(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Matcap.ini"] = Encoding.UTF8.GetBytes(NewMatcap),
            ["Oil/new.dds"] = [1, 2, 3, 4],
        });

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class DictionaryAssetSource(IReadOnlyDictionary<string, byte[]> assets) : IOilGlossAssetSource
    {
        public IReadOnlyList<string> AssetPaths => assets.Keys.ToList();

        public Stream? OpenRead(string relativePath)
            => assets.TryGetValue(relativePath, out var bytes) ? new MemoryStream(bytes) : null;
    }
}
