using System;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.UI.Controls;
using WpfColor = System.Windows.Media.Color;

namespace CyberSnap.UI.Editor;

internal sealed class ColorPickerFlyoutWindow : System.Windows.Window
{
    private bool _hasColorSet;
    private WpfColor? _selectedColor;

    public WpfColor? SelectedColor => _selectedColor;
    public bool HasColorSet => _hasColorSet;

    public ColorPickerFlyoutWindow(System.Windows.Point screenPos, Action<WpfColor?> onColorChanged, WpfColor? initialColor)
    {
        WindowStyle = System.Windows.WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        SizeToContent = System.Windows.SizeToContent.WidthAndHeight;
        Topmost = true;

        var picker = new ColorPickerPopup();
        if (initialColor is { } c)
        {
            picker.SetColor(c);
        }

        picker.ColorChanged += color =>
        {
            _selectedColor = color;
            onColorChanged(color);
        };
        picker.CloseRequested += () =>
        {
            _hasColorSet = true;
            Close();
        };

        Content = picker;

        Left = screenPos.X;
        Top = screenPos.Y;

        Deactivated += (s, e) => Close();

        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }
}
