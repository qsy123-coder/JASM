using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace JASM.Tests;

/// <summary>
/// 生成器的 ini 必须同时满足三件事：3DMigoto 能加载、点击等价于按一次键、命名空间隔离。
/// 这些断言都是「文本层面」的契约，不依赖游戏；实机效果由 Phase 0 手测覆盖。
/// </summary>
public class InGameMenuIniBuilderTests
{
    private static ModIniToggleRow Row(
        string label,
        string variable = "xiezi",
        string ns = "Fox",
        params string[] values) => new()
    {
        SectionName = "[Key" + label.Replace(" ", string.Empty) + "]",
        IniRelativePath = "mod.ini",
        Namespace = ns,
        VariableName = variable,
        Values = values.Length == 0 ? ["4", "8"] : values,
        KeyType = "cycle",
        KeyDisplay = "↓",
        Label = label,
    };

    [Fact]
    public void DeclaresNamespaceAndQualifiedVariable()
    {
        var build = InGameMenuIniBuilder.Build([Row("Shoes")], "abcd1234", new InGameMenuOptions());

        // 必须有 ASCII namespace：否则 3DMigoto 用 ini 路径当变量命名空间，中文路径会让变量名非法
        Assert.Contains("namespace = JasmMenuabcd1234", build.IniText);
        Assert.Contains("$jasm_abcd1234_open", build.IniText);
        Assert.Contains(@"if $\Fox\xiezi == 4", build.IniText);
        Assert.Contains(@"$\Fox\xiezi = 8", build.IniText);
        Assert.Contains(@"$\Fox\xiezi = 4", build.IniText);
    }

    [Fact]
    public void CycleChainCoversEveryValueAndWraps()
    {
        var build = InGameMenuIniBuilder.Build([Row("Shoes", "xiezi", "Fox", "4", "8", "9")], "x",
            new InGameMenuOptions());

        Assert.Contains(@"if $\Fox\xiezi == 4", build.IniText);
        Assert.Contains(@"if $\Fox\xiezi == 8", build.IniText);
        Assert.Contains(@"if $\Fox\xiezi == 9", build.IniText);
        Assert.Contains(@"$\Fox\xiezi = 4", build.IniText);
    }

    [Fact]
    public void WritesOpenKeyAndMouseBindings()
    {
        var build = InGameMenuIniBuilder.Build([Row("Shoes")], "x",
            new InGameMenuOptions { OpenKeyTokens = "CTRL SHIFT VK_M" });

        Assert.Contains("key = CTRL SHIFT VK_M", build.IniText);
        Assert.Contains("$jasm_x_open = 0,1", build.IniText);
        // 纯鼠标交互：Alt+左键点击、Alt+右键拖动（鸣潮按住 Alt 时光标解锁）
        Assert.Contains("key = ALT VK_LBUTTON", build.IniText);
        Assert.Contains("key = ALT VK_RBUTTON", build.IniText);
        Assert.DoesNotContain("key = UP", build.IniText);
        Assert.DoesNotContain("key = DOWN", build.IniText);
        Assert.DoesNotContain("key = F9", build.IniText);
    }

    [Fact]
    public void PaginatesAboveRowLimit()
    {
        var rows = Enumerable.Range(0, 12).Select(i => Row("Item" + i, "var" + i)).ToList();
        var build = InGameMenuIniBuilder.Build(rows, "x", new InGameMenuOptions { MaxRowsPerPage = 10 });

        Assert.Equal(2, build.PageCount);
        Assert.Contains("[CommandListJasmPage0_x]", build.IniText);
        Assert.Contains("[CommandListJasmPage1_x]", build.IniText);
    }

    [Fact]
    public void GeneratesTexturesForLabelsValuesAndKeys()
    {
        var build = InGameMenuIniBuilder.Build([Row("Shoes", values: ["4", "8", "9"])], "x",
            new InGameMenuOptions());

        Assert.Contains(build.Textures, t => t.FileName == "JASM_Menu/row.dds");
        Assert.Contains(build.Textures, t => t.FileName == "JASM_Menu/label_00.dds" && t.Text == "Shoes");
        Assert.Contains(build.Textures, t => t.FileName == "JASM_Menu/value_00_02.dds" && t.Text == "9");
        Assert.Contains(build.Textures, t => t.FileName == "JASM_Menu/hint_00.dds");
    }

    [Fact]
    public void UnclickableRowHasNoCycleBlock()
    {
        var row = new ModIniToggleRow
        {
            SectionName = "[KeyHold]",
            IniRelativePath = "mod.ini",
            Namespace = "Fox",
            KeyType = "hold",
            KeyDisplay = "→",
            Label = "Hold",
        };

        var build = InGameMenuIniBuilder.Build([row], "x", new InGameMenuOptions());

        Assert.DoesNotContain("$jasm_in_rect == 1 && $jasm_click == 1", build.IniText);
    }

    [Fact]
    public void NeverEmitsUnsupportedAndOperator()
    {
        // 本机 3DMigoto 的 CommandList if 不支持 &&（日志会报 Variable not recognized，
        // 并打乱 if/endif 结构导致绘制无条件执行）。作者条件即使含 && 也不能原样带进来。
        var row = Row("Shoes") with { Condition = "$a == 1 && $b == 0" };

        var build = InGameMenuIniBuilder.Build([row], "x", new InGameMenuOptions());

        Assert.DoesNotContain("&&", build.IniText);
    }

    [Fact]
    public void PanelHeightFollowsVisibleRows()
    {
        var build = InGameMenuIniBuilder.Build([Row("A")], "x", new InGameMenuOptions());

        // 156(行起点) + 1*(64+6) + 3*26 + 16
        Assert.Equal(320, build.PanelHeight);
        Assert.Equal(620, build.PanelWidth);
    }

    [Fact]
    public void DrawSectionIsCustomShaderNotCommandList()
    {
        var build = InGameMenuIniBuilder.Build([Row("Shoes")], "x", new InGameMenuOptions());

        // vs/ps/blend/Draw 只属于 [CustomShader*]；写成 [CommandList*] 会让 Draw 用错管线，
        // 实机表现为 D3D device lost (0x887A0006 HUNG)。
        Assert.Contains("[CustomShaderJasmQuad_x]", build.IniText);
        Assert.DoesNotContain("[CommandListJasmQuad_x]", build.IniText);
        Assert.Contains("run = CustomShaderJasmQuad_x", build.IniText);
        Assert.Contains("vs = JASM_Menu/draw_2d.hlsl", build.IniText);
        Assert.Contains("Draw     = 4,0", build.IniText);
    }

    [Fact]
    public void GeneratedIniIsStructurallyBalancedAndSelfContained()
    {
        var rows = Enumerable.Range(0, 12).Select(i => Row("Item" + i, "var" + i, "Ns" + i, "0", "1", "2")).ToList();
        var build = InGameMenuIniBuilder.Build(rows, "x", new InGameMenuOptions());

        var lines = build.IniText.Split('\n').Select(l => l.Trim()).ToArray();

        // if / endif 必须配平（3DMigoto 解析失败通常只报一行，很难查）
        var depth = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("if ", StringComparison.Ordinal) || line.StartsWith("if(", StringComparison.Ordinal))
                depth++;
            else if (line == "endif")
                depth--;

            Assert.True(depth >= 0, "endif 多于 if");
        }

        Assert.Equal(0, depth);

        var sections = lines
            .Where(l => l.StartsWith('[') && l.EndsWith(']'))
            .Select(l => l[1..^1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Where(l =>
                     l.StartsWith("run = CommandListJasm", StringComparison.Ordinal)
                     || l.StartsWith("run = CustomShaderJasm", StringComparison.Ordinal)))
        {
            var target = line["run = ".Length..].Trim();
            Assert.Contains(target, sections);
        }

        foreach (var line in lines.Where(l => l.StartsWith("ps-t100 = ResourceJasm", StringComparison.Ordinal)))
        {
            var target = line["ps-t100 = ".Length..].Trim();
            Assert.Contains(target, sections);
        }
    }
}
