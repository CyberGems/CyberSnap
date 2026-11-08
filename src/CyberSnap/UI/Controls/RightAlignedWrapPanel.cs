using System.Windows;
using Panel = System.Windows.Controls.Panel;
using Size = System.Windows.Size;

namespace CyberSnap.UI.Controls;

public sealed class RightAlignedWrapPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Measure(availableSize);

        return LayoutRows(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutRows(finalSize.Width, arrange: true);
        return finalSize;
    }

    private Size LayoutRows(double availableWidth, bool arrange)
    {
        double width = 0, height = 0;
        double rowWidth = 0, rowHeight = 0;
        int rowStart = 0;

        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var size = InternalChildren[i].DesiredSize;
            if (i > rowStart && rowWidth + size.Width > availableWidth)
                FinishRow(i);

            rowWidth += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
        }

        FinishRow(InternalChildren.Count);
        return new Size(width, height);

        void FinishRow(int end)
        {
            if (arrange)
            {
                double x = Math.Max(0, availableWidth - rowWidth);
                for (int i = rowStart; i < end; i++)
                {
                    var child = InternalChildren[i];
                    child.Arrange(new Rect(x, height, child.DesiredSize.Width, rowHeight));
                    x += child.DesiredSize.Width;
                }
            }

            width = Math.Max(width, rowWidth);
            height += rowHeight;
            rowStart = end;
            rowWidth = rowHeight = 0;
        }
    }
}
