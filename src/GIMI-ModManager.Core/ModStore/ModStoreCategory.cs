using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;

namespace GIMI_ModManager.Core.ModStore;

/// <summary>
/// 商店里的一条分类。
///
/// 鸣潮板块的分类**就是角色名**（实测 <c>Hsin</c> / <c>Changli</c> / <c>UI</c> / <c>Other/Misc</c> …），
/// 所以「按角色筛选」直接落在这个字段上，不需要另建一张角色表。
/// </summary>
public sealed class ModStoreCategory
{
    private ModStoreCategory(int? id, string name, Uri? iconUrl)
    {
        Id = id;
        Name = name;
        IconUrl = iconUrl;
    }

    /// <summary>
    /// 分类 Id。<b>可能为 null</b> —— 列表接口的 <c>_aRootCategory</c> 实测**没有 <c>_idRow</c>**，
    /// 只能从 <c>_sProfileUrl</c> 末段抠（<c>…/mods/cats/29496</c>）；抠不出来就是 null。
    /// 筛选用 <see cref="Name"/>，别依赖这个字段。
    /// </summary>
    public int? Id { get; }

    public string Name { get; }

    /// <summary>
    /// 分类图标（实测在 <c>images.gamebanana.com/img/ico/ModCategory/*.png</c>）。
    /// 校验走 <see cref="GameBananaMediaUrls.TryCreateImageUrl(string?)"/>：空串（部分记录的分类图标就是空串）
    /// 与非本图床的地址都按「没有」处理。
    /// </summary>
    public Uri? IconUrl { get; }

    /// <summary>名称为空（有些提交确实没有分类）时返回 null。</summary>
    public static ModStoreCategory? FromApi(ApiSubfeedCategory? category)
    {
        if (category is null || string.IsNullOrWhiteSpace(category.Name))
            return null;

        return new ModStoreCategory(ParseId(category.ProfileUrl), category.Name.Trim(),
            GameBananaMediaUrls.TryCreateImageUrl(category.IconUrl));
    }

    /// <summary>从 <c>…/mods/cats/29496</c> 这类地址取末段整数。取不到返回 null（不抛）。</summary>
    private static int? ParseId(string? profileUrl)
    {
        if (string.IsNullOrWhiteSpace(profileUrl) ||
            !Uri.TryCreate(profileUrl, UriKind.Absolute, out var url))
            return null;

        var last = url.Segments.Length > 0 ? url.Segments[^1].Trim('/') : string.Empty;

        return int.TryParse(last, out var id) && id > 0 ? id : null;
    }
}