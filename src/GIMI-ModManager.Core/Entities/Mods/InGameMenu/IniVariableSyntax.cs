namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>
/// 判断一个 ini 变量引用能不能安全地写进 3DMigoto 表达式。
///
/// 表达式里引用别的命名空间变量只能写成 <c>$\Namespace\var</c>；这个 token 由 3DMigoto
/// 的表达式解析器按固定字符集切分（S3DMIni 规范：字母、数字，以及
/// <c>% , . : ? @ [ \ ] ^ _ ` { } ~</c>）。<b>连字符不在其中</b>，而作者很喜欢
/// <c>namespace = Mods\Xin-HunSha</c> 这种带 <c>-</c> 的命名空间：写进我们的菜单后会被
/// 解析成「<c>$\Mods\Xin</c> 减去 <c>HunSha\object_detected</c>」，整行报
/// <c>Syntax Error</c> / <c>Unrecognised entry</c>（实机日志已证实）。
///
/// 真正的坑在于 <c>if &lt;非法表达式&gt;</c> 的后果：3DMigoto 不会把它当作「条件为假」，
/// 而是丢掉这层 if、让里面的语句<b>无条件执行</b>。门控里紧跟着的 <c>present = 1</c>
/// 于是每帧都跑 —— 菜单永远「在场」，按 F7 会把这份菜单一起打开，看起来就是
/// 「开绯雪的菜单，把别的角色的菜单也带出来了」。
///
/// 所以只要命名空间 / 变量名里有非法字符，就绝不能把它写进表达式：门控退回纹理 hash，
/// 行则视为菜单无法驱动（<see cref="ModIniToggleRow.IsClickable"/> 为 false）。
/// </summary>
public static class IniVariableSyntax
{
    /// <summary>
    /// 3DMigoto 表达式里变量 token 允许的符号（字母、数字之外的部分）。
    /// 取自 S3DMIni 规范的变量名定义；反斜杠是命名空间分隔符，所以必须在里面。
    /// </summary>
    private const string AllowedSymbols = "%.,:?@[]\\^_`{}~";

    /// <summary>命名空间 / 变量名本身能不能出现在表达式里。</summary>
    public static bool IsExpressibleName(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var c in name)
        {
            if (char.IsAsciiLetterOrDigit(c))
                continue;

            if (AllowedSymbols.Contains(c))
                continue;

            // 连字符、空格、中文等都会让 3DMigoto 把 token 拆错
            return false;
        }

        return true;
    }

    /// <summary>
    /// <c>$\Namespace\var</c> / <c>$var</c> 这种完整引用能不能被表达式解析器识别。
    /// </summary>
    public static bool IsExpressibleReference(string? qualifiedVariable)
    {
        if (string.IsNullOrWhiteSpace(qualifiedVariable))
            return false;

        var name = qualifiedVariable.Trim();
        if (name[0] == '$')
            name = name[1..];

        return IsExpressibleName(name);
    }
}