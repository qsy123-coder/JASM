using System.Text;

namespace GIMI_ModManager.Core.Entities.Mods.Helpers;

/// <summary>
/// 把 3dmigoto mod 的 ini 段落名翻译成中文标签，供「按键映射」面板显示。
/// 段落名是 mod 作者自定的（<c>[KeyShoes]</c> → <c>Shoes</c>、<c>[Keyxiezi]</c> → <c>xiezi</c>），
/// 没法走 resw 资源，所以词表内置在代码里 —— 与同目录 <see cref="ModIniKeyBindingParser"/> 的
/// MapKeyName / SectionToAction 同风格。
///
/// 设计原则：<b>只在能确定含义时才翻译</b>。任何识别不出来的标签一律原样返回，
/// 宁可显示英文，也不要把 mod 作者自定义的段名翻错。
/// 本类只影响显示，不会改动 ini 文件。
/// </summary>
public static class KeyBindingLabelLocalizer
{
    /// <summary>
    /// 翻译一个已经去掉段落格式的标签（如 "Shoes"、"SwapHair"、"xiezi"）。
    /// 解析顺序：整串精确匹配 → 分词逐词翻译 → 拼音组合切分 → 原样返回。
    /// </summary>
    public static string Localize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var trimmed = label.Trim();

        // 已经是中文（作者自己写了中文段名，如 [Key鞋子]）→ 不动
        if (ContainsCjk(trimmed))
            return trimmed;

        // 1. 整串精确匹配：分词会拆错的少数特例，以及需要消歧的复合词
        if (ExactLabels.TryGetValue(Normalize(trimmed), out var exact))
            return exact;

        // 2. 分词后逐词翻译：SwapHair → 切换头发、Top1 → 上衣1、ToggleCompatibilityMode → 开/关兼容模式
        var tokens = Tokenize(trimmed);
        var sb = new StringBuilder(trimmed.Length);
        var translatedCount = 0;

        foreach (var token in tokens)
        {
            if (Words.TryGetValue(token, out var english))
            {
                sb.Append(english);
                translatedCount++;
            }
            else if (PinyinWords.TryGetValue(token, out var pinyin))
            {
                sb.Append(pinyin);
                translatedCount++;
            }
            else
            {
                sb.Append(token); // 词表里没有 → 原样保留，与前后中文直接拼接（如 上衣1、手臂Thing）
            }
        }

        if (translatedCount > 0 && sb.Length > 0)
            return sb.ToString();

        // 3. 拼音组合词：作者把多个词连写（youguangsiwa → 亮光丝袜），整串查不到时按最长匹配切分
        if (TrySegmentPinyin(trimmed, out var segmented))
            return segmented;

        // 4. 兜底：原样返回
        return trimmed;
    }

    /// <summary>
    /// 翻译一个 ini 段落名，会先剥掉方括号和 <c>Key</c> 前缀。
    /// <c>[KeyShoes]</c> → <c>鞋子</c>、<c>[KeySwapTextures]</c> → <c>切换贴图</c>。
    /// </summary>
    public static string LocalizeSectionName(string? sectionName)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
            return string.Empty;

        var name = sectionName.Trim().Trim('[', ']').Trim();

        // 剥 "Key" 前缀：[KeyShoes] → Shoes、[Key enable_mods] → enable_mods
        if (name.StartsWith("Key", StringComparison.OrdinalIgnoreCase))
        {
            name = name[3..];
            if (name.StartsWith(' '))
                name = name[1..];
        }

        return Localize(name);
    }

    // ── 归一化 / 分词 ──────────────────────────────────

    /// <summary>去掉所有非字母数字字符并转小写，"Swap Hair" / "swap_hair" / "SwapHair" 归一成同一个 key</summary>
    private static string Normalize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static bool ContainsCjk(string value)
    {
        foreach (var c in value)
        {
            if (c is >= '一' and <= '鿿')
                return true;
        }
        return false;
    }

    private static readonly char[] Separators = ['_', '-', ' ', '.', '/', '\\', '(', ')', '[', ']', '+'];

    /// <summary>按分隔符与驼峰/缩写/数字边界拆词：SwapHair → Swap|Hair、FXLiquid → FX|Liquid、Top1 → Top|1</summary>
    private static List<string> Tokenize(string label)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < label.Length; i++)
        {
            var c = label[i];

            if (Separators.Contains(c))
            {
                Flush(tokens, current);
                continue;
            }

            if (current.Length > 0 && IsWordBoundary(label, i))
                Flush(tokens, current);

            current.Append(c);
        }

        Flush(tokens, current);
        return tokens;
    }

    private static void Flush(List<string> tokens, StringBuilder current)
    {
        if (current.Length > 0)
            tokens.Add(current.ToString());
        current.Clear();
    }

    private static bool IsWordBoundary(string value, int index)
    {
        var previous = value[index - 1];
        var current = value[index];

        // 字母 ↔ 数字：Top1 → Top|1
        if (char.IsDigit(current) != char.IsDigit(previous))
            return true;

        // 只有遇到大写才可能是驼峰边界
        if (!char.IsUpper(current))
            return false;

        // aA / 1A → 断开
        if (char.IsLower(previous) || char.IsDigit(previous))
            return true;

        // 连续大写看作缩写，靠后面是否跟着小写来决定在哪断：FXLiquid → FX|Liquid
        return index + 1 < value.Length && char.IsLower(value[index + 1]);
    }

    // ── 拼音组合词切分 ─────────────────────────────────

    /// <summary>
    /// 对纯小写的拼音连写做贪心最长匹配切分。要求切分完整、每段都在拼音表内、且至少两段，
    /// 否则放弃（返回 false）由调用方原样输出，避免把英文单词切碎。
    /// </summary>
    private static bool TrySegmentPinyin(string value, out string result)
    {
        result = string.Empty;

        // 单个拼音词（xiezi）在第 2 步就命中了；走到这里只可能是连写，长度至少两段
        const int minSegmentLength = 3;
        if (value.Length < minSegmentLength * 2)
            return false;

        foreach (var c in value)
        {
            if (c is < 'a' or > 'z')
                return false;
        }

        var segments = new List<string>();
        var offset = 0;

        while (offset < value.Length)
        {
            var matchedLength = 0;
            var matched = string.Empty;

            // 最长优先：从剩余整段往下试，第一个命中的就是最长匹配
            for (var length = value.Length - offset; length >= minSegmentLength; length--)
            {
                if (!PinyinWords.TryGetValue(value.Substring(offset, length), out var chinese))
                    continue;

                matchedLength = length;
                matched = chinese;
                break;
            }

            if (matchedLength == 0)
                return false; // 切不动 → 整体放弃

            segments.Add(matched);
            offset += matchedLength;
        }

        if (segments.Count < 2)
            return false;

        result = string.Concat(segments);
        return true;
    }

    // ── 词表 ───────────────────────────────────────────

    /// <summary>
    /// 整串精确匹配表。key 必须是 <see cref="Normalize"/> 之后的形式（全小写、无分隔符）。
    /// 只在「分词会拆错」或「需要消歧」时才需要往这里加。
    /// </summary>
    private static readonly Dictionary<string, string> ExactLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["headacc"] = "头饰",
        ["hairacc"] = "发饰",
        ["facefx"] = "脸部特效",
        ["bodytex"] = "身体贴图",
        ["armthing"] = "手臂配件",
        ["hideuid"] = "隐藏UID",
        ["boobsize"] = "胸部尺寸",
    };

    /// <summary>英文词表（逐词匹配，大小写不敏感）</summary>
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── 动作 ──
        ["swap"] = "切换",
        ["switch"] = "切换",
        ["toggle"] = "开/关",
        ["hold"] = "长按",
        ["click"] = "点击",
        ["press"] = "按下",
        ["cycle"] = "循环",
        ["enable"] = "启用",
        ["enabled"] = "启用",
        ["disable"] = "禁用",
        ["disabled"] = "禁用",
        ["show"] = "显示",
        ["hide"] = "隐藏",
        ["open"] = "打开",
        ["close"] = "关闭",
        ["next"] = "下一个",
        ["prev"] = "上一个",
        ["previous"] = "上一个",
        ["add"] = "添加",
        ["remove"] = "移除",
        ["reset"] = "重置",
        ["random"] = "随机",
        ["auto"] = "自动",
        ["default"] = "默认",
        ["mode"] = "模式",
        ["variant"] = "变体",
        ["variations"] = "变体",
        ["size"] = "尺寸",
        ["color"] = "颜色",
        ["colour"] = "颜色",
        ["style"] = "样式",
        ["level"] = "等级",
        ["fix"] = "修复",
        ["patch"] = "补丁",
        ["mod"] = "模组",
        ["mods"] = "模组",
        ["key"] = "按键",
        ["keys"] = "按键",
        ["help"] = "帮助",
        ["info"] = "信息",
        ["guide"] = "指南",
        ["tips"] = "提示",
        ["notice"] = "提示",
        ["notification"] = "通知",
        ["first"] = "首次",
        ["run"] = "运行",
        ["test"] = "测试",
        ["custom"] = "自定义",
        ["compatibility"] = "兼容",
        ["hunting"] = "狩猎",
        ["user"] = "用户",
        ["full"] = "完整",
        ["half"] = "半",
        ["all"] = "全部",
        ["none"] = "无",
        ["on"] = "开",
        ["off"] = "关",
        ["up"] = "上",
        ["down"] = "下",
        ["left"] = "左",
        ["right"] = "右",
        ["top"] = "上衣",
        ["bottom"] = "下装",
        ["back"] = "背面",
        ["front"] = "正面",
        ["part"] = "部件",
        ["thing"] = "配件",

        // ── 身体部位 ──
        ["body"] = "身体",
        ["skin"] = "皮肤",
        ["face"] = "脸部",
        ["head"] = "头部",
        ["hair"] = "头发",
        ["hairstyle"] = "发型",
        ["ponytail"] = "马尾",
        ["braid"] = "辫子",
        ["bangs"] = "刘海",
        ["eye"] = "眼睛",
        ["eyes"] = "眼睛",
        ["eyebrow"] = "眉毛",
        ["mouth"] = "嘴部",
        ["lip"] = "嘴唇",
        ["makeup"] = "妆容",
        ["chest"] = "胸部",
        ["boob"] = "胸部",
        ["boobs"] = "胸部",
        ["breast"] = "胸部",
        ["thicc"] = "丰满",
        ["pubes"] = "体毛",
        ["waist"] = "腰部",
        ["hip"] = "臀部",
        ["ear"] = "耳朵",
        ["ears"] = "耳朵",
        ["tail"] = "尾巴",
        ["horn"] = "角",
        ["horns"] = "角",
        ["wing"] = "翅膀",
        ["wings"] = "翅膀",
        ["halo"] = "光环",
        ["arm"] = "手臂",
        ["arms"] = "手臂",
        ["hand"] = "手",
        ["hands"] = "手",
        ["finger"] = "手指",
        ["nails"] = "指甲",
        ["leg"] = "腿",
        ["legs"] = "腿",
        ["thigh"] = "大腿",
        ["foot"] = "脚",
        ["tattoo"] = "纹身",
        ["earring"] = "耳环",
        ["earrings"] = "耳环",
        ["necklace"] = "项链",
        ["choker"] = "颈饰",
        ["bracelet"] = "手镯",
        ["bracelets"] = "手镯",
        ["armband"] = "臂环",
        ["anklet"] = "脚链",
        ["anklets"] = "脚链",

        // ── 服装 ──
        ["hat"] = "帽子",
        ["cap"] = "帽子",
        ["glasses"] = "眼镜",
        ["mask"] = "面罩",
        ["veil"] = "面纱",
        ["scarf"] = "围巾",
        ["collar"] = "衣领",
        ["tie"] = "领带",
        ["bowtie"] = "领结",
        ["ribbon"] = "缎带",
        ["cape"] = "披风",
        ["cloak"] = "斗篷",
        ["dress"] = "连衣裙",
        ["gown"] = "长裙",
        ["skirt"] = "短裙",
        ["shirt"] = "衬衫",
        ["jacket"] = "外套",
        ["coat"] = "大衣",
        ["suit"] = "套装",
        ["sweater"] = "毛衣",
        ["hoodie"] = "卫衣",
        ["uniform"] = "制服",
        ["apron"] = "围裙",
        ["kimono"] = "和服",
        ["hanfu"] = "汉服",
        ["qipao"] = "旗袍",
        ["leotard"] = "连体衣",
        ["swimsuit"] = "泳衣",
        ["bikini"] = "比基尼",
        ["lingerie"] = "内衣",
        ["underwear"] = "内衣",
        ["bra"] = "胸罩",
        ["panty"] = "内裤",
        ["panties"] = "内裤",
        ["pants"] = "裤子",
        ["shorts"] = "短裤",
        ["legging"] = "打底裤",
        ["leggings"] = "打底裤",
        ["sleeve"] = "袖子",
        ["sleeves"] = "袖子",
        ["cuff"] = "袖口",
        ["cuffs"] = "袖口",
        ["glove"] = "手套",
        ["gloves"] = "手套",
        ["strap"] = "绑带",
        ["straps"] = "绑带",
        ["belt"] = "腰带",
        ["shoes"] = "鞋子",
        ["boots"] = "靴子",
        ["heel"] = "高跟鞋",
        ["heels"] = "高跟鞋",
        ["sock"] = "袜子",
        ["socks"] = "袜子",
        ["stocking"] = "丝袜",
        ["stockings"] = "丝袜",
        ["pantyhose"] = "连裤袜",
        ["garter"] = "吊带袜",
        ["accessory"] = "配饰",
        ["accessories"] = "配饰",
        ["acc"] = "配饰",
        ["prop"] = "道具",
        ["props"] = "道具",
        ["weapon"] = "武器",
        ["sword"] = "剑",
        ["bow"] = "弓",
        ["catalyst"] = "法器",
        ["flower"] = "花",
        ["lolipop"] = "棒棒糖",
        ["plug"] = "塞子",

        // ── 材质 / 贴图 / 特效 ──
        ["tex"] = "贴图",
        ["texture"] = "贴图",
        ["textures"] = "贴图",
        ["material"] = "材质",
        ["shader"] = "着色器",
        ["fx"] = "特效",
        ["effect"] = "特效",
        ["particle"] = "粒子",
        ["glow"] = "发光",
        ["shine"] = "光泽",
        ["specular"] = "高光",
        ["wet"] = "湿润",
        ["liquid"] = "液体",
        ["water"] = "水",
        ["snow"] = "雪",
        ["dust"] = "灰尘",
        ["trail"] = "拖尾",
        ["censor"] = "遮挡",
        ["watermark"] = "水印",
        ["model"] = "模型",
        ["mesh"] = "网格",
        ["bone"] = "骨骼",
        ["physics"] = "物理",
        ["cloth"] = "布料",
        ["world"] = "世界",
        ["ui"] = "界面",
        ["hud"] = "界面",
        ["bg"] = "背景",
        ["screen"] = "屏幕",
        ["light"] = "光照",
        ["shadow"] = "阴影",
        ["fog"] = "雾",
        ["alpha"] = "透明度",
    };

    /// <summary>
    /// 拼音词表。只收录 ≥ 3 个字母的词，避免 you / xia / shang 这类短音节误伤英文。
    /// 既用于逐词匹配（[Keyxiezi] → 鞋子），也用于 <see cref="TrySegmentPinyin"/> 的组合切分。
    /// </summary>
    private static readonly Dictionary<string, string> PinyinWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xiezi"] = "鞋子",
        ["toufa"] = "头发",
        ["faxing"] = "发型",
        ["liuhai"] = "刘海",
        ["mawei"] = "马尾",
        ["yifu"] = "衣服",
        ["shangyi"] = "上衣",
        ["waitao"] = "外套",
        ["chenshan"] = "衬衫",
        ["qunzi"] = "裙子",
        ["qunbai"] = "裙摆",
        ["lianyiqun"] = "连衣裙",
        ["xiuzi"] = "袖子",
        ["kuzi"] = "裤子",
        ["duanku"] = "短裤",
        ["maozi"] = "帽子",
        ["weijin"] = "围巾",
        ["shoutao"] = "手套",
        ["siwa"] = "丝袜",
        ["liankuwa"] = "连裤袜",
        ["neiyi"] = "内衣",
        ["neiku"] = "内裤",
        ["xiongzhao"] = "胸罩",
        ["yongyi"] = "泳衣",
        ["hefu"] = "和服",
        ["qipao"] = "旗袍",
        ["yanjing"] = "眼睛",
        ["meimao"] = "眉毛",
        ["zuichun"] = "嘴唇",
        ["biaoqing"] = "表情",
        ["zhuangrong"] = "妆容",
        ["kouhong"] = "口红",
        ["bozi"] = "脖子",
        ["xiongbu"] = "胸部",
        ["xiongxing"] = "胸型",
        ["yaobu"] = "腰部",
        ["tunbu"] = "臀部",
        ["fubu"] = "腹部",
        ["houbei"] = "后背",
        ["datui"] = "大腿",
        ["shoubu"] = "手部",
        ["zhijia"] = "指甲",
        ["erduo"] = "耳朵",
        ["weiba"] = "尾巴",
        ["chibang"] = "翅膀",
        ["guanghuan"] = "光环",
        ["toushi"] = "头饰",
        ["fashi"] = "发饰",
        ["erhuan"] = "耳环",
        ["xianglian"] = "项链",
        ["xiangquan"] = "项圈",
        ["shouzhuo"] = "手镯",
        ["jiaolian"] = "脚链",
        ["hudie"] = "蝴蝶",
        ["jie"] = "结",
        ["hudiejie"] = "蝴蝶结",
        ["hua"] = "花",
        ["wenshen"] = "纹身",
        ["mianju"] = "面具",
        ["yanzhao"] = "眼罩",
        ["tietu"] = "贴图",
        ["cailiao"] = "材质",
        ["texiao"] = "特效",
        ["guangxiao"] = "光效",
        ["youguang"] = "亮光",
        ["liangguang"] = "亮光",
        ["faguang"] = "发光",
        ["liushui"] = "流水",
        ["shuihua"] = "水花",
        ["xuehua"] = "雪花",
        ["wupin"] = "物品",
        ["moxing"] = "模型",
        ["yanse"] = "颜色",
        ["touming"] = "透明",
        ["jiaohuan"] = "交换",
        ["qiehuan"] = "切换",
        ["xianshi"] = "显示",
        ["yincang"] = "隐藏",
        ["quanbu"] = "全部",
        ["moren"] = "默认",
        ["shuaxin"] = "刷新",
        ["xiufu"] = "修复",
        ["kaiguan"] = "开关",
        ["moban"] = "模板",
        ["shenti"] = "身体",
        ["pifu"] = "皮肤",
    };
}