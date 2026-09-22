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

    // ── 实测补齐：扫本机 247 个不重复段落名后补的词表 ──────────
    //
    // 下面每一行的段名都来自真实 mod 的 ini。补词之前它们**整串**原样显示英文（或中英夹杂），
    // 也就是用户报的「按键映射汉化还是不完整」。删词表条目先看这两组测试。

    [Theory]
    [InlineData("[KeyArmor]", "护甲")]
    [InlineData("[KeyHelm]", "头盔")]
    [InlineData("[KeyCrown]", "王冠")]
    [InlineData("[Keyring]", "戒指")]
    [InlineData("[Keyhubi]", "护臂")]
    [InlineData("[Keybihuan]", "臂环")]
    [InlineData("[Keyruhuan]", "乳环")]
    [InlineData("[Keyjiezhi]", "戒指")]
    [InlineData("[Keyhuahuan]", "花环")]
    [InlineData("[Keydiaozhui]", "吊坠")]
    [InlineData("[Keyxionzhao]", "胸罩")] // 作者把 xiongzhao 拼成了 xionzhao
    [InlineData("[Keyshoe]", "鞋子")]
    [InlineData("[KeySwords]", "剑")]
    [InlineData("[KeyTorso]", "躯干")]
    [InlineData("[KeyVine]", "藤蔓")]
    [InlineData("[Keythorn]", "荆棘")]
    [InlineData("[Keystar]", "星星")]
    [InlineData("[keysmol]", "娇小")]
    [InlineData("[KeyRed]", "红色")]
    [InlineData("[Keypantsu]", "内裤")]
    [InlineData("[keypreg]", "怀孕")]
    [InlineData("[keybukkake]", "颜射")]
    [InlineData("[keybackflap]", "后摆")]
    [InlineData("[keyflappysleeve]", "飘带袖")]
    [InlineData("[keyinnerclothes]", "内衬")]
    [InlineData("[KeyOuterclothe]", "外衣")]
    [InlineData("[KeyMenu]", "菜单")]
    [InlineData("[KeyClickedSlot]", "点击插槽")]
    [InlineData("[KeyCostumeBreak]", "服装破损")]
    [InlineData("[KeyMouseDrag]", "鼠标拖拽")]
    [InlineData("[key2ndlayer]", "第二层")]
    public void LocalizeSectionName_CoversTheFullyUntranslatedNames(string sectionName, string expected)
        => Assert.Equal(expected, KeyBindingLabelLocalizer.LocalizeSectionName(sectionName));

    // 中英夹杂的那批：分词能翻一半（Swap 切换），剩下的一半是这里补的
    [Theory]
    [InlineData("[KeyBody_Scale]", "身体缩放")]
    [InlineData("[KeyBody_Scale_Reset]", "身体缩放重置")]
    [InlineData("[KeyBody_Upper]", "身体上部")]
    [InlineData("[keyCycleAnim]", "循环动画")]
    [InlineData("[keyCycleSpeed]", "循环速度")]
    [InlineData("[KeyEffectStat]", "特效状态")]
    [InlineData("[KeyHoldShape]", "长按形状")]
    [InlineData("[KeyResetPosition]", "重置位置")]
    [InlineData("[KeyMenu.ResetPosition]", "菜单重置位置")]
    [InlineData("[KeyMermaidTail]", "人鱼尾巴")]
    [InlineData("[KeySwapAni]", "切换动画")]
    [InlineData("[KeySwapArmcloth]", "切换手臂布料")]
    [InlineData("[KeySwapArmlet]", "切换臂环")]
    [InlineData("[KeySwapBeidai]", "切换背带")]
    [InlineData("[KeySwapBracel]", "切换手镯")]
    [InlineData("[KeySwapBracer]", "切换护腕")]
    [InlineData("[KeySwapBrassards]", "切换臂章")]
    [InlineData("[KeySwapCasque]", "切换头盔")]
    [InlineData("[KeySwapCeinture]", "切换腰带")]
    [InlineData("[KeySwapCouronneEtCornes]", "切换王冠与角")] // 法语 "couronne et cornes"
    [InlineData("[KeySwapEpaule]", "切换肩部")]
    [InlineData("[KeySwapEpaules]", "切换肩部")]
    [InlineData("[KeySwapFins]", "切换鳍")]
    [InlineData("[KeySwapHuwan]", "切换护腕")]
    [InlineData("[KeySwapRing]", "切换戒指")]
    [InlineData("[KeySwapShangban]", "切换上半")]
    [InlineData("[KeySwapTete]", "切换头部")]
    [InlineData("[KeySwapThorns]", "切换荆棘")]
    [InlineData("[KeySwapXiaban]", "切换下半")]
    [InlineData("[KeySwapXiongdai]", "切换胸带")]
    [InlineData("[KeySwapYinmao]", "切换阴毛")]
    [InlineData("[KeySwapCum]", "切换精液")]
    [InlineData("[KeyToggleMap]", "开/关地图")]
    public void LocalizeSectionName_FinishesTheHalfTranslatedNames(string sectionName, string expected)
        => Assert.Equal(expected, KeyBindingLabelLocalizer.LocalizeSectionName(sectionName));

    // ⚠️ 这几类**故意不翻**：段名是 mod 作者自定的不透明标识符，没有可靠词义，翻错比留英文更糟。
    // 哪天能确认含义了，把它们移到上面两组去。
    [Theory]
    [InlineData("[KeyDirectA0]")] // DirectX 绘制调用名，不是英文单词
    [InlineData("[KeySK_A]")]     // 作者的变体编号
    [InlineData("[KeyTS1]")]
    [InlineData("[KeyFa]")]       // 两个字母的拼音：发 / 法 / 乏 分不出来
    [InlineData("[KeyTu]")]       // 腿 / 兔 / 图
    [InlineData("[KeyYan]")]      // 眼 / 烟 / 颜
    [InlineData("[Keyzhuang]")]   // 装 / 妆
    [InlineData("[Keyyinwen]")]   // 淫纹 / 印纹
    [InlineData("[Keypangci]")]
    [InlineData("[Keyfweapon]")]
    [InlineData("[key1]")]        // 纯数字段名 → 显示 "1"，本来就不是英文
    [InlineData("[key10]")]
    public void LocalizeSectionName_LeavesOpaqueAuthorIdsUntouched(string sectionName)
    {
        // 与 LocalizeSectionName 同样的剥壳：去方括号 + 去 "Key" 前缀
        var expected = sectionName.Trim().Trim('[', ']');
        if (expected.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
            expected = expected[3..];

        Assert.Equal(expected, KeyBindingLabelLocalizer.LocalizeSectionName(sectionName));
    }

    // 变体字母同理：翻译它们会把「这是同一功能的第几个变体」这条信息抹掉
    [Theory]
    [InlineData("[KeySwapA]", "切换A")]
    [InlineData("[KeySwapE]", "切换E")]
    [InlineData("[KeySwapEx]", "切换Ex")]
    [InlineData("[KeySwapR2]", "切换R2")]
    [InlineData("[KeySwapFPS]", "切换FPS")]
    [InlineData("[KeySwapJiao]", "切换Jiao")] // 脚 / 角 / 交 分不出来
    public void LocalizeSectionName_KeepsAuthorVariantLetters(string sectionName, string expected)
        => Assert.Equal(expected, KeyBindingLabelLocalizer.LocalizeSectionName(sectionName));

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