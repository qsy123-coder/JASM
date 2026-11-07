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

    /// <summary>按键类型，如 <c>cycle</c> / <c>hold</c>；空表示 ini 没写。</summary>
    public string KeyType { get; init; } = string.Empty;

    /// <summary>按键的显示文本，如 <c>↓</c>、<c>Num5</c>。</summary>
    public string KeyDisplay { get; init; } = string.Empty;

    /// <summary>段落里的 <c>condition =</c> 原文；空表示无条件。</summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>菜单里显示的名字：注释优先，否则由段落名推导。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// 这一行能否由菜单直接改值。需要：有变量名、至少两个候选值、按键类型是循环/开关类。
    /// </summary>
    public bool IsClickable =>
        !string.IsNullOrWhiteSpace(VariableName)
        && Values.Count >= 2
        && IsCycleLikeType(KeyType);

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
