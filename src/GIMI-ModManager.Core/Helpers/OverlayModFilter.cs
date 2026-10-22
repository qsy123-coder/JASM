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
}

/// <summary>
/// 浮窗 Mod 列表的过滤。
///
/// **匹配字段与主窗口画廊逐字一致**（<c>CharacterGalleryViewModel.ResetContent</c> 的
/// FolderName / Name / Author 三字段 <c>OrdinalIgnoreCase</c> 子串匹配）：同一个搜索词在两个界面上
/// 必须命中同一批 Mod，否则用户会以为浮窗"丢了东西"。
///
/// **顺序一律保持传入时的样子，不按勾选状态重排。** 浮窗每次唤出都会整体重建列表，
/// 若在这里把已启用的提到最前，刚勾上的那件（比如第六行）下次打开就会跳到第一行 ——
/// 用户是按位置记 Mod 的，位置一动就找不到刚才试的是哪件了。
/// </summary>
public static class OverlayModFilter
{
    /// <summary>
    /// 按 <paramref name="searchText"/> 过滤，命中项保持传入时的相对顺序。
    /// </summary>
    public static List<T> Apply<T>(IEnumerable<T> mods, string? searchText) where T : IOverlayModEntry
    {
        ArgumentNullException.ThrowIfNull(mods);

        return string.IsNullOrEmpty(searchText)
            ? mods.ToList()
            : mods.Where(m => Matches(m, searchText)).ToList();
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