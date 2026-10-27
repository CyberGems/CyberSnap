using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CyberSnap.Native;
using CyberSnap.Services;
using CyberSnap.UI.Controls;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Point = System.Windows.Point;

namespace CyberSnap.UI;

public partial class CapturePreviewDialog
{
    private Popup? _previewBackgroundColorPopup;
    private ColorPickerPopup? _previewBackgroundColorPicker;

    private void InitializePreviewBackgroundControls()
    {
        PreviewMouseDown += (_, e) =>
        {
            if (_previewBackgroundColorPopup?.PlacementTarget is Visual target
                && e.OriginalSource is Visual source && (ReferenceEquals(source, target)
                || target.IsAncestorOf(source))) return;
            ClosePreviewBackgroundColorPicker(restoreFocus: false);
        };
        Deactivated += (_, _) => ClosePreviewBackgroundColorPicker(restoreFocus: false);
        LocationChanged += (_, _) => ClosePreviewBackgroundColorPicker(restoreFocus: false);
        Closed += (_, _) => ClosePreviewBackgroundColorPicker(restoreFocus: false);
    }

    private void PreviewBackgroundBtn_Changed(object sender, RoutedEventArgs e)
    {
        bool checkerboard = PreviewBackgroundBtn.IsChecked == true;
        if (_isClosing || _settingsService.Settings.PreviewShowCheckerboard == checkerboard)
            return;
        _settingsService.Settings.PreviewShowCheckerboard = checkerboard;
        _settingsService.Save();
        UpdatePreviewBackground();
        CancelAutoCloseOnInteraction();
    }

    private static Color DefaultPreviewBackgroundColor(bool dark) => dark
        ? Color.FromRgb(0x33, 0x33, 0x37)
        : Color.FromRgb(0xEA, 0xED, 0xF5);

    private Color GetPreviewBackgroundColor(bool dark)
    {
        var settings = _settingsService.Settings;
        int? argb = dark ? settings.PreviewBackgroundDarkColorArgb : settings.PreviewBackgroundLightColorArgb;
        return argb is { } value
            ? Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : DefaultPreviewBackgroundColor(dark);
    }

    private void SetPreviewBackgroundColor(Color? color, bool dark)
    {
        if (_isClosing) return;
        int? argb = color is { } c ? System.Drawing.Color.FromArgb(c.R, c.G, c.B).ToArgb() : null;
        var settings = _settingsService.Settings;
        if (dark) settings.PreviewBackgroundDarkColorArgb = argb;
        else settings.PreviewBackgroundLightColorArgb = argb;
        settings.PreviewShowCheckerboard = false;
        _settingsService.Save();
        UpdatePreviewBackground();
        CancelAutoCloseOnInteraction();
    }

    private void UpdatePreviewBackground()
    {
        bool checkerboard = _settingsService.Settings.PreviewShowCheckerboard;
        var solid = GetPreviewBackgroundColor(Theme.IsDark);
        CheckerboardHost.Background = checkerboard ? Theme.CreateCheckerboardBrush() : Theme.Brush(solid);
        PreviewBackgroundColorSwatch.Background = Theme.Brush(solid);
        PreviewBackgroundBtn.IsChecked = checkerboard;
        string language = _settingsService.Settings.InterfaceLanguage;
        PreviewBackgroundBtn.ToolTip = LocalizationService.Translate(language,
            checkerboard ? "Switch to solid background" : "Show checkerboard background");
        AutomationProperties.SetName(PreviewBackgroundBtn, LocalizationService.Translate(language, "Checkerboard background"));
        string colorLabel = LocalizationService.Translate(language, "Choose solid background color");
        PreviewBackgroundColorBtn.ToolTip = colorLabel;
        AutomationProperties.SetName(PreviewBackgroundColorBtn, colorLabel);
    }

    private void PreviewBackgroundColorBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || !IsLoaded) return;
        CancelAutoCloseOnInteraction();
        if (_previewBackgroundColorPopup?.IsOpen == true)
        {
            bool same = ReferenceEquals(_previewBackgroundColorPopup.PlacementTarget, PreviewBackgroundColorBtn);
            ClosePreviewBackgroundColorPicker();
            if (same) return;
        }

        // Keep the original theme when a screen pick completes after the flyout closes.
        bool dark = Theme.IsDark;
        bool resetting = false;
        var picker = new ColorPickerPopup(LocalizationService.Translate(
            _settingsService.Settings.InterfaceLanguage, "Theme default"));
        picker.SetColor(GetPreviewBackgroundColor(dark));
        picker.ColorChanged += color =>
        {
            if (resetting || _isClosing) return;
            SetPreviewBackgroundColor(color, dark);
            if (color is null)
            {
                resetting = true;
                try { picker.SetColor(DefaultPreviewBackgroundColor(dark)); }
                finally { resetting = false; }
            }
        };
        ShowPreviewColorPicker(picker, PreviewBackgroundColorBtn);
    }

    private void ShowPreviewColorPicker(ColorPickerPopup picker, FrameworkElement button)
    {
        picker.CloseRequested += () => ClosePreviewBackgroundColorPicker();
        picker.PreviewKeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            key.Handled = true;
            ClosePreviewBackgroundColorPicker();
        };

        var anchor = button.PointToScreen(new Point(0, 0));
        var physicalPoint = new System.Drawing.Point((int)anchor.X, (int)anchor.Y);
        var screen = System.Windows.Forms.Screen.FromPoint(physicalPoint);
        var workArea = PopupWindowHelper.TryGetNativeMonitorInfo(screen, out _, out var nativeWork)
            ? nativeWork : screen.WorkingArea;
        var (_, dpiY) = PopupWindowHelper.GetScaleForPoint(physicalPoint);
        picker.LayoutTransform = new ScaleTransform(UiScale.Current, UiScale.Current);
        picker.MaxHeight = Math.Max(160, (workArea.Height / dpiY - 24) / UiScale.Current);
        var popup = new Popup
        {
            PlacementTarget = button,
            Placement = PlacementMode.Top,
            HorizontalOffset = button.ActualWidth - (ColorPickerPopup.FlyoutWidth + 12) * UiScale.Current,
            VerticalOffset = -4,
            AllowsTransparency = true,
            // Owner clicks dismiss explicitly, so clicking the swatch can reliably toggle it.
            StaysOpen = true,
            Child = picker
        };
        // WPF may clamp a Popup to the screen bounds instead of the working area,
        // notably with a side taskbar. Move its native window after placement settles.
        void KeepInsideWorkingArea()
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!popup.IsOpen || PresentationSource.FromVisual(picker) is not HwndSource source
                    || !User32.GetWindowRect(source.Handle, out var rect)) return;
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                int x = Math.Clamp(rect.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
                int y = Math.Clamp(rect.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
                if (x == rect.Left && y == rect.Top) return;
                if (!User32.SetWindowPos(source.Handle, IntPtr.Zero, x, y, 0, 0,
                    User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE))
                    AppDiagnostics.LogWarning("preview.picker.placement", "Could not keep the color picker within the monitor working area.");
            }, DispatcherPriority.ContextIdle);
        }
        popup.Opened += (_, _) => KeepInsideWorkingArea();
        picker.SizeChanged += (_, _) => KeepInsideWorkingArea();
        popup.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_previewBackgroundColorPopup, popup)) return;
            _previewBackgroundColorPopup = null;
            _previewBackgroundColorPicker = null;
            UpdateZoomControlsVisibility();
        };
        _previewBackgroundColorPopup = popup;
        _previewBackgroundColorPicker = picker;
        popup.IsOpen = true;
        picker.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        UpdateZoomControlsVisibility();
    }

    private void ClosePreviewBackgroundColorPicker(bool restoreFocus = true)
    {
        if (_previewBackgroundColorPopup?.IsOpen != true) return;
        var anchor = _previewBackgroundColorPopup.PlacementTarget;
        _previewBackgroundColorPopup.IsOpen = false;
        if (restoreFocus && IsActive && !_isClosing)
            anchor?.Focus();
    }

    private bool HandlePreviewBackgroundPickerKey(KeyEventArgs e)
    {
        if (_previewBackgroundColorPopup?.IsOpen != true) return false;
        if (e.Key == Key.Escape)
        {
            ClosePreviewBackgroundColorPicker();
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.Return)
        {
            _previewBackgroundColorPicker?.AcceptFromKeyboard();
            e.Handled = true;
        }
        return true;
    }
}
