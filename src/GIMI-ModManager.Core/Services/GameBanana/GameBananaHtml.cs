using System.Net;
using System.Text.RegularExpressions;

namespace GIMI_ModManager.Core.Services.GameBanana;

/// <summary>
/// 把 GameBanana 的 **HTML 字段**洗成能直接塞进 <c>TextBlock</c> 的纯文本。
///
/// 实测详情页有三处是 HTML，不是纯文本：<c>_sText</c>（正文，带
/// <c>&lt;h1&gt;</c> / <c>&lt;br&gt;</c> / <c>&lt;b&gt;</c> / <c>&lt;a href&gt;</c>）、
/// 嵌着 <c>&lt;br /&gt;</c> 的 <c>_sDescription</c>、以及整段带
/// <c>&lt;img&gt;</c> 的 <c>_sLicense</c>。
/// 直接显示就是满屏尖括号 —— 而且这里的文本是**用户提交的内容**，
/// 往后要渲染富文本时更该从这一处统一过一遍。
///
/// 刻意**不做**的事：不保留链接（<c>&lt;a&gt;</c> 只留可见文字，不生成超链接）、
/// 不解析 <c>&lt;img&gt;</c>、除 HTML 实体外不做任何转义还原。首版详情只要求「读得懂」。
/// </summary>
public static partial class GameBananaHtml
{
    /// <summary>
    /// 换行语义的标签 → 真正的换行。必须在剥标签**之前**做，否则
    /// <c>a&lt;br&gt;b</c> 会变成 <c>ab</c>（两行粘成一行，正文读起来会串味）。
    /// </summary>
    [GeneratedRegex(@"<br\s*/?>|</(?:p|div|li|tr|h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTags();

    /// <summary>剩下的标签一律丢掉。要求标签里有内容，免得把「3 &lt; 5」这种正文吃成两半。</summary>
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\f\v]{2,}")]
    private static partial Regex RunsOfSpaces();

    /// <summary>连着的空行压成一行空行 —— 原 HTML 里换行常常是排版用的一串 <c>&lt;br&gt;</c>。</summary>
    [GeneratedRegex(@"\n[ \t]*(?:\n[ \t]*){2,}")]
    private static partial Regex RunsOfBlankLines();

    /// <summary>
    /// HTML → 纯文本。空/空白输入按 <c>null</c> 处理（「没有这段内容」比空串更好判）。
    /// </summary>
    public static string? ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var text = BreakTags().Replace(html, "\n");
        text = Tags().Replace(text, string.Empty);

        // 实体还原放在剥标签之后：&lt;b&gt; 这种「本来就想显示尖括号」的写法
        // 还原出来的是文字，不该再被当成标签剥掉。
        text = WebUtility.HtmlDecode(text);

        // &nbsp; 还原出来的是 U+00A0，在 TextBlock 里长得像空格但不算空格 —— 换成普通空格。
        text = text.Replace('\u00A0', ' ');
        text = RunsOfSpaces().Replace(text, " ");
        text = RunsOfBlankLines().Replace(text, "\n\n");

        var trimmed = text.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}