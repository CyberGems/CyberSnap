using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.UI;

namespace CyberSnap.UI.Controls;

/// <summary>
/// Small keycap shown inside a confirm or cancel button. Escape is the word
/// "Esc". Enter is the return arrow, matching CyberClock's .btn-kbd.
/// CtrlEnter pairs the "Ctrl" caption with the same arrow.
/// </summary>
public enum HotkeyBadgeKind
{
    Escape,
    Enter,
    CtrlEnter
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
        if (Kind is HotkeyBadgeKind.Enter or HotkeyBadgeKind.CtrlEnter)
        {
            // Two matching keycaps ("Ctrl" + arrow) instead of one wide pill,
            // with a "+" between them. Same chrome, same metrics both sides.
            System.Windows.Controls.Border MiniCap(System.Windows.FrameworkElement content)
            {
                return new System.Windows.Controls.Border
                {
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(4, 2, 4, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = content
                };
            }

            void ApplyChrome(System.Windows.Controls.Border cap)
            {
                // Explicit brushes (resource-backed with computed fallback): a missing
                // or late resource dictionary must never render an invisible keycap.
                cap.Background = ChromeBrush("HotkeyBadgeBackgroundBrush", true);
                cap.BorderBrush = ChromeBrush("HotkeyBadgeBorderBrush", false);
            }

            var arrow = new System.Windows.Shapes.Path
            {
                Data = EnterArrow,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Fill = System.Windows.Media.Brushes.Transparent,
                Width = 10,
                Height = 10,
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

            if (Kind == HotkeyBadgeKind.CtrlEnter)
            {
                var ctrlCap = MiniCap(new TextBlock
                {
                    Text = "Ctrl",
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                });
                TextOptions.SetTextFormattingMode((TextBlock)ctrlCap.Child, TextFormattingMode.Display);
                var arrowCap = MiniCap(arrow);
                ApplyChrome(ctrlCap);
                ApplyChrome(arrowCap);

                var row = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(ctrlCap);
                row.Children.Add(new TextBlock
                {
                    Text = "+",
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 3, 0),
                    Opacity = 0.7
                });
                row.Children.Add(arrowCap);

                Padding = new Thickness(0);
                Background = System.Windows.Media.Brushes.Transparent;
                BorderThickness = new Thickness(0);
                Child = row;
                return;
            }

            Padding = new Thickness(3, 2, 3, 2);
            arrow.StrokeThickness = 2.2;
            arrow.Width = 11;
            arrow.Height = 11;
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

    private static System.Windows.Media.Brush ChromeBrush(string key, bool background)
    {
        if (System.Windows.Application.Current?.TryFindResource(key)
            is System.Windows.Media.Brush found)
            return found;

        // Same values as Theme for dark / light; guarantees a visible keycap.
        bool dark = Theme.IsDark;
        var color = (background, dark) switch
        {
            (true, true) => System.Windows.Media.Color.FromArgb(20, 255, 255, 255),
            (true, false) => System.Windows.Media.Color.FromArgb(16, 0, 0, 0),
            (false, true) => System.Windows.Media.Color.FromArgb(41, 255, 255, 255),
            (false, false) => System.Windows.Media.Color.FromArgb(32, 0, 0, 0),
        };
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
