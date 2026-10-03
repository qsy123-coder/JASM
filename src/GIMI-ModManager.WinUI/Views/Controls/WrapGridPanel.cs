using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace GIMI_ModManager.WinUI.Views.Controls;

/// <summary>
/// 卡片容器，两种排版方式二选一：
/// <list type="bullet">
///   <item>默认（<see cref="Columns"/> = 0）：**自动换行**，按卡片自己的尺寸排 —— Mod 市场用它，
///         那边卡片是固定尺寸的竖卡，容器不需要知道有几列。</item>
///   <item>设了 <see cref="Columns"/>：**固定列数**，宽度均分给每一列（Mod 商店的横屏卡片要
///         「一行 6 个」且卡片随窗口伸缩，自适应换行做不到「正好 6 个」）。</item>
/// </list>
/// </summary>
public class WrapGridPanel : Panel
{
    /// <summary>视口宽度还不知道时的兜底值（首次布局前 <c>availableSize</c> 可能是 Infinity）。</summary>
    private const double FallbackWidth = 1200;

    /// <summary>自动换行时给卡片的测量尺寸：竖屏卡片 宽250 + margin6*2 = 262，高 340 + 上下 margin。</summary>
    private static readonly Size AdaptiveChildSize = new(262, 352);

    /// <summary>
    /// 固定列数。**0 = 自动换行**（默认，Mod 市场那条路径逐字未变）。
    /// 大于 0 时每个子元素按 <c>可用宽度 / 列数</c> 均分，卡片撑满自己那一格。
    /// </summary>
    public int Columns { get; set; }

    /// <summary>
    /// 固定列数时单个卡片的最小宽度：窗口窄到放不下这么多列时**按这个下限减少列数**，
    /// 而不是把卡片压成几十像素宽的碎片。0 = 不设下限，永远保持 <see cref="Columns"/> 列。
    /// </summary>
    public double MinItemWidth { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = NormalizeWidth(availableSize.Width);
        var columns = ResolveColumns(maxWidth);

        // ── 自动换行：卡片自己定尺寸，按 DesiredSize 排 ──
        if (columns <= 0)
        {
            double x = 0, y = 0, rowHeight = 0;
            foreach (UIElement child in Children)
            {
                child.Measure(AdaptiveChildSize);
                var cw = child.DesiredSize.Width;
                var ch = child.DesiredSize.Height;

                if (x + cw > maxWidth && x > 0)
                {
                    x = 0;
                    y += rowHeight;
                    rowHeight = 0;
                }
                x += cw;
                rowHeight = Math.Max(rowHeight, ch);
            }
            return new Size(maxWidth, y + rowHeight);
        }

        // ── 固定列数：宽度均分，高度不限（横屏卡片的图片高度是按宽度算出来的，得让它自己量）──
        var itemWidth = maxWidth / columns;
        var contents = (IList<UIElement>)Children;
        double totalHeight = 0;
        for (var start = 0; start < contents.Count; start += columns)
        {
            totalHeight += MeasureRow(contents, start, RowEnd(contents, start, columns), itemWidth);
        }

        return new Size(maxWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var maxWidth = NormalizeWidth(finalSize.Width);
        var columns = ResolveColumns(maxWidth);

        if (columns <= 0)
        {
            double x = 0, y = 0, rowHeight = 0;
            foreach (UIElement child in Children)
            {
                var cw = child.DesiredSize.Width;
                var ch = child.DesiredSize.Height;

                if (x + cw > maxWidth && x > 0)
                {
                    x = 0;
                    y += rowHeight;
                    rowHeight = 0;
                }
                child.Arrange(new Rect(x, y, cw, ch));
                x += cw;
                rowHeight = Math.Max(rowHeight, ch);
            }
            return new Size(maxWidth, y + rowHeight);
        }

        var itemWidth = maxWidth / columns;
        var contents = (IList<UIElement>)Children;
        double top = 0;
        for (var start = 0; start < contents.Count; start += columns)
        {
            var end = RowEnd(contents, start, columns);
            // 同一行的卡片等高：矮的卡片会被拉高到本行最高的那张，行的下边缘才是齐的。
            var rowHeight = RowHeight(contents, start, end);
            for (var i = start; i < end; i++)
            {
                contents[i].Arrange(new Rect((i - start) * itemWidth, top, itemWidth, rowHeight));
            }
            top += rowHeight;
        }

        return new Size(maxWidth, top);
    }

    /// <summary>一行的结束下标（开区间）：最后一行可能不满 <paramref name="columns"/> 个。</summary>
    private static int RowEnd(IList<UIElement> contents, int start, int columns) =>
        Math.Min(start + columns, contents.Count);

    /// <summary>测量一行（宽度定死、高度不限），返回该行高度。</summary>
    private static double MeasureRow(IList<UIElement> contents, int start, int end, double itemWidth)
    {
        for (var i = start; i < end; i++)
            contents[i].Measure(new Size(itemWidth, double.PositiveInfinity));

        return RowHeight(contents, start, end);
    }

    /// <summary>行高 = 该行最高的那张卡片（横屏卡片等高，所以正常情况下就是任意一张的高度）。</summary>
    private static double RowHeight(IList<UIElement> contents, int start, int end)
    {
        double height = 0;
        for (var i = start; i < end; i++)
            height = Math.Max(height, contents[i].DesiredSize.Height);
        return height;
    }

    /// <summary>生效列数：没设固定列数就是 0（走自动换行）；设了但窗口窄到放不下就按下限降列。</summary>
    private int ResolveColumns(double maxWidth)
    {
        if (Columns <= 0)
            return 0;

        if (MinItemWidth <= 0)
            return Columns;

        return Math.Clamp((int)(maxWidth / MinItemWidth), 1, Columns);
    }

    /// <summary>首次布局前宽度可能是 Infinity / 0 / NaN，那种情况下用兜底宽度排。</summary>
    private static double NormalizeWidth(double width) =>
        double.IsFinite(width) && width > 0 ? width : FallbackWidth;
}