using System.Text.Json.Serialization;

namespace GIMI_ModManager.Core.Services.GameBanana.ApiModels;

/// <summary>
/// <c>apiv11/Game/{id}/ProfilePage</c>（板块主页）的响应 —— 这里**只用它的根分类清单**。
///
/// 为什么不去别处找分类：实测没有「列出板块子分类」的端点（<c>Game/{id}/Categories</c> 404、
/// <c>Mod/Categories?_idGameRow=…</c> 400、<c>ModCategory/Index</c> 忽略游戏过滤按全局分页每页 5 条）。
/// 板块主页倒是稳定给出根分类与条目数，一次请求就够侧栏「分类」那一节用。
///
/// 这个响应体很大（含 <c>_aSections</c> 等一堆板块元数据），只反序列化需要的字段。
/// </summary>
public sealed class ApiGameProfilePageResponse
{
    [JsonPropertyName("_aModRootCategories")] public ApiRootCategory[]? ModRootCategories { get; init; }
}

/// <summary>
/// 板块主页里的根分类条目（实测鸣潮三个：<c>Skins</c> 29524 / <c>Other/Misc</c> 29493 / <c>UI</c> 29496）。
///
/// ⚠️ 与列表记录里的 <c>_aRootCategory</c>（<see cref="ApiSubfeedCategory"/>）**不是同一个东西**：
/// 那份没有 id，这份有 <c>_idRow</c>；那份没有条目数，这份有 <c>_nItemCount</c>。
/// 侧栏那些数字就来自这里 —— 别去数列表记录，那得翻几十页。
/// </summary>
public sealed class ApiRootCategory
{
    [JsonPropertyName("_idRow")] public int Id { get; init; } = -1;

    [JsonPropertyName("_sName")] public string? Name { get; init; }

    /// <summary>该分类下的条目数。字段缺失时保持 -1（= 未知），不要当成 0。</summary>
    [JsonPropertyName("_nItemCount")] public int ItemCount { get; init; } = -1;
}