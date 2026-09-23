namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 浮窗列表里一项要暴露给「过滤 + 排序」的字段。
///
/// 做成接口而不是把过滤逻辑写进 WinUI 的 ViewModel：`JASM.Tests` 只引用 Core
/// （`net9.0`，非 windows 目标），过滤/排序这种有边界条件的纯逻辑放 Core 才测得着。
/// </summary>
public interface IOverlayModEntry
{
    string FolderName { get; }
    string Name { get; }
    string Author { get; }
    bool IsEnabled { get; }
}

/// <summary>
/// 浮窗 Mod 列表的过滤与排序。
///
/// **匹配字段与主窗口画廊逐字一致**（<c>CharacterGalleryViewModel.ResetContent</c> 的
/// FolderName / Name / Author 三字段 <c>OrdinalIgnoreCase</c> 子串匹配）：同一个搜索词在两个界面上
/// 必须命中同一批 Mod，否则用户会以为浮窗"丢了东西"。
///
/// 排序同样对齐画廊的最后一步：**已启用的项前移**。区别是画廊为增量更新同一个
/// <c>ObservableCollection</c> 用的是 <c>Remove</c> + <c>Insert(0, …)</c> 那套；
/// 浮窗每次都整体重建列表，没有增量更新的包袱，用一次稳定排序更不容易写错。
/// </summary>
public static class OverlayModFilter
{
    /// <summary>
    /// 按 <paramref name="searchText"/> 过滤，并把已启用的项排到前面。
    /// 两个分组内部都保持传入时的相对顺序（<c>OrderByDescending</c> 是稳定排序）。
    /// </summary>
    public static List<T> Apply<T>(IEnumerable<T> mods, string? searchText) where T : IOverlayModEntry
    {
        ArgumentNullException.ThrowIfNull(mods);

        var filtered = string.IsNullOrEmpty(searchText)
            ? mods.ToList()
            : mods.Where(m => Matches(m, searchText)).ToList();

        // bool 降序 = true 在前，即已启用的项排在前面
        return filtered.OrderByDescending(m => m.IsEnabled).ToList();
    }

    /// <summary>三字段任意一个包含搜索词即算命中（大小写不敏感）。</summary>
    public static bool Matches(IOverlayModEntry mod, string searchText)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(searchText);

        return mod.FolderName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
               || mod.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase)
               || mod.Author.Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }
}