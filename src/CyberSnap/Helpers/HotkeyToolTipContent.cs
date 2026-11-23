using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.RegularExpressions;
using CyberSnap.UI;

namespace CyberSnap.Helpers;

/// <summary>
/// Suite-standard tooltip content: the label plus its keyboard shortcut drawn as the
/// same keycap badge used inside CyberSnap's buttons (<see cref="CyberSnap.UI.Controls.HotkeyBadge"/>)
/// and by the other CyberGems apps, instead of a plain "(Ctrl+S)" suffix.
/// </summary>
internal static class HotkeyToolTipContent
{
    /// <summary>
    /// Tooltips are measured with an unlimited width, so a horizontal row never wraps its
    /// label and long texts stretched into one very wide line. Capping the label makes it
    /// wrap while the keycap stays right after the last line.
    /// </summary>
    private const double LabelMaxWidth = 320;

    /// <summary>Trailing keycap hint written as plain text, e.g. "Copy and close (Ctrl+Enter)".</summary>
    private static readonly Regex TrailingHint = new(
        @"\s*\(([^()]{1,32})\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] BareKeyNames =
    {
        "Esc", "Enter", "Tab", "Space", "Delete", "Insert", "Home", "End", "Backspace"
    };

    internal static FrameworkElement WithShortcut(string label, string? shortcut)
    {
        var labelText = new TextBlock
        {
            Text = label,
            MaxWidth = LabelMaxWidth,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextPrimaryBrush");

        if (string.IsNullOrWhiteSpace(shortcut))
            return labelText;

        var row = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(labelText);
        row.Children.Add(CreateBadge(shortcut));
        return row;
    }

    /// <summary>
    /// Same as <see cref="WithShortcut"/>, for already localized texts that still carry the
    /// shortcut as a trailing parenthetical (the legacy localization entries). The hint is
    /// extracted and drawn as a keycap; texts without one stay plain.
    /// </summary>
    internal static FrameworkElement WithShortcutHint(string label)
    {
        string text = label ?? string.Empty;
        var match = TrailingHint.Match(text);
        if (match.Success && LooksLikeShortcut(match.Groups[1].Value))
            return WithShortcut(text[..match.Index].TrimEnd(), match.Groups[1].Value.Trim());
        return WithShortcut(text, null);
    }

    /// <summary>Guards against turning an ordinary prose parenthetical into a keycap.</summary>
    private static bool LooksLikeShortcut(string hint)
    {
        string trimmed = hint.Trim();
        if (trimmed.Length == 0)
            return false;

        if (trimmed.Contains('+'))
            return trimmed.Split('+').All(part => part.Length is > 0 and <= 12);

        if (BareKeyNames.Any(key => string.Equals(key, trimmed, StringComparison.OrdinalIgnoreCase)))
            return true;

        return trimmed.Length > 1
            && (trimmed[0] == 'F' || trimmed[0] == 'f')
            && int.TryParse(trimmed[1..], out int fn)
            && fn is >= 1 and <= 24;
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
