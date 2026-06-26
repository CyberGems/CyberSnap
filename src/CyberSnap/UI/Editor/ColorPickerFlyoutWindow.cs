using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CyberSnap.UI.Controls;
using WpfColor = System.Windows.Media.Color;

namespace CyberSnap.UI.Editor;

internal sealed class ColorPickerFlyoutWindow : System.Windows.Window
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private bool _hasColorSet;
    private WpfColor? _selectedColor;

    public WpfColor? SelectedColor => _selectedColor;
    public bool HasColorSet => _hasColorSet;

    public ColorPickerFlyoutWindow(int physicalX, int physicalY, Action<WpfColor?> onColorChanged, WpfColor? initialColor)
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

        SourceInitialized += (s, e) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, IntPtr.Zero, physicalX, physicalY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        };

        Deactivated += (s, e) => Close();

        PreviewKeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }
}
