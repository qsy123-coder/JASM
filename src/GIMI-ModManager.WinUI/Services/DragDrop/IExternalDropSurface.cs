using Windows.Foundation;

namespace GIMI_ModManager.WinUI.Services.DragDrop;

/// <summary>
/// 「本页愿意接住外部拖放」的页面/窗口所实现的接口。
///
/// <para>
/// <b>它为什么存在</b>：在关掉 UAC 的机器上（<c>EnableLUA=0</c>，网吧无盘机常见），WinUI 3
/// 收不到外部拖放，XAML 的 <c>DragEnter</c>/<c>Drop</c> 根本不触发。于是 <see cref="ExternalDropChannel"/>
/// 自己接管了那一路 OLE 事件 —— 但它拿到的是**裸的屏幕坐标与文件路径**，
/// 不知道用户到底压在哪张角色卡片上。本接口就是把这层「页面自己的判据」还给页面：
/// 通道只管把「落在哪一点、带了哪些文件」递进来，**收不收、算谁的、装到哪个角色**，仍由页面说了算。
/// </para>
///
/// <para>
/// <b>这样做的意义是判据只有一份</b>：页面里既有的落点逻辑（卡片上落压缩包要改判给自动识别、
/// 文件夹才装进落点角色……）不必为这条新通道抄第二遍 —— 抄一遍就等于埋一个「两条路迟早分家」的雷。
/// </para>
/// </summary>
internal interface IExternalDropSurface
{
    /// <summary>写进日志的名字，用来区分是哪个页面接的（如「概览页」「浮窗」）。</summary>
    string DropSurfaceName { get; }

    /// <summary>
    /// 指到这个点（**XAML 坐标，DIP**）时，这一处接不接外部拖放。
    /// 返回值决定光标画成「可放置」还是禁止符 —— 所以它必须诚实：
    /// 一律返回 true 会让用户以为到处都能放，松手却什么都不发生。
    /// </summary>
    bool CanAcceptDropAt(Point point);

    /// <summary>
    /// 真的落下了。<paramref name="paths"/> 是拖进来的文件/文件夹的**完整路径**（至少一个）。
    /// 实现方负责按自己既有的判据处理（拒绝的走既有的拒绝表现，不要静默吞掉）。
    /// </summary>
    Task HandleExternalDropAsync(IReadOnlyList<string> paths, Point point);
}