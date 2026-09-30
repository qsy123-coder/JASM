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
    /// <summary>名称为空、或没有有效 id 的条目直接丢掉 —— 建不出能用来筛选的项。</summary>
    public static ModStoreRootCategory? FromApi(ApiRootCategory? category)
    {
        if (category is null || category.Id <= 0 || string.IsNullOrWhiteSpace(category.Name))
            return null;

        return new ModStoreRootCategory(category.Id, category.Name.Trim(), category.ItemCount);
    }
}