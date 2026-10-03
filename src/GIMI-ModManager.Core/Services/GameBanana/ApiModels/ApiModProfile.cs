using System.Text.Json.Serialization;

namespace GIMI_ModManager.Core.Services.GameBanana.ApiModels;

/// <summary>
/// <c>apiv11/Mod/{id}/ProfilePage</c>（mod 详情）。
///
/// 这个端点是**唯一**给出下载量、正文、内容分级的地方 —— 列表接口没有这些字段，
/// 所以卡片/详情不能共用一套模型（见 <c>ModStoreMod</c> 的注释）。
///
/// 四处反直觉的地方（均实测）：
/// <list type="bullet">
///   <item>这个端点**也**给文件（<c>_aFiles</c> / <c>_aArchivedFiles</c>），但不可作为依据：
///         隐藏的 mod（709792）<c>_aFiles</c> **整个键都不存在**、文件全在 <c>_aArchivedFiles</c>；
///         文件清单以 <c>Mod/{id}/DownloadPage</c> 为准，这里那份只当兜底（见
///         <c>ModStoreDetail.BuildFiles</c>）；</item>
///   <item>列表侧用来判成人内容的 <c>_bHasContentRatings</c> 在这里**整个键都不存在**，
///         详情要改用 <c>_aContentRatings</c>（缺失 = 无分级）；</item>
///   <item><c>_aCategory</c> 是**最具体**的那个分类：Skins 板块下的 mod 拿到的是角色子分类
///         （<c>Qingxiao</c>），而本身属于根分类的 mod（UI）拿到的就是根分类 ——
///         分不清这两者会把 UI 类 mod 的角色显示成「UI」，判别依据见 <see cref="SuperCategory"/>；</item>
///   <item><c>_sDescription</c> / <c>_sText</c> / <c>_sLicense</c> 都是 **HTML**，
///         不能直接丢进 TextBlock（见 <c>GameBananaHtml</c>）。</item>
/// </list>
/// </summary>
public class ApiModProfile
{
    [JsonPropertyName("_idRow")] public int ModId { get; init; } = -1;
    [JsonPropertyName("_sName")] public string? ModName { get; init; }

    [JsonPropertyName("_aSubmitter")] public ApiAuthor? Author { get; init; }
    [JsonPropertyName("_aPreviewMedia")] public ApiImagesRoot? PreviewMedia { get; init; }

    [JsonPropertyName("_sProfileUrl")] public string? ModPageUrl { get; init; }

    /// <summary>
    /// 活跃文件。声明为可空是因为它**真的可能是 null 或键缺失**（隐藏的 mod 如 709792），
    /// 不能因为「这个端点一般会给文件」就当成必有。
    /// </summary>
    [JsonPropertyName("_aFiles")] public ICollection<ApiModFileInfo>? Files { get; init; }

    /// <summary>
    /// 归档文件。**不是隐藏 mod 专有** —— 实测普通 mod 也会带一条（旧版本，如 575376 / 537550）。
    /// 归档文件的 <c>_bIsArchived</c> 为 true、通常**带** <c>_sVersion</c>。
    /// </summary>
    [JsonPropertyName("_aArchivedFiles")] public ICollection<ApiModFileInfo>? ArchivedFiles { get; init; }

    /// <summary>一句话简介（可能带少量 HTML）。</summary>
    [JsonPropertyName("_sDescription")] public string? Description { get; init; }

    /// <summary>正文，**HTML**。</summary>
    [JsonPropertyName("_sText")] public string? Text { get; init; }

    [JsonPropertyName("_sVersion")] public string? Version { get; init; }

    /// <summary>下载量。**只有这个端点给**，列表侧没有。</summary>
    [JsonPropertyName("_nDownloadCount")] public int DownloadCount { get; init; } = -1;

    [JsonPropertyName("_nLikeCount")] public int LikeCount { get; init; } = -1;
    [JsonPropertyName("_nViewCount")] public int ViewCount { get; init; } = -1;
    [JsonPropertyName("_nPostCount")] public int PostCount { get; init; } = -1;
    [JsonPropertyName("_nThanksCount")] public int ThanksCount { get; init; } = -1;

    [JsonPropertyName("_tsDateAdded")] public long DateAdded { get; init; }
    [JsonPropertyName("_tsDateUpdated")] public long DateUpdated { get; init; }

    /// <summary>作者标记为过时（游戏新版本已经不支持这个 mod）。</summary>
    [JsonPropertyName("_bIsObsolete")] public bool IsObsolete { get; init; }

    [JsonPropertyName("_bHasUpdates")] public bool HasUpdates { get; init; }
    [JsonPropertyName("_nUpdatesCount")] public int UpdatesCount { get; init; } = -1;

    /// <summary>
    /// <c>show</c> / <c>hide</c>。实测 <c>hide</c> 的 mod 其文件全部只在
    /// <c>DownloadPage._aArchivedFiles</c> 里（如 709792），而列表端点也可能根本不给它。
    /// </summary>
    [JsonPropertyName("_sInitialVisibility")] public string? InitialVisibility { get; init; }

    /// <summary>
    /// 内容分级。**对象**不是数组：<c>{"pn":"Partial Nudity","nu":"Full Nudity"}</c>，
    /// 键是缩写、值是给人看的标签。缺失或空 = 无分级（另注意列表侧的
    /// <c>_bHasContentRatings</c> 在此端点不存在，别指望它）。
    /// </summary>
    [JsonPropertyName("_aContentRatings")]
    public Dictionary<string, string>? ContentRatings { get; init; }

    /// <summary>
    /// 分类 —— **最具体**的那一级。鸣潮 Skins 板块下的 mod 拿到的是角色子分类
    /// （实测 709792 → <c>Qingxiao</c>），与列表侧的 <c>_aSubCategory</c> 语义相同，
    /// 但这份**带 `_idRow`**（列表那份不带）。本身挂在根分类上的 mod（UI）拿到的就是根分类名。
    /// </summary>
    [JsonPropertyName("_aCategory")] public ApiSubfeedCategory? Category { get; init; }

    /// <summary>
    /// <c>_aCategory</c> 的**父**分类。它的存在与否就是「<c>_aCategory</c> 是子分类还是根分类」
    /// 的判别依据 —— 实测 <c>cat=Qingxiao/Jinhsi</c> 必带 <c>super=Skins</c>，而
    /// <c>cat=UI</c> 时**整个键不存在**。不看这个就会把 UI 类 mod 的角色显示成「UI」。
    /// </summary>
    [JsonPropertyName("_aSuperCategory")] public ApiSubfeedCategory? SuperCategory { get; init; }

    /// <summary>许可协议，**HTML**（带链接与图片）。拿来显示前必须清洗。</summary>
    [JsonPropertyName("_sLicense")] public string? License { get; init; }
}

public sealed class ApiAuthor
{
    [JsonPropertyName("_sName")] public string? AuthorName { get; init; }
    [JsonPropertyName("_sAvatarUrl")] public string? AvatarImageUrl { get; init; }
    [JsonPropertyName("_sProfileUrl")] public string? ProfileUrl { get; init; }
}

public sealed class ApiImagesRoot
{
    [JsonPropertyName("_aImages")] public ApiImageUrl[] Images { get; init; } = [];
}

public sealed class ApiImageUrl
{
    [JsonPropertyName("_sFile")] public string? ImageId { get; init; }
    [JsonPropertyName("_sBaseUrl")] public string? BaseUrl { get; init; }
}