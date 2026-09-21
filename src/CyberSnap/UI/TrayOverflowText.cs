using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CyberSnap.UI;

internal static class TrayOverflowText
{
    public static void Set(TextBlock target, string text)
    {
        const string marker = "(^)";
        int markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            target.Text = text;
            return;
        }

        string before = text[..markerIndex];
        string after = text[(markerIndex + marker.Length)..];
        Match match = Regex.Match(before, @"^(.*\s)(\S+\s+\S+\s*)$", RegexOptions.Singleline);
        string prefix = match.Success ? match.Groups[1].Value : before;
        string anchorText = match.Success ? match.Groups[2].Value.TrimEnd() : string.Empty;

        target.Text = string.Empty;
        target.Inlines.Clear();
        target.Inlines.Add(new Run(prefix));

        var label = new TextBlock
        {
            Text = anchorText,
            FontFamily = target.FontFamily,
            FontSize = target.FontSize,
            Foreground = target.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var noWrapGroup = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        noWrapGroup.Children.Add(label);
        noWrapGroup.Children.Add(CreateIndicator());

        target.Inlines.Add(new InlineUIContainer(noWrapGroup)
        {
            BaselineAlignment = BaselineAlignment.Baseline,
        });
        target.Inlines.Add(new Run(after));
    }

    private static Border CreateIndicator()
    {
        var chevron = new Path
        {
            Data = Geometry.Parse("M 1,6 L 4.5,2.5 L 8,6"),
            Stretch = Stretch.Fill,
            StrokeThickness = 1.5,
            Width = 9,
            Height = 9,
        };
        chevron.SetResourceReference(Shape.StrokeProperty, "ThemeTextSecondaryBrush");

        var indicator = new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(2.5, 0, 2.5, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = chevron,
            VerticalAlignment = VerticalAlignment.Center,
        };
        indicator.SetResourceReference(Border.BorderBrushProperty, "ThemeInputBorderBrush");
        indicator.SetResourceReference(Border.BackgroundProperty, "ThemeTabHoverBrush");
        return indicator;
    }
}
