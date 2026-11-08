using System.Text;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace JASM.Tests;

/// <summary>
/// 端到端（文件层）验证：生成只新增 JASM_Menu.ini + JASM_Menu/，作者 ini 字节不变；
/// 移除能精确删干净；已有可点击菜单的 Mod 会被跳过。
/// </summary>
public class InGameMenuGeneratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jasm-menu-gen-" + Guid.NewGuid().ToString("N"));

    public InGameMenuGeneratorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void GeneratesOnlyAdditiveFilesAndKeepsAuthorIniByteIdentical()
    {
        var mod = CreateMod("MyMod");

        var result = InGameMenuGenerator.Generate(mod);

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);
        Assert.True(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu.ini")));
        Assert.True(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu", "manifest.json")));
        Assert.True(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu", "row.dds")));
        Assert.True(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu", "draw_2d.hlsl")));

        Assert.Equal(OriginalIni, File.ReadAllText(Path.Combine(mod, "Normal", "mod.ini")));
    }

    [Fact]
    public void GeneratedIniCarriesMarkerAndQualifiedVariables()
    {
        var mod = CreateMod("MyMod");

        InGameMenuGenerator.Generate(mod);

        var ini = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.Contains("JASM 游戏内切换菜单", ini);
        Assert.Contains(@"$\FoxMain\xiezi", ini);
        Assert.DoesNotContain("namespace = FoxMain", ini); // 不沿用作者的 namespace
        Assert.Contains("namespace = JasmMenu", ini);      // 必须有 ASCII namespace，避免中文路径进入变量名
        Assert.Contains("$jasm_", ini);
    }

    [Fact]
    public void RemoveDeletesOnlyJasmArtifacts()
    {
        var mod = CreateMod("MyMod");
        InGameMenuGenerator.Generate(mod);

        Assert.True(InGameMenuGenerator.Remove(mod));

        Assert.False(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu.ini")));
        Assert.False(Directory.Exists(Path.Combine(mod, "Normal", "JASM_Menu")));
        Assert.True(File.Exists(Path.Combine(mod, "Normal", "mod.ini")));
        Assert.Equal(OriginalIni, File.ReadAllText(Path.Combine(mod, "Normal", "mod.ini")));
    }

    [Fact]
    public void SkipsModThatAlreadyHasClickableMenu()
    {
        var mod = CreateMod("MyMod");
        File.WriteAllText(Path.Combine(mod, "Normal", "menu.ini"), """
            [KeyMouseClick]
            key = VK_LBUTTON
            type = hold

            [Present]
            $my_mouse_x = cursor_x * res_width
            run = CommandListDrawUI
            """);

        var result = InGameMenuGenerator.Generate(mod);

        Assert.Equal(InGameMenuGenerationStatus.SkippedExistingClickableMenu, result.Status);
        Assert.False(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu.ini")));
    }

    [Fact]
    public void SkipsModWithoutClickableRows()
    {
        var mod = CreateMod("MyMod", """
            [KeyHold]
            key = VK_RIGHT
            type = hold
            """);

        var result = InGameMenuGenerator.Generate(mod);

        Assert.Equal(InGameMenuGenerationStatus.SkippedNoClickableRows, result.Status);
    }

    [Fact]
    public void RegenerationIsIdempotent()
    {
        var mod = CreateMod("MyMod");

        var first = InGameMenuGenerator.Generate(mod);
        var second = InGameMenuGenerator.Generate(mod);

        Assert.Equal(InGameMenuGenerationStatus.Generated, first.Status);
        Assert.Equal(InGameMenuGenerationStatus.Generated, second.Status);
        Assert.True(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu.ini")));
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

    private const string OriginalIni = """
        namespace = FoxMain

        [KeyShoes]
        key = VK_DOWN
        type = cycle
        $xiezi = 4,8,9

        [KeyHair]
        key = Num5
        type = cycle
        $hair = 0,1
        """;

    private string CreateMod(string name, string iniContent = OriginalIni)
    {
        var mod = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(mod, "Normal"));
        File.WriteAllText(Path.Combine(mod, "Normal", "mod.ini"), iniContent,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return mod;
    }
}
