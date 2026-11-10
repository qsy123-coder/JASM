using System.Text;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace JASM.Tests;

/// <summary>
/// 端到端（文件层）验证：生成只新增 JASM_Menu.ini + JASM_Menu/，作者 ini 字节不变；
/// 移除能精确删干净；作者已有可点击菜单时仍会生成。
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
    public void ShowsOnlyTheActiveFormRows()
    {
        // 多形态 Mod（普通 / 大招各自的 ini）：非当前形态的行整行不画（不是只置灰），
        // 两套形态共用同一段槽位，当前形态的行自动顶上来。
        var mod = CreateMod("FormMod", """
            namespace = ButtonNs

            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9
            """);
        File.WriteAllText(Path.Combine(mod, "Normal", "norm.ini"), """
            namespace = NormNs

            [Constants]
            global $object_detected = 0

            [KeyNormPart]
            key = VK_1
            type = cycle
            $part = 0,1

            [TextureOverrideComponent0]
            hash = aaaa
            $object_detected = 1
            """);
        File.WriteAllText(Path.Combine(mod, "Normal", "ult.ini"), """
            namespace = UltNs

            [Constants]
            global $object_detected = 0

            [KeyUltPart]
            key = VK_2
            type = cycle
            $part = 0,1

            [TextureOverrideComponent0]
            hash = bbbb
            $object_detected = 1
            """);

        InGameMenuGenerator.Generate(mod);

        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        // 每个形态的行列表整体被形态门控包住：非当前形态整行不画
        Assert.Contains(@"if $\NormNs\object_detected == 1", menu);
        Assert.Contains(@"if $\UltNs\object_detected == 1", menu);
        Assert.DoesNotContain(@"if $\NormNs\object_detected != 1", menu);
        Assert.DoesNotContain(@"if $\UltNs\object_detected != 1", menu);
    }

    [Fact]
    public void KeepsEveryFormRowWhenAuthorInisHaveNoNamespace()
    {
        // 真实案例（心月狐）：普通 / 大招两套形态各一份 ini，变量同名、作者都没写 namespace。
        // 首次生成必须同时保留两套行并给两个文件各补一个 ASCII namespace；
        // 否则只剩第一套，另一套形态在游戏里没有菜单行（要靠再生成一次才“恢复”）。
        var mod = CreateMod("MultiFormNamespaceLess", """
            [Constants]
            global $object_detected = 0

            [KeySwapAccessory]
            key = /
            type = cycle
            $swapvar_accessory = -1, 0

            [TextureOverrideComponent3]
            hash = normalhash
            $object_detected = 1
            """);
        Directory.CreateDirectory(Path.Combine(mod, "ult"));
        File.WriteAllText(Path.Combine(mod, "ult", "mod.ini"), """
            [Constants]
            global $object_detected = 0

            [KeySwapAccessory]
            key = /
            type = cycle
            $swapvar_accessory = -1, 0

            [TextureOverrideComponent4]
            hash = ulthash
            $object_detected = 1
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var result = InGameMenuGenerator.Generate(mod);

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);
        Assert.Equal(2, result.ClickableRowCount);

        var normalNs = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(Path.Combine(mod, "Normal", "mod.ini")), @"namespace = (Jasm[0-9A-F]{8})")
            .Groups[1].Value;
        var ultNs = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(Path.Combine(mod, "ult", "mod.ini")), @"namespace = (Jasm[0-9A-F]{8})")
            .Groups[1].Value;

        Assert.NotEmpty(normalNs);
        Assert.NotEmpty(ultNs);
        Assert.NotEqual(normalNs, ultNs);

        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.Contains($@"$\{normalNs}\swapvar_accessory", menu);
        Assert.Contains($@"$\{ultNs}\swapvar_accessory", menu);
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
    public void GeneratesMenuEvenWhenAuthorHasClickableMenu()
    {
        // 可拖动的帮助图 / 浮层同样有 [Present] + 光标 + 鼠标键（绯雪 mod 的 draw_image.ini），
        // 不能据此拒绝生成；JASM 菜单只在 F7 时显示，与作者菜单共存。
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

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);
        Assert.True(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu.ini")));
    }

    [Fact]
    public void GeneratesMenuWithExtraRowsWhenAuthorHasClickableMenu()
    {
        // 油光补丁必须能出现在 F7 里：作者自己的可点击菜单不再是跳过条件。
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

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);
        Assert.True(File.Exists(Path.Combine(mod, "Normal", "JASM_Menu.ini")));
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
    public void DropsRowsWhoseNamespaceCannotBeWrittenIntoExpressions()
    {
        // 作者命名空间带连字符（Mods\Xin-HunSha 是常见写法）：这个变量写进 3DMigoto
        // 表达式会整行报 Syntax Error，取值永远显示最后一个、点击也写不对；
        // 更糟的是无法识别的 if 会被丢掉、让里面的语句无条件执行。这类行必须整行移除。
        var mod = CreateMod("HyphenRows", """
            namespace = Mods\Xin-HunSha

            [KeyQunzi]
            key = VK_DOWN
            type = cycle
            $qunzi = 0,1
            """);
        File.WriteAllText(Path.Combine(mod, "Normal", "02_Safe.ini"), """
            namespace = SafeNs

            [KeyShoes]
            key = VK_UP
            type = cycle
            $xiezi = 0,1
            """);

        var result = InGameMenuGenerator.Generate(mod);

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);
        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.Contains(@"$\SafeNs\xiezi", menu);
        Assert.DoesNotContain("Xin-HunSha", menu);
        Assert.DoesNotContain("[KeyQunzi]", menu);
    }

    [Fact]
    public void FallsBackToHashGatingWhenNamespaceCannotBeReferenced()
    {
        // 变量门控写不进表达式时退回纹理 hash（纯数字，不受解析字符集限制）。
        // 不退回就只能写一条语法错误的 if，而它会让 present 永远为 1。
        var mod = CreateMod("HyphenGate", """
            namespace = Mods\Xin-HunSha

            [Constants]
            global $object_detected = 0

            [KeyQunzi]
            key = VK_DOWN
            type = cycle
            $qunzi = 0,1

            [TextureOverrideComponent0]
            hash = 9bb8b247
            match_first_index = 0
            $object_detected = 1
            """);
        File.WriteAllText(Path.Combine(mod, "Normal", "02_Safe.ini"), """
            namespace = SafeNs

            [KeyShoes]
            key = VK_UP
            type = cycle
            $xiezi = 0,1
            """);

        var result = InGameMenuGenerator.Generate(mod);

        Assert.Equal(InGameMenuGenerationStatus.Generated, result.Status);
        var menu = File.ReadAllText(Path.Combine(mod, "Normal", "JASM_Menu.ini"));
        Assert.DoesNotContain("Xin-HunSha", menu);
        Assert.Contains("[TextureOverrideJasmDetect_", menu);
        Assert.Contains("hash = 9bb8b247", menu);
        Assert.Contains("age = time -", menu);
    }

    [Fact]
    public void RemovesStaleMenuWhenEveryRowVariableIsUnaddressable()
    {
        // 旧版本会给这类 Mod 生成一份「点了没反应、按 F7 必然弹出」的菜单；
        // 现在一条可驱动的行都不剩，旧菜单必须从磁盘上清掉，否则它还会继续串菜单。
        var mod = CreateMod("HyphenOnly");
        Assert.Equal(InGameMenuGenerationStatus.Generated, InGameMenuGenerator.Generate(mod).Status);

        var iniPath = Path.Combine(mod, "Normal", "JASM_Menu.ini");
        Assert.True(File.Exists(iniPath));

        File.WriteAllText(Path.Combine(mod, "Normal", "mod.ini"), """
            namespace = Mods\Xin-HunSha

            [KeyQunzi]
            key = VK_DOWN
            type = cycle
            $qunzi = 0,1
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var result = InGameMenuGenerator.Generate(mod);

        Assert.Equal(InGameMenuGenerationStatus.SkippedNoClickableRows, result.Status);
        Assert.False(File.Exists(iniPath));
        Assert.False(Directory.Exists(Path.Combine(mod, "Normal", "JASM_Menu")));
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
