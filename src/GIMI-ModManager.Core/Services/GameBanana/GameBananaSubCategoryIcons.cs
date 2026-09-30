using System.Text;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;

namespace GIMI_ModManager.Core.Services.GameBanana;

/// <summary>
/// 从一页搜索记录里挑出「某个角色」的分类图标（商店侧栏的角色图标）。
///
/// 为什么要这么绕：GameBanana **没有**「列出板块子分类」的端点（实测），所以角色表用的是本地
/// <c>characters.json</c> 的内部名；图标只能从搜索结果里**顺带**抠 —— 好在那次搜索每个角色本来就要打
/// （侧栏补计数走的就是它），拿图标等于零额外请求。
///
/// 名字对不齐是常态：本地内部名 <c>YangyangXuanling</c> 对应 GameBanana 的
/// <c>Yangyang: Xuanling</c>，所以比对前先归一化（只留字母数字 + 转小写）。
/// 对不齐就是 null —— 界面退回字形图标，不猜一个别的角色的图标顶上。
/// </summary>
public static class GameBananaSubCategoryIcons
{
    /// <summary>
    /// 在 <paramref name="records"/> 里找子分类名与 <paramref name="query"/> 相符的那条，返回它的图标。
    /// 先按归一化后**相等**找，一轮不中再放宽到**包含**（GB 的分类名常带后缀，如 <c>… (Skin)</c>）。
    /// </summary>
    public static Uri? TryPick(IReadOnlyList<ApiSubfeedRecord>? records, string? query)
    {
        if (records is null || records.Count == 0 || string.IsNullOrWhiteSpace(query))
            return null;

        var wanted = Normalize(query);
        if (wanted.Length == 0)
            return null;

        return Pick(records, wanted, exactOnly: true) ?? Pick(records, wanted, exactOnly: false);
    }

    private static Uri? Pick(IReadOnlyList<ApiSubfeedRecord> records, string wanted, bool exactOnly)
    {
        foreach (var record in records)
        {
            var name = Normalize(record.SubCategory?.Name);
            if (name.Length == 0)
                continue;

            var matched = exactOnly
                ? name.Equals(wanted, StringComparison.Ordinal)
                : name.Contains(wanted, StringComparison.Ordinal);

            if (!matched)
                continue;

            // 图标本身也要过校验：实测有记录的子分类图标是空串，那种记录不能算「找到了」。
            if (GameBananaMediaUrls.TryCreateImageUrl(record.SubCategory?.IconUrl) is { } icon)
                return icon;
        }

        return null;
    }

    /// <summary>
    /// 归一化：只留字母与数字，统一小写。<c>Yangyang: Xuanling</c> → <c>yangyangxuanling</c>，
    /// 与本地内部名 <c>YangyangXuanling</c> 就此对齐。
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}