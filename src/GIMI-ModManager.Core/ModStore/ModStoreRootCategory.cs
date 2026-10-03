using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;

namespace GIMI_ModManager.Core.ModStore;

/// <summary>
/// 板块的根分类（鸣潮实测三个：<c>Skins</c> / <c>Other/Misc</c> / <c>UI</c>），带条目数。
///
/// 与 <see cref="ModStoreCategory"/> 的分工：
/// <list type="bullet">
///   <item><see cref="ModStoreCategory"/> = 一条 mod 身上的分类标签，id 可能抠不出来，没有条目数；</item>
///   <item>本类 = 板块主页给的**可点选的筛选项**，id 一定有效，还带条目数。</item>
/// </list>
/// 按 id 筛是服务端行为（<c>Mod/Index?_aFilters[Generic_Category]</c>，实测有效），
/// 所以侧栏点分类不需要客户端过滤那一套。
/// </summary>
/// <param name="Id">分类 id，用于 <c>_aFilters[Generic_Category]</c>。</param>
/// <param name="Name">分类名，直接显示（GameBanana 的英文名，没有本地化）。</param>
/// <param name="ItemCount">条目数；**小于 0 = 未知**（接口没给这个字段），不是「0 条」。</param>
public sealed record ModStoreRootCategory(int Id, string Name, int ItemCount)
{
    /// <summary>
    /// 分类图标（侧栏显示）。取不到 / 校验不过就是 null —— 界面退回字形图标，不是空一格。
    ///
    /// 做成 <c>init</c> 属性而不是第 4 个位置参数：位置参数会改掉构造签名，
    /// 而现有的构造点（测试里那批 <c>FromApi</c> 断言）没必要跟着动。
    /// </summary>
    public Uri? IconUrl { get; init; }

    /// <summary>名称为空、或没有有效 id 的条目直接丢掉 —— 建不出能用来筛选的项。</summary>
    public static ModStoreRootCategory? FromApi(ApiRootCategory? category)
    {
        if (category is null || category.Id <= 0 || string.IsNullOrWhiteSpace(category.Name))
            return null;

        return new ModStoreRootCategory(category.Id, category.Name.Trim(), category.ItemCount)
        {
            IconUrl = GameBananaMediaUrls.TryCreateImageUrl(category.IconUrl)
        };
    }
}