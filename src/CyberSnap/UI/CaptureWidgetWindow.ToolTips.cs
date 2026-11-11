using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using CyberSnap.Native;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
    private const double ToolTipGap = 6;
    private UIElement? _openToolTipOwner;

    private void RefreshToolTips()
    {
        SetToolTip(CaptureButton, _settings.DefaultCaptureMode == Models.CaptureMode.Center
            ? CaptureToolTipKind.Center : CaptureToolTipKind.Area);
        AreaModeLabel.Text = Services.LocalizationService.Translate(_settings.InterfaceLanguage, "Area");
        SetActionLabel(FullscreenModeButton, FullscreenModeLabel, "Fullscreen short", CaptureToolTipKind.Fullscreen, showHelp: true);
        SetActionLabel(WindowModeButton, WindowModeLabel, "Active window short", CaptureToolTipKind.Window, showHelp: true);
        SetActionLabel(RepeatModeButton, RepeatModeLabel, "Repeat short", CaptureToolTipKind.Repeat, showHelp: true);
        SetActionLabel(GalleryShortcutButton, GalleryShortcutLabel, "Gallery", CaptureToolTipKind.Gallery);
        SetActionLabel(EditorShortcutButton, EditorShortcutLabel, "Editor", CaptureToolTipKind.Editor);
        SetToolTip(ScrollToolButton, CaptureToolTipKind.Scrolling);
        SetToolTip(VideoToolButton, CaptureToolTipKind.Video);
        SetToolTip(GifToolButton, CaptureToolTipKind.Gif);
        SetToolTip(OcrToolButton, CaptureToolTipKind.Ocr);
        SetToolTip(ColorToolButton, CaptureToolTipKind.Color);
        SetToolTip(QrToolButton, CaptureToolTipKind.Qr);
        SetToolTip(RulerToolButton, CaptureToolTipKind.Ruler);
        SetToolTip(ZoomToolButton, CaptureToolTipKind.Zoom);
        SetToolTip(PinToolButton, CaptureToolTipKind.Pin);
        SetToolTip(WidgetMoreButton, CaptureToolTipKind.More);
    }

    private void SetActionLabel(FrameworkElement button, TextBlock label, string key, CaptureToolTipKind kind, bool showHelp = false)
    {
        label.Text = Services.LocalizationService.Translate(_settings.InterfaceLanguage, key);
        AutomationProperties.SetName(button, label.Text);
        string description = CaptureToolTips.Get(kind, _settings.InterfaceLanguage).Description;
        AutomationProperties.SetHelpText(button, description);
        if (showHelp) SetToolTipContent(button, description);
    }

    private void SetToolTip(FrameworkElement owner, CaptureToolTipKind kind)
    {
        var content = CaptureToolTips.Get(kind, _settings.InterfaceLanguage);
        AutomationProperties.SetName(owner, content.Title);
        AutomationProperties.SetHelpText(owner, content.Description);
        SetToolTipContent(owner, content);
    }

    private void SetToolTipContent(FrameworkElement owner, object content)
    {
        if (owner.ToolTip is not ToolTip tooltip)
        {
            tooltip = new ToolTip
            {
                PlacementTarget = owner,
                Placement = PlacementMode.Top,
                VerticalOffset = -ToolTipGap,
                IsHitTestVisible = false
            };
            tooltip.Opened += (_, _) => WidgetToolTip_Opened(tooltip, owner);
            tooltip.Closed += WidgetToolTip_Closed;
            owner.ToolTip = tooltip;
        }
        tooltip.Content = content;
        ToolTipService.SetInitialShowDelay(owner, 900);
        ToolTipService.SetBetweenShowDelay(owner, 0);
        var details = content as ActionToolTipContent;
        ToolTipService.SetShowDuration(owner, details is null ? 5000 : 10000);
        AutomationProperties.SetName(tooltip, details?.Title ?? (string)content);
        AutomationProperties.SetHelpText(tooltip, details?.Description ?? "");
    }

    private void WidgetToolTip_Opened(ToolTip tip, FrameworkElement owner)
    {
        if (ReferenceEquals(_openToolTip, tip)) return;
        DismissWidgetToolTip();
        // Closing must remove the popup before a capture, even if Windows enables tooltip fades.
        if (tip.Parent is Popup popup)
            popup.SetCurrentValue(Popup.PopupAnimationProperty, PopupAnimation.None);
        if (!IsVisible || !_isExpanded || _fullscreenSuppressed || _widgetMenuOpen || !TryPositionWidgetToolTip(tip, owner))
        {
            tip.IsOpen = false;
            return;
        }
        TrackWidgetToolTip(tip, owner);
    }

    private void WidgetToolTip_Closed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_openToolTip, sender)) TrackWidgetToolTip(null, null);
    }

    private void TrackWidgetToolTip(ToolTip? tip, UIElement? owner)
    {
        if (_openToolTipOwner is not null) _openToolTipOwner.MouseLeave -= ToolTipOwner_MouseLeave;
        _openToolTip = tip;
        _openToolTipOwner = owner;
        if (_openToolTipOwner is not null) _openToolTipOwner.MouseLeave += ToolTipOwner_MouseLeave;
    }

    // WPF's tooltip safe area can extend over adjacent controls. End the hover at its owner.
    private void ToolTipOwner_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => DismissWidgetToolTip();

    private void Widget_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => DismissWidgetToolTip();

    private void DismissWidgetToolTip()
    {
        if (_openToolTip is not { } tip) return;
        TrackWidgetToolTip(null, null);
        tip.IsOpen = false;
    }

    private bool TryPositionWidgetToolTip(ToolTip tip, FrameworkElement owner)
    {
        if (PresentationSource.FromVisual(tip) is not HwndSource source || !User32.GetWindowRect(source.Handle, out var rect))
            return false;

        var hwnd = new WindowInteropHelper(this).Handle;
        var work = GetPhysicalWorkingArea(Forms.Screen.FromHandle(hwnd));
        var start = owner.PointToScreen(new Point());
        var end = owner.PointToScreen(new Point(owner.ActualWidth, owner.ActualHeight));
        var anchor = Drawing.Rectangle.FromLTRB((int)Math.Floor(start.X), (int)Math.Floor(start.Y),
            (int)Math.Ceiling(end.X), (int)Math.Ceiling(end.Y));
        var size = rect.ToRectangle().Size;
        if (size.Width > work.Width || size.Height > work.Height) return false;

        int x = Math.Clamp(anchor.Left, work.Left, work.Right - size.Width);
        int y = Math.Clamp(anchor.Top, work.Top, work.Bottom - size.Height);
        var dpi = VisualTreeHelper.GetDpi(owner);
        int horizontalGap = (int)Math.Ceiling(ToolTipGap * dpi.DpiScaleX);
        int verticalGap = (int)Math.Ceiling(ToolTipGap * dpi.DpiScaleY);
        var above = new Drawing.Point(x, anchor.Top - size.Height - verticalGap);
        var below = new Drawing.Point(x, anchor.Bottom + verticalGap);
        var right = new Drawing.Point(anchor.Right + horizontalGap, y);
        var left = new Drawing.Point(anchor.Left - size.Width - horizontalGap, y);
        foreach (var point in new[] { above, below, right, left })
        {
            if (!work.Contains(new Drawing.Rectangle(point, size))) continue;
            return User32.SetWindowPos(source.Handle, User32.HWND_TOPMOST, point.X, point.Y, 0, 0,
                User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER);
        }

        // Even on a cramped monitor, leave the hovered control clear.
        return false;
    }
}
