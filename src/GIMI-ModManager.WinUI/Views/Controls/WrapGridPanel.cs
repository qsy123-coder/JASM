using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace GIMI_ModManager.WinUI.Views.Controls;

public class WrapGridPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double maxWidth = double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width;
        // Use fallback if viewport width not yet known
        if (maxWidth <= 0) maxWidth = 1200;

        double x = 0, y = 0, rowHeight = 0;
        foreach (UIElement child in Children)
        {
            // Measure child with known card dimensions so it reports real height
            child.Measure(new Size(226, 300));
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

    protected override Size ArrangeOverride(Size finalSize)
    {
        double maxWidth = double.IsInfinity(finalSize.Width) ? 1200 : finalSize.Width;
        if (maxWidth <= 0) maxWidth = 1200;

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
}
