namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 一块矩形。**单位是物理像素**，与 <c>AppWindow.Position</c> / <c>DisplayArea.WorkArea</c> 同一坐标系
/// （注意别和 <c>WindowEx.Width/Height</c> 的 DIP 混用，那两者差一个 DPI 缩放系数）。
/// </summary>
public readonly record struct OverlayRect(int X, int Y, int Width, int Height);

/// <summary>
/// 浮窗该摆在哪（纯算术，可单测）。
/// </summary>
public static class OverlayPlacement
{
    /// <summary>
    /// 算出浮窗左上角的位置。
    ///
    /// 没保存过位置 → 在工作区（<b>不含任务栏</b>，所以任务栏压不住它）里居中；
    /// 保存过 → 沿用，但**夹回**工作区内。
    ///
    /// **为什么每次都要夹**：浮窗没有标题栏，一旦被摆到屏幕外就再也拖不回来了。而位置失配是常事 ——
    /// 换了显示器、拔掉一块屏、改了缩放，上次存下的坐标就可能落在当前工作区之外。
    ///
    /// 坐标可以是负数：主显示器左边那块屏的 X 就是负的。所以这里一律按 <paramref name="workArea"/>
    /// 的**绝对**坐标夹取，不做任何「坐标必须 ≥ 0」的假设。
    /// </summary>
    /// <param name="savedTopLeft">上次保存的左上角；<c>null</c> 表示没存过。</param>
    public static (int X, int Y) ResolveTopLeft((int X, int Y)? savedTopLeft, OverlayRect workArea,
        int windowWidth, int windowHeight)
    {
        var x = savedTopLeft?.X ?? (workArea.X + ((workArea.Width - windowWidth) / 2));
        var y = savedTopLeft?.Y ?? (workArea.Y + ((workArea.Height - windowHeight) / 2));

        return (ClampAxis(x, workArea.X, workArea.X + workArea.Width - windowWidth),
                ClampAxis(y, workArea.Y, workArea.Y + workArea.Height - windowHeight));
    }

    /// <summary>
    /// 单轴夹取。窗口比工作区还大时（比如 4K 屏上把浮窗拉得比 1080p 副屏还宽），上限会小于下限，
    /// 此时贴住工作区左上角 —— <c>Math.Clamp</c> 遇到上下限颠倒会抛异常，所以这里必须先判一下。
    /// </summary>
    private static int ClampAxis(int value, int min, int max) => max < min ? min : Math.Clamp(value, min, max);
}