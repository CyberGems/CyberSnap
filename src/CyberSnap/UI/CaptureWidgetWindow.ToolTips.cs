using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

    private static void SetToolTipContent(FrameworkElement owner, object content)
    {
        var tooltip = owner.ToolTip as ToolTip ?? new ToolTip();
        tooltip.Content = content;
        owner.ToolTip = tooltip;
        ToolTipService.SetInitialShowDelay(owner, 400);
        ToolTipService.SetBetweenShowDelay(owner, 0);
        var details = content as ActionToolTipContent;
        ToolTipService.SetShowDuration(owner, details is null ? 5000 : 10000);
        AutomationProperties.SetName(tooltip, details?.Title ?? (string)content);
        AutomationProperties.SetHelpText(tooltip, details?.Description ?? "");
    }

    private void WidgetToolTip_Opened(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_openToolTip, sender)) return;
        DismissWidgetToolTip();
        TrackWidgetToolTip((ToolTip)sender);
        AssertToolTipTopmost();
    }

    private void WidgetToolTip_Closed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(_openToolTip, sender)) TrackWidgetToolTip(null);
    }

    private void TrackWidgetToolTip(ToolTip? tip)
    {
        if (_openToolTipOwner is not null) _openToolTipOwner.MouseLeave -= ToolTipOwner_MouseLeave;
        _openToolTip = tip;
        _openToolTipOwner = tip?.PlacementTarget;
        if (_openToolTipOwner is not null) _openToolTipOwner.MouseLeave += ToolTipOwner_MouseLeave;
    }

    // WPF's tooltip safe area can extend over adjacent controls. End the hover at its owner.
    private void ToolTipOwner_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => DismissWidgetToolTip();

    private void DismissWidgetToolTip()
    {
        if (_openToolTip is not { } tip) return;
        TrackWidgetToolTip(null);
        tip.IsOpen = false;
    }
}
