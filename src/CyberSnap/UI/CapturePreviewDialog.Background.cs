using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
            if (e.OriginalSource is Visual source && (ReferenceEquals(source, PreviewBackgroundColorBtn)
                || PreviewBackgroundColorBtn.IsAncestorOf(source))) return;
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
            ClosePreviewBackgroundColorPicker();
            return;
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
        picker.CloseRequested += () => ClosePreviewBackgroundColorPicker();
        picker.PreviewKeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape) return;
            key.Handled = true;
            ClosePreviewBackgroundColorPicker();
        };

        var anchor = PreviewBackgroundColorBtn.PointToScreen(new Point(0, 0));
        var physicalPoint = new System.Drawing.Point((int)anchor.X, (int)anchor.Y);
        var workArea = System.Windows.Forms.Screen.FromPoint(physicalPoint).WorkingArea;
        var (_, dpiY) = PopupWindowHelper.GetScaleForPoint(physicalPoint);
        picker.LayoutTransform = new ScaleTransform(UiScale.Current, UiScale.Current);
        var host = new ScrollViewer
        {
            Content = picker,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(160, workArea.Height / dpiY - 24)
        };
        var popup = new Popup
        {
            PlacementTarget = PreviewBackgroundColorBtn,
            Placement = PlacementMode.Top,
            HorizontalOffset = 24 - (ColorPickerPopup.FlyoutWidth + 12) * UiScale.Current,
            VerticalOffset = -4,
            AllowsTransparency = true,
            // Owner clicks dismiss explicitly, so clicking the swatch can reliably toggle it.
            StaysOpen = true,
            Child = host
        };
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
        _previewBackgroundColorPopup.IsOpen = false;
        if (restoreFocus && IsActive && !_isClosing)
            PreviewBackgroundColorBtn.Focus();
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
