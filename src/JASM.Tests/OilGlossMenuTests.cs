using System.Text;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;
using GIMI_ModManager.Core.Services.OilGloss;

namespace JASM.Tests;

/// <summary>
/// 油光三行接入 F7 菜单：固定置顶、只读共享命名空间、本体自带的强度/样式按键不进菜单。
/// </summary>
public class OilGlossMenuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jasm-oil-menu-" + Guid.NewGuid().ToString("N"));

    public OilGlossMenuTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CreatesMasterBodyAndClothRows()
    {
        var manifest = new OilGlossManifest
        {
            Namespace = "JasmOilABCD1234",
            HasBodyGroup = true,
            HasClothGroup = false,
        };

        var rows = OilGlossMenuRows.Create(manifest);

        Assert.Equal(2, rows.Count);
        Assert.Equal("油光总开关", rows[0].Label);
        Assert.Equal("身体油光", rows[1].Label);
        Assert.Equal(@"$\JasmOilABCD1234\jasm_oil_on", rows[0].QualifiedVariable);
        Assert.True(rows[0].IsClickable);
        Assert.Equal("1", rows[0].DefaultValue);
        Assert.DoesNotContain(rows, r => r.SectionName == OilGlossMenuRows.ClothSection);
    }

    [Fact]
    public void GeneratorPinsOilRowsFirstAndExcludesBodyHotkeys()
    {
        var mod = CreateMod();
        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        var apply = OilGlossPatcher.Apply(mod, body: body);
        Assert.Equal(OilGlossApplyStatus.Applied, apply.Status);

        var result = InGameMenuGenerator.Generate(mod, new InGameMenuOptions
        {
            ExtraRows = OilGlossMenuRows.Create(apply.Manifest!),
        });

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);

        var menu = File.ReadAllText(Path.Combine(mod, "JASM_Menu.ini"));
        var ns = apply.Manifest!.Namespace;

        Assert.Contains($@"$\{ns}\jasm_oil_on", menu);
        Assert.Contains($@"$\{ns}\oil_body", menu);
        Assert.Contains($@"$\{ns}\oil_cloth", menu);
        Assert.Contains("行 0: [KeyOilMaster]", menu);
        Assert.Contains("行 1: [KeyOilBody]", menu);

        // 本体 Matcap.ini 的 [KeyOilToggle] 属于 KatFXMatcap 命名空间，不进菜单
        Assert.DoesNotContain("oilSkin", menu);
        Assert.DoesNotContain("[KeyOilToggle]", menu);

        // 门控变量仍取自作者文件，不会被油光命名空间带偏
        Assert.Contains(@"$\TestMod\object_detected", menu);
    }

    [Fact]
    public void GeneratedMenuUsesChineseLabels()
    {
        var mod = CreateMod();
        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        var apply = OilGlossPatcher.Apply(mod, body: body);

        InGameMenuGenerator.Generate(mod, new InGameMenuOptions
        {
            ExtraRows = OilGlossMenuRows.Create(apply.Manifest!),
        });

        var manifest = File.ReadAllText(Path.Combine(mod, "JASM_Menu", "manifest.json"));
        Assert.Contains("油光总开关", manifest);
        Assert.Contains("身体油光", manifest);
        Assert.Contains("衣物油光", manifest);
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
        File.WriteAllText(Path.Combine(mod, "mod.ini"), """
            namespace = TestMod

            [Constants]
            global $object_detected = 0

            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9

            [TextureOverrideComponent0]
            hash = f8b55eeb
            match_first_index = 0
            $object_detected = 1
            drawindexed = 900, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 500, 900, 0

            [Present]
            if $object_detected
                post $object_detected = 0
            endif
            """, new UTF8Encoding(false));

        return mod;
    }

    private static IOilGlossAssetSource CreateAssets()
    {
        var bytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            // 模拟本体：带 namespace 与一组强度/样式热键，菜单必须把它们排除
            ["Matcap.ini"] = Encoding.UTF8.GetBytes("""
                namespace = KatFXMatcap

                [Constants]
                global persist $oilSkin = 2

                [KeyOilToggle]
                key = VK_MULTIPLY
                type = cycle
                $oilSkin = 0,1,2

                [CommandListOilSkin]
                run = CommandList\KatFXMatcap\SetParams
                """),
            ["Oil/test.dds"] = [1, 2, 3, 4],
        };

        return new DictionaryAssetSource(bytes);
    }

    private sealed class DictionaryAssetSource(IReadOnlyDictionary<string, byte[]> assets) : IOilGlossAssetSource
    {
        public IReadOnlyList<string> AssetPaths => assets.Keys.ToList();

        public Stream? OpenRead(string relativePath)
            => assets.TryGetValue(relativePath, out var bytes) ? new MemoryStream(bytes) : null;
    }
}