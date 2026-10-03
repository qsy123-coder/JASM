namespace GIMI_ModManager.Core.Services.GameBanana.Models;

/// <summary>
/// 搜索端点一次取数的结果：命中数 + 顺带抠出来的子分类（角色）图标。
///
/// 为什么两件事挤在一个返回里：这次搜索**本来就是**为了侧栏那个数字打的（每个角色一次），
/// 而角色的图标恰好只在这份记录里（<c>_aSubCategory._sIconUrl</c>）——
/// 拆成两个方法等于让五十多个角色各多打一次请求，只为了拿一个小图标。
/// </summary>
/// <param name="ModCount">
/// 命中的 Mod 条数。**0 与 null 不同**：0 = 确实一条都没有（服务端命中 0 时不给那一项），
/// null = 没取到 / 不知道。侧栏里这两种显示得不一样。
/// </param>
/// <param name="SubCategoryIconUrl">
/// 与搜索词相符的角色图标；名字对不上、图标为空串或不在图床上时是 null（界面退回字形图标）。
/// </param>
public sealed record GbSearchSummary(int? ModCount, Uri? SubCategoryIconUrl);