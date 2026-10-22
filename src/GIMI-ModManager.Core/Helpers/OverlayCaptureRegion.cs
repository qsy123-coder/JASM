namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 浮窗截图用的一块屏幕区域。**单位是物理像素**，与 <see cref="OverlayRect"/> 同一套坐标系
/// （虚拟屏幕坐标，副屏摆在主屏左边时 X 为负）。
///
/// 纯算术、不碰 win32：拖框事件交上来的只是两个点，归一化与夹取都在这里做，能单测。
/// </summary>
public readonly record struct OverlayCaptureRegion(int X, int Y, int Width, int Height)
{
    /// <summary>
    /// 宽或高小于它就算误操作（拖框时手一抖点了一下）。
    /// 太小的一块区域既没有审阅价值，也容易是「想点取消却点成了截图」。
    /// </summary>
    public const int MinimumSide = 8;

    /// <summary>这块区域能不能拿去截图。不检查是否在屏幕内 —— 那件事交给 <see cref="ClampTo"/>。</summary>
    public bool IsUsable => Width >= MinimumSide && Height >= MinimumSide;

    /// <summary>
    /// 从拖框的起点与终点造一块区域。两个点顺序随意（用户可能从右下往左上拖），
    /// 这里统一归一化成左上角 + 正数宽高。
    /// </summary>
    public static OverlayCaptureRegion FromPoints(int startX, int startY, int endX, int endY)
        => new(
            Math.Min(startX, endX),
            Math.Min(startY, endY),
            Math.Abs(endX - startX),
            Math.Abs(endY - startY));

    /// <summary>
    /// 把区域夹回给定范围（通常是整个虚拟屏幕）。
    ///
    /// 用途是「复用上次区域」：换过分辨率、拔过显示器之后，旧坐标可能已经落在屏幕外。
    /// 完全不相交时返回 <c>null</c> —— 那不是「夹一下还能用」，而是必须让用户重新框选。
    /// </summary>
    public OverlayCaptureRegion? ClampTo(OverlayRect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return null;

        var left = Math.Max(X, bounds.X);
        var top = Math.Max(Y, bounds.Y);
        var right = Math.Min(X + Width, bounds.X + bounds.Width);
        var bottom = Math.Min(Y + Height, bounds.Y + bounds.Height);

        if (right <= left || bottom <= top)
            return null;

        var clamped = new OverlayCaptureRegion(left, top, right - left, bottom - top);

        // 夹完可能只剩一条缝：那不是一块能截的区域，按无效处理，别让调用方拿一张 3×300 的图去命名落盘
        return clamped.IsUsable ? clamped : null;
    }
}
