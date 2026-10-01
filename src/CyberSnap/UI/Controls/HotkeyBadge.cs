using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CyberSnap.UI.Controls;

/// <summary>
/// Small keycap shown inside a confirm or cancel button. Escape is the word
/// "Esc". Enter is the return arrow, matching CyberClock's .btn-kbd.
/// </summary>
public enum HotkeyBadgeKind
{
    Escape,
    Enter
}

public sealed class HotkeyBadge : Border
{
    private static readonly Geometry EnterArrow = Geometry.Parse(
        "M20,4 V11 A3,3 0 0 1 17,14 H4 M4,14 L9,9 M4,14 L9,19");

    public static readonly DependencyProperty KindProperty =
        DependencyProperty.Register(
            nameof(Kind),
            typeof(HotkeyBadgeKind),
            typeof(HotkeyBadge),
            new PropertyMetadata(HotkeyBadgeKind.Escape, OnKindChanged));

    public HotkeyBadgeKind Kind
    {
        get => (HotkeyBadgeKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public HotkeyBadge()
    {
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Opacity = 0.9;
        SetResourceReference(BackgroundProperty, "HotkeyBadgeBackgroundBrush");
        SetResourceReference(BorderBrushProperty, "HotkeyBadgeBorderBrush");
        Rebuild();
    }

    /// <summary>Label plus an optional keycap. The label inherits the button font.</summary>
    public static StackPanel Labeled(string text, HotkeyBadgeKind? kind)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (kind is { } badge)
        {
            row.Children.Add(new HotkeyBadge
            {
                Kind = badge,
                Margin = new Thickness(6, 0, 0, 0)
            });
        }
        return row;
    }

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyBadge badge)
            badge.Rebuild();
    }

    private void Rebuild()
    {
        if (Kind == HotkeyBadgeKind.Enter)
        {
            Padding = new Thickness(3, 2, 3, 2);
            var arrow = new System.Windows.Shapes.Path
            {
                Data = EnterArrow,
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = System.Windows.Media.Brushes.Transparent,
                Width = 11,
                Height = 11,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            arrow.SetBinding(
                System.Windows.Shapes.Path.StrokeProperty,
                new System.Windows.Data.Binding(nameof(System.Windows.Controls.Control.Foreground))
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor,
                        typeof(System.Windows.Controls.Control),
                        1)
                });
            Child = arrow;
            return;
        }

        Padding = new Thickness(4, 2, 4, 2);
        var label = new TextBlock
        {
            Text = "Esc",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        TextOptions.SetTextFormattingMode(label, TextFormattingMode.Display);
        Child = label;
    }
}
