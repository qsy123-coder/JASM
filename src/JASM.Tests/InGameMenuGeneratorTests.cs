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
    public void GenerateAddsAsciiNamespaceForNamespaceLessMod()
    {
        var mod = CreateMod("绯雪-测试", NamespaceLessIni);

        var result = InGameMenuGenerator.Generate(mod);

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);

        // 作者 ini 开头补了一行 ASCII namespace（含中文路径的限定名 3DMigoto 解析不了）
        var authorIni = File.ReadAllText(Path.Combine(mod, "Normal", "mod.ini"));
        Assert.StartsWith(IniFileText.AddedNamespaceMarker, authorIni);
        var nsMatch = System.Text.RegularExpressions.Regex.Match(authorIni, @"namespace = (Jasm[0-9A-F]{8})");
        Assert.True(nsMatch.Success, authorIni);

        // 菜单用这个 ASCII 命名空间访问 Mod 变量
        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.Contains($@"if $\{nsMatch.Groups[1].Value}\xiezi == 4", menu);
        Assert.DoesNotContain(@"$\Mods\", menu);
    }

    [Fact]
    public void GatesMenuOnModsOwnObjectDetectedVariable()
    {
        var mod = CreateMod("GateMod", ObjectDetectedIni);

        InGameMenuGenerator.Generate(mod);

        var authorIni = File.ReadAllText(Path.Combine(mod, "Normal", "mod.ini"));
        var ns = System.Text.RegularExpressions.Regex.Match(authorIni, @"namespace = (Jasm[0-9A-F]{8})")
            .Groups[1].Value;
        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));

        // 每个 Mod 补了 namespace 后，object_detected 是独立的；用它门控，避免多份菜单同时亮
        Assert.Contains($@"if $\{ns}\object_detected == 1", menu);
        Assert.Contains("present == 0", menu);
        Assert.DoesNotContain(@"condition = $\" + ns, menu);
        // 有 per-character 变量时**不再**叠加 hash 信号：hash 命中的是网格资源，
        // 游戏暂停 / 时间冻结时会让上一件 Mod 一直“在场”，导致两个角色的菜单同时出现。
        Assert.DoesNotContain("[TextureOverrideJasmDetect", menu);
        Assert.DoesNotContain("age = time -", menu);
    }

    [Fact]
    public void GatesOnFileThatActuallySetsObjectDetected()
    {
        // 作者把 [Key*] 放在一个 ini、把 $object_detected 放在另一个带命名空间的 ini。
        // 门控必须指向真正设置变量的命名空间（BodyNs），否则条件永远为 0、菜单不显示。
        var mod = CreateMod("MultiIni", """
            namespace = ButtonNs

            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9
            """);
        File.WriteAllText(Path.Combine(mod, "Normal", "05_Body.ini"), """
            namespace = BodyNs

            [Constants]
            global $object_detected = 0

            [TextureOverrideComponent0]
            hash = f8b55eeb
            $object_detected = 1
            """);

        InGameMenuGenerator.Generate(mod);

        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.Contains(@"if $\BodyNs\object_detected == 1", menu);
        Assert.DoesNotContain(@"$\ButtonNs\object_detected", menu);
    }

    [Fact]
    public void DropsGlobalObjectDetectedWhenNamespacedVariableExists()
    {
        // 全局 $object_detected 是所有 Mod 共享的：有命名空间变量时不能混进门控，
        // 否则别的角色把它置 1 时这份菜单也会亮（两个角色的菜单同时出现）。
        var mod = CreateMod("MixedDetect", """
            namespace = ButtonNs

            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9
            """);
        File.WriteAllText(Path.Combine(mod, "Normal", "05_Body.ini"), """
            namespace = BodyNs

            [Constants]
            global $object_detected = 0

            [TextureOverrideComponent0]
            hash = f8b55eeb
            $object_detected = 1
            """);
        File.WriteAllText(Path.Combine(mod, "Normal", "03_Aux.ini"), """
            [Constants]
            global $object_detected = 0

            [TextureOverrideAux]
            hash = abcdef01
            $object_detected = 1
            """);

        InGameMenuGenerator.Generate(mod);

        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.Contains(@"$\BodyNs\object_detected", menu);
        Assert.DoesNotContain("if $object_detected == 1", menu);
    }

    [Fact]
    public void GatesOnEveryFormNamespaceWithLogicalOr()
    {
        // 普通 / 大招两套形态各有一个 $object_detected；任意一套绘制时菜单都要显示。
        var mod = CreateMod("TwoForms", """
            namespace = ButtonNs

            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9
            """);
        File.WriteAllText(Path.Combine(mod, "Normal", "05_Body.ini"), """
            namespace = BodyNs

            [Constants]
            global $object_detected = 0

            [TextureOverrideComponent0]
            hash = f8b55eeb
            $object_detected = 1
            """);
        Directory.CreateDirectory(Path.Combine(mod, "Ult"));
        File.WriteAllText(Path.Combine(mod, "Ult", "mod.ini"), """
            namespace = UltNs

            [Constants]
            global $object_detected = 0

            [TextureOverrideComponent0]
            hash = abcdef01
            $object_detected = 1
            """);

        InGameMenuGenerator.Generate(mod);

        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.Contains(@"if $\BodyNs\object_detected == 1 || $\UltNs\object_detected == 1", menu);
        Assert.Contains("present == 0", menu);
    }

    [Fact]
    public void GatesOnGlobalObjectDetectedWhenOwnerFileHasNoNamespace()
    {
        var mod = CreateMod("GlobalDetect", """
            namespace = ButtonNs

            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9
            """);
        File.WriteAllText(Path.Combine(mod, "Normal", "05_Body.ini"), """
            [Constants]
            global $object_detected = 0

            [TextureOverrideComponent0]
            hash = f8b55eeb
            $object_detected = 1
            """);

        InGameMenuGenerator.Generate(mod);

        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.Contains("if $object_detected == 1", menu);
        Assert.Contains("present == 0", menu);
        Assert.DoesNotContain(@"$\ButtonNs\object_detected", menu);
    }

    [Fact]
    public void NamespacesAreUniqueAcrossModsWithSameIniPath()
    {
        var first = CreateMod("ModA", NamespaceLessIni);
        var second = CreateMod("ModB", NamespaceLessIni);

        InGameMenuGenerator.Generate(first);
        InGameMenuGenerator.Generate(second);

        var firstNs = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(Path.Combine(first, "Normal", "mod.ini")), @"namespace = (Jasm[0-9A-F]{8})")
            .Groups[1].Value;
        var secondNs = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(Path.Combine(second, "Normal", "mod.ini")), @"namespace = (Jasm[0-9A-F]{8})")
            .Groups[1].Value;

        Assert.NotEqual(firstNs, secondNs);
    }

    [Fact]
    public void RemoveRestoresAuthorIniAfterNamespaceInsertion()
    {
        var mod = CreateMod("绯雪-测试", NamespaceLessIni);
        InGameMenuGenerator.Generate(mod);

        Assert.True(InGameMenuGenerator.Remove(mod));

        var authorIni = File.ReadAllText(Path.Combine(mod, "Normal", "mod.ini"));
        Assert.Equal(NamespaceLessIni, authorIni);
        Assert.False(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu.ini")));
    }

    [Fact]
    public void NamespaceInsertionPreservesGb18030Encoding()
    {
        var mod = Path.Combine(_root, "GbMod");
        Directory.CreateDirectory(Path.Combine(mod, "Normal"));
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gbk = Encoding.GetEncoding(54936);
        const string content = "; 中文注释\r\n[KeyShoes]\r\nkey = VK_DOWN\r\ntype = cycle\r\n$xiezi = 4,8,9";
        var iniPath = Path.Combine(mod, "Normal", "mod.ini");
        File.WriteAllBytes(iniPath, gbk.GetBytes(content));

        InGameMenuGenerator.Generate(mod);

        var decoded = gbk.GetString(File.ReadAllBytes(iniPath));
        Assert.Contains("; 中文注释", decoded);
        Assert.Contains(IniFileText.AddedNamespaceMarker, decoded);
        Assert.Contains("namespace = Jasm", decoded);
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
    public void ExtractsDetectionHashFromObjectDetectedOverride()
    {
        var mod = CreateMod("MyMod", """
            [Constants]
            global $object_detected = 0

            [TextureOverrideComponent0]
            hash = f8b55eeb
            match_first_index = 0
            $object_detected = 1
            """);

        Assert.Equal("f8b55eeb", InGameMenuGenerator.ExtractDetectionHash(mod));
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
    public void AppliesRowHideAndOrder()
    {
        var mod = CreateMod("LayoutMod");
        var rows = ModIniToggleParser.ParseDirectory(mod);
        var shoes = rows.Single(r => r.SectionName == "[KeyShoes]");
        var hair = rows.Single(r => r.SectionName == "[KeyHair]");

        InGameMenuGenerator.Generate(mod, new InGameMenuOptions
        {
            RowOrder = [hair.Key, shoes.Key],
            HiddenRows = [shoes.Key], // 隐藏优先于排序
        });

        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.DoesNotContain(@"$\FoxMain\xiezi", menu);
        Assert.Contains(@"$\FoxMain\hair", menu);
        Assert.Contains("行 0: [KeyHair]", menu);
        Assert.DoesNotContain("行 1:", menu);
    }

    [Fact]
    public void IsUpToDateIsFalseForMenuFromOlderGenerator()
    {
        var mod = CreateMod("MyMod");

        Assert.False(InGameMenuGenerator.IsUpToDate(mod));

        InGameMenuGenerator.Generate(mod);
        Assert.True(InGameMenuGenerator.IsUpToDate(mod));

        // 模拟升级前生成的菜单：文件还在，但 manifest 记录的是旧版本
        var manifest = Path.Combine(mod, "Normal", "JASM_Menu", "manifest.json");
        var json = File.ReadAllText(manifest).Replace(
            $"\"generatorVersion\": {InGameMenuOptions.CurrentGeneratorVersion}",
            "\"generatorVersion\": 2");
        File.WriteAllText(manifest, json);

        Assert.True(InGameMenuGenerator.IsGenerated(mod));
        Assert.False(InGameMenuGenerator.IsUpToDate(mod));
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

    private const string NamespaceLessIni = """
        [KeyShoes]
        key = VK_DOWN
        type = cycle
        $xiezi = 4,8,9
        """;

    private const string ObjectDetectedIni = """
        [Constants]
        global $object_detected = 0

        [KeyShoes]
        key = VK_DOWN
        type = cycle
        $xiezi = 4,8,9

        [TextureOverrideComponent0]
        hash = f8b55eeb
        $object_detected = 1

        [Present]
        if $object_detected
            post $object_detected = 0
        endif
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
