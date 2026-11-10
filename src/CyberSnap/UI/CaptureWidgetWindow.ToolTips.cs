using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using CyberSnap.Models;
using CyberSnap.Native;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
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
            tooltip = new ToolTip { PlacementTarget = MainPanelBorder, IsHitTestVisible = false };
            tooltip.Opened += (_, _) => WidgetToolTip_Opened(tooltip, owner);
            tooltip.Closed += WidgetToolTip_Closed;
            owner.ToolTip = tooltip;
        }
        tooltip.Placement = _settings.WidgetDockEdge switch
        {
            CaptureDockSide.Top => PlacementMode.Bottom,
            CaptureDockSide.Bottom => PlacementMode.Top,
            CaptureDockSide.Left => PlacementMode.Right,
            _ => PlacementMode.Left
        };
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
        var start = MainPanelBorder.PointToScreen(new Point());
        var end = MainPanelBorder.PointToScreen(new Point(MainPanelBorder.ActualWidth, MainPanelBorder.ActualHeight));
        var panel = Drawing.Rectangle.FromLTRB((int)Math.Floor(start.X), (int)Math.Floor(start.Y),
            (int)Math.Ceiling(end.X), (int)Math.Ceiling(end.Y));
        var size = rect.ToRectangle().Size;
        if (size.Width > work.Width || size.Height > work.Height) return false;

        var anchor = owner.PointToScreen(new Point());
        int x = Math.Clamp((int)Math.Round(anchor.X), work.Left, work.Right - size.Width);
        int y = Math.Clamp((int)Math.Round(anchor.Y), work.Top, work.Bottom - size.Height);
        int gap = (int)Math.Ceiling(8 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var below = new Drawing.Point(x, panel.Bottom + gap);
        var above = new Drawing.Point(x, panel.Top - size.Height - gap);
        var right = new Drawing.Point(panel.Right + gap, y);
        var left = new Drawing.Point(panel.Left - size.Width - gap, y);
        var preferred = _settings.WidgetDockEdge switch
        {
            CaptureDockSide.Top => below,
            CaptureDockSide.Bottom => above,
            CaptureDockSide.Left => right,
            _ => left
        };
        foreach (var point in new[] { preferred, right, left, below, above })
        {
            if (!work.Contains(new Drawing.Rectangle(point, size))) continue;
            return User32.SetWindowPos(source.Handle, User32.HWND_TOPMOST, point.X, point.Y, 0, 0,
                User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER);
        }

        // On a cramped monitor, a tooltip must never cover the controls it explains.
        return false;
    }
}
