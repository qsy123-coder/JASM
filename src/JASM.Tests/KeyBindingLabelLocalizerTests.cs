using GIMI_ModManager.Core.Entities.Mods.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="KeyBindingLabelLocalizer"/>, which turns mod ini section names into the
/// Chinese labels shown in the "按键映射" panel ([KeyShoes] → 鞋子, [Keyxiezi] → 鞋子).
/// Pure unit tests: no file system, no WinUI.
/// </summary>
public class KeyBindingLabelLocalizerTests
{
    // ── 段落名：剥方括号 + 剥 "Key" 前缀 + 翻译 ──────────────

    [Theory]
    [InlineData("[KeyShoes]", "鞋子")]
    [InlineData("[Keyxiezi]", "鞋子")]
    [InlineData("[KeySwapTextures]", "切换贴图")]
    [InlineData("[KeySwapHair]", "切换头发")]
    [InlineData("[KeyHideFirstRunNotification]", "隐藏首次运行通知")]
    [InlineData("[KeyToggleCompatibilityMode]", "开/关兼容模式")]
    [InlineData("[Keyyouguangsiwa]", "亮光丝袜")]
    [InlineData("[Key toggle_mods]", "开/关模组")]
    [InlineData("KeyHelp", "帮助")]
    [InlineData("[Key鞋子]", "鞋子")] // 作者自己写了中文 → 原样保留
    public void LocalizeSectionName_TranslatesSectionNames(string sectionName, string expected)
        => Assert.Equal(expected, KeyBindingLabelLocalizer.LocalizeSectionName(sectionName));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LocalizeSectionName_ReturnsEmptyForBlankInput(string? sectionName)
        => Assert.Equal(string.Empty, KeyBindingLabelLocalizer.LocalizeSectionName(sectionName));

    // ── 标签：整串精确匹配 / 分词 / 拼音 ─────────────────────

    [Theory]
    // 整串精确匹配（分词会拆错，靠 ExactLabels 兜住）
    [InlineData("Boobsize", "胸部尺寸")]
    [InlineData("HeadAcc", "头饰")]
    // 分词逐词翻译
    [InlineData("SwapHair", "切换头发")]
    [InlineData("Top1", "上衣1")]
    [InlineData("ToggleCompatibilityMode", "开/关兼容模式")]
    [InlineData("FaceFXLiquid", "脸部特效液体")]
    [InlineData("EnableMods", "启用模组")]
    // 拼音逐词
    [InlineData("xiezi", "鞋子")]
    [InlineData("toufa", "头发")]
    [InlineData("bozi", "脖子")]
    // 拼音连写 → 最长匹配切分
    [InlineData("youguangsiwa", "亮光丝袜")]
    public void Localize_TranslatesLabels(string label, string expected)
        => Assert.Equal(expected, KeyBindingLabelLocalizer.Localize(label));

    [Theory]
    [InlineData("Swap Hair")]
    [InlineData("swap_hair")]
    [InlineData("SwapHair")]
    public void Localize_IgnoresSeparatorsAndCasing(string label)
        => Assert.Equal("切换头发", KeyBindingLabelLocalizer.Localize(label));

    [Theory]
    [InlineData("Xyzzy")]
    [InlineData("Blorp")]
    [InlineData("shangkanzhege")] // 拆不成拼音连写（shang 切不动）→ 不能硬翻
    public void Localize_LeavesUnknownLabelsUntouched(string label)
        => Assert.Equal(label, KeyBindingLabelLocalizer.Localize(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Localize_ReturnsEmptyForBlankInput(string? label)
        => Assert.Equal(string.Empty, KeyBindingLabelLocalizer.Localize(label));

    // ── 与 ModIniKeyBindingEntry 的接线 ──────────────────────

    [Theory]
    [InlineData("[KeyShoes]", "鞋子")]
    [InlineData("[Keyxiezi]", "鞋子")]
    [InlineData("[KeyHideNotification]", "隐藏通知")]
    public void DisplayLabel_IsLocalized(string sectionName, string expected)
    {
        var entry = new ModIniKeyBindingEntry { SectionName = sectionName };

        Assert.Equal(expected, entry.DisplayLabel);
        // 原始段名必须原样保留，调试面板与日志依赖它
        Assert.Equal(sectionName, entry.SectionName);
    }
}