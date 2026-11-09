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
    public void CreatesMasterStrengthAndTextureRows()
    {
        var manifest = new OilGlossManifest
        {
            Namespace = "JasmOilABCD1234",
            HasBodyGroup = true,
            HasClothGroup = false,
        };

        var rows = OilGlossMenuRows.Create(manifest);

        Assert.Equal(3, rows.Count);
        Assert.Equal("油光", rows[0].Label);
        Assert.Equal("油光强度", rows[1].Label);
        Assert.Equal("油光贴图", rows[2].Label);
        Assert.Equal(@"$\JasmOilABCD1234\jasm_oil_on", rows[0].QualifiedVariable);
        Assert.True(rows[0].IsClickable);
        Assert.Equal("1", rows[0].DefaultValue);
        Assert.True(rows[1].IsSlider);
        Assert.Equal(@"$\KatFXMatcap\oilstrength", rows[1].QualifiedVariable);
        Assert.Equal(OilGlossProtocol.MaxStrength, rows[1].SliderMax);
        Assert.Equal(@"$\KatFXMatcap\oilSkin", rows[2].QualifiedVariable);
        Assert.Equal(["2", "3", "4", "5", "6", "1"], rows[2].Values);
        Assert.Equal(["MC6", "MC1", "Oil1", "Oil2", "Wet", "OIl_MC"], rows[2].ValueLabels);
        Assert.True(rows[2].IsClickable);
        Assert.DoesNotContain(rows, r => r.SectionName is OilGlossMenuRows.BodySection or OilGlossMenuRows.ClothSection);
    }

    [Fact]
    public void IgnoresLegacyGroupFlags()
    {
        // 旧清单里还有 HasBodyGroup/HasClothGroup，但菜单已经不再生成身体/衣物两行。
        var manifest = new OilGlossManifest
        {
            Namespace = "JasmOilABCD1234",
            HasBodyGroup = true,
            HasClothGroup = true,
        };

        var rows = OilGlossMenuRows.Create(manifest);

        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, r => r.SectionName is OilGlossMenuRows.BodySection or OilGlossMenuRows.ClothSection);
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
        // 新补丁只引用总开关，不再写身体/衣物变量。
        Assert.DoesNotContain($@"$\{ns}\oil_body", menu);
        Assert.DoesNotContain($@"$\{ns}\oil_cloth", menu);
        Assert.Contains("行 0: [KeyOilMaster]", menu);
        Assert.Contains("行 1: [KeyOilStrength]", menu);
        Assert.Contains("行 2: [KeyOilTexture]", menu);
        Assert.Contains(@"$\KatFXMatcap\oilstrength", menu);
        Assert.Contains(@"$\KatFXMatcap\oilSkin", menu);
        Assert.Contains("slider_v", menu);
        // 强度上限 2.0 写进滑块轨道
        Assert.Contains("/ 2 * ", menu);
        Assert.Contains("slider_v > 2", menu);

        // 本体 Matcap.ini 的 [KeyOilToggle] 属于 KatFXMatcap 命名空间，不进菜单
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
        Assert.Contains("油光强度", manifest);
        Assert.Contains("油光贴图", manifest);
        Assert.Contains("油光", manifest);
        Assert.DoesNotContain("身体油光", manifest);
        Assert.DoesNotContain("衣物油光", manifest);
    }

    [Fact]
    public void AddsOilRowsEvenWhenAuthorHasClickableMenu()
    {
        var mod = CreateMod();
        File.WriteAllText(Path.Combine(mod, "author_menu.ini"), """
            [KeyAuthorClick]
            key = VK_LBUTTON
            type = hold

            [Present]
            $my_mouse_x = cursor_x * res_width
            run = CommandListDrawAuthorMenu
            """);

        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        var apply = OilGlossPatcher.Apply(mod, body: body);
        Assert.Equal(OilGlossApplyStatus.Applied, apply.Status);

        // OilGlossService.ToOptions 在油光 manifest 存在时会带上这个开关
        var result = InGameMenuGenerator.Generate(mod, new InGameMenuOptions
        {
            ExtraRows = OilGlossMenuRows.Create(apply.Manifest!),
            AllowWithExistingClickableMenu = true,
        });

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);
        var menu = File.ReadAllText(Path.Combine(mod, "JASM_Menu.ini"));
        Assert.Contains("行 0: [KeyOilMaster]", menu);
        Assert.Contains("行 1: [KeyOilStrength]", menu);
        Assert.Contains("行 2: [KeyOilTexture]", menu);
    }

    [Fact]
    public void GeneratesMenuForModWithoutAnyKeySections()
    {
        var mod = Path.Combine(_root, "OilOnly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.ini"), """
            namespace = TestMod

            [Constants]
            global $object_detected = 0

            [TextureOverrideComponent3]
            hash = f8b55eeb
            match_first_index = 0
            $object_detected = 1
            drawindexed = 900, 0, 0

            [TextureOverrideComponent4]
            drawindexed = 500, 900, 0

            [Present]
            if $object_detected
                post $object_detected = 0
            endif
            """, new UTF8Encoding(false));

        var body = OilGlossBodyProvisioner.Ensure(mod, CreateAssets());
        var apply = OilGlossPatcher.Apply(mod, body: body);
        Assert.Equal(OilGlossApplyStatus.Applied, apply.Status);

        // 完全没有 [Key*]：只靠注入的油光三行也应该能生成菜单
        var result = InGameMenuGenerator.Generate(mod, new InGameMenuOptions
        {
            ExtraRows = OilGlossMenuRows.Create(apply.Manifest!),
        });

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);
        var menu = File.ReadAllText(Path.Combine(mod, "JASM_Menu.ini"));
        Assert.Contains("行 0: [KeyOilMaster]", menu);
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

            [TextureOverrideComponent3]
            hash = f8b55eeb
            match_first_index = 0
            $object_detected = 1
            drawindexed = 900, 0, 0

            [TextureOverrideComponent4]
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