namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>
/// 一个 Mod ini 里的 <c>[Key*]</c> 段落，映射成游戏内菜单的一行。
///
/// 只描述「读到了什么」，不做任何猜测：
/// <see cref="Values"/> 为空或只有一个值时，这一行在菜单里只能显示、不能点击
/// （3DMigoto 的循环至少需要两个候选值）。
/// </summary>
public sealed record ModIniToggleRow
{
    /// <summary>原始段落名，含方括号，如 <c>[KeyShoes]</c>。</summary>
    public required string SectionName { get; init; }

    /// <summary>相对 Mod 根目录的 ini 路径，用于日志与失效检测。</summary>
    public required string IniRelativePath { get; init; }

    /// <summary>声明该段落的 ini 文件的命名空间；空字符串表示全局命名空间。</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>被循环的变量名（不含 <c>$</c>），如 <c>xiezi</c>。</summary>
    public string VariableName { get; init; } = string.Empty;

    /// <summary>循环候选值，按 ini 里出现的顺序原样保留（如 <c>4,8,9</c>）。</summary>
    public IReadOnlyList<string> Values { get; init; } = [];

    /// <summary>
    /// Mod ini 里 <c>global [persist] $var = …</c> 声明的初始值，用于菜单的「恢复默认」。
    /// 空表示没有可用的默认值（这一行不参与重置）。
    /// </summary>
    public string DefaultValue { get; init; } = string.Empty;

    /// <summary>按键类型，如 <c>cycle</c> / <c>hold</c>；空表示 ini 没写。</summary>
    public string KeyType { get; init; } = string.Empty;

    /// <summary>按键的显示文本，如 <c>↓</c>、<c>Num5</c>。</summary>
    public string KeyDisplay { get; init; } = string.Empty;

    /// <summary>段落里的 <c>condition =</c> 原文；空表示无条件。</summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>菜单里显示的名字：注释优先，否则由段落名推导（已本地化）。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>本地化之前的原始标签（注释/段落名），写进 manifest 便于排查。</summary>
    public string RawLabel { get; init; } = string.Empty;

    /// <summary>
    /// 菜单行的稳定标识（<c>ini 相对路径|段落名</c>），用于保存「行顺序 / 隐藏」偏好。
    /// 变量名可能被作者改动，段落名 + 文件路径更稳定。
    /// </summary>
    public string Key => IniRelativePath + "|" + SectionName;

    /// <summary>
    /// 特殊行：拖动滑块（油光强度）。滑块不走 <see cref="Values"/>，
    /// 由菜单按光标在轨道上的横坐标实时写变量。
    /// </summary>
    public bool IsSlider { get; init; }

    /// <summary>
    /// 滑块最大值（默认 1.0）。油光强度允许超过 1，菜单轨道按这个上限归一化。
    /// 非正数按 1 处理。
    /// </summary>
    public double SliderMax { get; init; } = 1.0;

    /// <summary>
    /// 循环值的显示文本（可选），与 <see cref="Values"/> 一一对应。
    /// 空 / 数量不足时退回到 <c>1/6</c> 这种序号显示。
    /// </summary>
    public IReadOnlyList<string> ValueLabels { get; init; } = [];

    /// <summary>
    /// 这一行在 ini 里确实是一个「可改值的目标」：循环行需要变量名 + 至少两个候选值 +
    /// 循环/开关类型；滑块行只需要变量名。
    ///
    /// 只看 ini 里写了什么，不看菜单能不能访问到它 —— 判断后者用 <see cref="IsClickable"/>。
    /// </summary>
    public bool HasToggleTarget =>
        !string.IsNullOrWhiteSpace(VariableName)
        && (IsSlider || (Values.Count >= 2 && IsCycleLikeType(KeyType)));

    /// <summary>
    /// 变量引用能不能写进 3DMigoto 表达式。作者命名空间里的连字符（<c>Mods\Xin-HunSha</c>）
    /// 会让整个 <c>if</c> 变成语法错误，而 3DMigoto 对无法识别的 <c>if</c> 是「丢掉条件、
    /// 照常执行里面的语句」，门控会直接失效（详见 <see cref="IniVariableSyntax"/>）。
    /// </summary>
    public bool HasExpressibleVariable => IniVariableSyntax.IsExpressibleReference(QualifiedVariable);

    /// <summary>
    /// 这一行能否由菜单直接改值：既是可改值的目标，又能在表达式里安全引用到变量。
    /// 引用不成立时这一行只可能是「点了没反应、值永远显示最后一个」的假开关，
    /// 生成器会把它整行移除。
    /// </summary>
    public bool IsClickable => HasToggleTarget && HasExpressibleVariable;

    /// <summary>
    /// 跨命名空间引用变量时的完整写法：<c>$\Namespace\var</c>；无命名空间时为 <c>$var</c>。
    /// 这是 3DMigoto 在 `[Key*]` 之外读写别人变量的唯一可靠形式。
    /// </summary>
    public string QualifiedVariable =>
        string.IsNullOrEmpty(Namespace)
            ? "$" + VariableName
            : "$\\" + Namespace + "\\" + VariableName;

    /// <summary>3DMigoto 的 <c>type</c> 里，缺省也按循环理解（部分 [KeySwap] 不写 type）。</summary>
    private static bool IsCycleLikeType(string keyType)
    {
        if (string.IsNullOrWhiteSpace(keyType))
            return true;

        return keyType.Trim().ToLowerInvariant() is "cycle" or "toggle" or "switch";
    }
}