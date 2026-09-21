using System.Text;
using System.Text.RegularExpressions;

namespace GIMI_ModManager.Core.ModMarket;

/// <summary>
/// 复刻 PostgREST 的 <c>title=ilike.*q*</c> 语义。
///
/// 关键点：这是 **LIKE 模式**，不是字面量子串。PostgREST 把 <c>*</c> 换成 <c>%</c>，而
/// <c>%</c> / <c>_</c> / <c>\</c> 会原样交给 Postgres 当元字符。实测库里 30 个标题含下划线，
/// 搜 "a_b" 在线上命中 17 行，而用 <c>Title.Contains("a_b")</c> 命中 0 行 —— 所以本地必须按 LIKE 规则翻译。
///
/// 翻译规则（与 Postgres LIKE 一致）：
/// <list type="bullet">
///   <item><c>*</c> 或 <c>%</c> → 任意长度</item>
///   <item><c>_</c> → 任意单字符</item>
///   <item><c>\x</c> → 字面量 x</item>
///   <item>其余 → 字面量</item>
/// </list>
/// 匹配是"包含"语义（线上把用户输入夹在 <c>*…*</c> 之间），故不锚定。
/// </summary>
public sealed class MarketLikePattern
{
    private static readonly RegexOptions PatternOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline |
        RegexOptions.NonBacktracking;

    /// <summary>空模式按 LIKE 的 <c>%%</c> 处理，即匹配一切。</summary>
    private static readonly MarketLikePattern MatchEverything = new(new Regex(string.Empty, PatternOptions));

    private readonly Regex _regex;

    private MarketLikePattern(Regex regex)
    {
        _regex = regex;
    }

    public static MarketLikePattern Create(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return MatchEverything;
        return new MarketLikePattern(new Regex(Translate(pattern), PatternOptions));
    }

    public bool IsMatch(string? value) => value is not null && _regex.IsMatch(value);

    /// <summary>
    /// 把 LIKE 模式翻译成正则表达式。
    /// 用 NonBacktracking 保证线性时间 —— 用户输入的搜索词不该有机会造成灾难性回溯。
    /// 结尾孤立的反斜杠按字面量处理（Postgres 对这种情况会直接报错，这里取宽松）。
    /// </summary>
    private static string Translate(string pattern)
    {
        var builder = new StringBuilder(pattern.Length * 2);

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '*' or '%':
                    builder.Append(".*");
                    break;
                case '_':
                    builder.Append('.');
                    break;
                case '\\' when i + 1 < pattern.Length:
                    builder.Append(Regex.Escape(pattern[++i].ToString()));
                    break;
                default:
                    builder.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }
}
