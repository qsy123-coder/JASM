using System.Text;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace JASM.Tests;

/// <summary>
/// <see cref="ModIniToggleParser"/> 只做一件事：把 <c>[Key*]</c> 里的「循环变量」认出来。
/// 这里锁住三个容易回退的点：命名空间（决定能不能写对别人的变量）、
/// 值列表顺序（4,8,9 不能排序）、以及「多个赋值 = 不可点击」的保守判定。
/// </summary>
public class ModIniToggleParserTests
{
    [Fact]
    public void LocalizesMenuLabelsAndKeepsRawLabel()
    {
        var rows = ModIniToggleParser.ParseText("""
            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9

            [KeyHairAcc]
            key = VK_UP
            type = cycle
            $acc = 0,1
            """, "mod.ini");

        var shoes = rows.Single(r => r.SectionName == "[KeyShoes]");
        Assert.Equal("鞋子", shoes.Label);
        Assert.Equal("Shoes", shoes.RawLabel);

        var acc = rows.Single(r => r.SectionName == "[KeyHairAcc]");
        Assert.Equal("发饰", acc.Label);
        Assert.Equal("Hair Acc", acc.RawLabel);
    }

    [Fact]
    public void KeepsMultiWordCommentWhenNotEveryWordTranslates()
    {
        var rows = ModIniToggleParser.ParseText("""
            ; Shoes are picked from the wardrobe
            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9
            """, "mod.ini");

        var row = Assert.Single(rows);
        Assert.Equal("Shoes are picked from the wardrobe", row.Label);
        Assert.Equal("Shoes are picked from the wardrobe", row.RawLabel);
    }

    [Fact]
    public void ParseDirectoryCapturesGlobalDefaultForReset()
    {
        var root = Path.Combine(Path.GetTempPath(), "jasm-defaults-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "mod.ini"), """
            [Constants]
            global persist $xiezi = 4

            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9
            """);

        try
        {
            var row = Assert.Single(ModIniToggleParser.ParseDirectory(root));
            Assert.Equal("4", row.DefaultValue);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParsesSimpleCycleRowWithNamespace()
    {
        var rows = ModIniToggleParser.ParseText("""
            namespace = FoxHiyukiMainV1824

            ; Shoes are picked from the wardrobe
            [KeyShoes]
            condition = $object_detected == 1
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8,9
            """, "Normal/Config/05_00_Normal.ini");

        var row = Assert.Single(rows);
        Assert.Equal("[KeyShoes]", row.SectionName);
        Assert.Equal("FoxHiyukiMainV1824", row.Namespace);
        Assert.Equal("xiezi", row.VariableName);
        Assert.Equal(["4", "8", "9"], row.Values);
        Assert.Equal("cycle", row.KeyType);
        Assert.Equal("↓", row.KeyDisplay);
        Assert.Equal("Shoes are picked from the wardrobe", row.Label);
        Assert.True(row.IsClickable);
        Assert.Equal(@"$\FoxHiyukiMainV1824\xiezi", row.QualifiedVariable);
    }

    [Fact]
    public void KeepsNonSequentialValueOrder()
    {
        var rows = ModIniToggleParser.ParseText("""
            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 9,4,8
            """, "mod.ini");

        Assert.Equal(["9", "4", "8"], Assert.Single(rows).Values);
    }

    [Fact]
    public void MultipleAssignmentsAreNotClickable()
    {
        var rows = ModIniToggleParser.ParseText("""
            [KeyResetMenu]
            key = /
            type = cycle
            $menuOffsetX = 500
            $menuOffsetY = 30
            """, "mod.ini");

        var row = Assert.Single(rows);
        Assert.False(row.IsClickable);
        Assert.Empty(row.Values);
        Assert.Equal(string.Empty, row.VariableName);
    }

    [Fact]
    public void HoldKeyWithoutVariableIsDisplayedButNotClickable()
    {
        var rows = ModIniToggleParser.ParseText("""
            [KeyHold]
            no_ctrl no_shift VK_RIGHT
            type = hold
            """, "mod.ini");

        var row = Assert.Single(rows);
        Assert.False(row.IsClickable);
        Assert.Equal("→", row.KeyDisplay);
        Assert.Equal("长按", row.Label);
    }

    [Fact]
    public void IgnoresPreAndPostAssignments()
    {
        var rows = ModIniToggleParser.ParseText("""
            [KeyMouseClick]
            key = VK_LBUTTON
            type = hold
            $isMouseButtonDown = 1
            post $isMouseButtonDown = 0
            """, "menu.ini");

        var row = Assert.Single(rows);
        Assert.False(row.IsClickable);
        Assert.Empty(row.Values);
    }

    [Fact]
    public void DerivesLabelFromCamelCaseSectionName()
    {
        var rows = ModIniToggleParser.ParseText("""
            [KeyBlackHairV176]
            key = 0
            type = cycle
            $black_hair_v176 = 0,1
            """, "mod.ini");

        // 段落名先用驼峰拆成 "Black Hair V176"，再接词表本地化
        Assert.Equal("黑色发型V176", Assert.Single(rows).Label);
    }

    [Fact]
    public void ReadsGb18030Comments()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var gb18030 = Encoding.GetEncoding(54936);
        var bytes = gb18030.GetBytes("""
            ; 发型开关
            [KeyHair]
            key = Num5
            type = cycle
            $hair = 0,1
            """);

        var text = IniFileText.Decode(bytes);
        var row = Assert.Single(ModIniToggleParser.ParseText(text, "mod.ini"));

        Assert.Equal("发型开关", row.Label);
        Assert.True(row.IsClickable);
    }

    [Fact]
    public void DeduplicatesSameVariableBoundTwice()
    {
        var rows = ModIniToggleParser.ParseText("""
            [KeyShoes]
            key = VK_DOWN
            type = cycle
            $xiezi = 4,8

            [KeyShoesAlt]
            key = VK_UP
            type = cycle
            $xiezi = 4,8
            """, "mod.ini");

        Assert.Single(rows);
    }

    [Fact]
    public void QuotedValuesKeepTheirQuotesForIniGeneration()
    {
        var rows = ModIniToggleParser.ParseText("""
            [KeyMode]
            key = 1
            type = cycle
            $mode = "day", "night"
            """, "mod.ini");

        Assert.Equal(["\"day\"", "\"night\""], Assert.Single(rows).Values);
    }
}
