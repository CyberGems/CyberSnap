using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using ToolTip = System.Windows.Controls.ToolTip;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
    private void RefreshToolTips()
    {
        SetToolTip(CaptureButton, _settings.DefaultCaptureMode == Models.CaptureMode.Center
            ? CaptureToolTipKind.Center : CaptureToolTipKind.Area);
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

    private void SetToolTip(FrameworkElement owner, CaptureToolTipKind kind)
    {
        var content = CaptureToolTips.Get(kind, _settings.InterfaceLanguage);
        if (owner.ToolTip is ToolTip { Content: ActionToolTipContent } existing)
            existing.Content = content;
        else
            owner.ToolTip = new ToolTip { Content = content };

        ToolTipService.SetInitialShowDelay(owner, 400);
        ToolTipService.SetBetweenShowDelay(owner, 0);
        ToolTipService.SetShowDuration(owner, 10000);
        AutomationProperties.SetName(owner, content.Title);
        AutomationProperties.SetHelpText(owner, content.Description);
        var tooltip = (ToolTip)owner.ToolTip;
        AutomationProperties.SetName(tooltip, content.Title);
        AutomationProperties.SetHelpText(tooltip, content.Description);
    }
}
