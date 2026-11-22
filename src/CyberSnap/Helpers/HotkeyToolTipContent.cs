using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.UI;

namespace CyberSnap.Helpers;

/// <summary>
/// Suite-standard tooltip content: the label plus its keyboard shortcut drawn as the
/// same keycap badge used inside CyberSnap's buttons (<see cref="CyberSnap.UI.Controls.HotkeyBadge"/>)
/// and by the other CyberGems apps, instead of a plain "(Ctrl+S)" suffix.
/// </summary>
internal static class HotkeyToolTipContent
{
    internal static FrameworkElement WithShortcut(string label, string shortcut)
    {
        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var labelText = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimaryBrush");
        row.Children.Add(labelText);
        row.Children.Add(CreateBadge(shortcut));
        return row;
    }

    /// <summary>Keycap chrome matching HotkeyBadge, with free-form shortcut text.</summary>
    internal static Border CreateBadge(string shortcut)
    {
        var text = new TextBlock
        {
            Text = shortcut,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        TextOptions.SetTextFormattingMode(text, TextFormattingMode.Display);
        text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextSecondaryBrush");

        var badge = new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(4, 2, 4, 2),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            SnapsToDevicePixels = true,
            Opacity = 0.9,
            Child = text
        };
        badge.SetResourceReference(Border.BackgroundProperty, "HotkeyBadgeBackgroundBrush");
        badge.SetResourceReference(Border.BorderBrushProperty, "HotkeyBadgeBorderBrush");
        return badge;
    }
}
