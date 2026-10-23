using CyberSnap.Services;

namespace CyberSnap.UI;

public sealed record ActionToolTipContent(string Title, string Description);

internal enum CaptureToolTipKind
{
    Area, Center, Scrolling, Video, Gif, Ocr, Color, Qr, Ruler, Zoom, Pin, More,
    Gallery, Editor, Fullscreen, Window, Repeat, Panel
}

internal static class CaptureToolTips
{
    internal static ActionToolTipContent Get(CaptureToolTipKind kind, string? language = null)
    {
        var (title, description) = kind switch
        {
            CaptureToolTipKind.Area => ("Area capture tooltip", "Select an area of the screen to capture."),
            CaptureToolTipKind.Center => ("Area capture (from center)", "Select an area from its center out to the edges."),
            CaptureToolTipKind.Scrolling => ("Scrolling capture", "Capture content beyond the visible area."),
            CaptureToolTipKind.Video => ("Record screen", "Record video from an area of the screen."),
            CaptureToolTipKind.Gif => ("Record GIF", "Create an animated GIF from an area of the screen."),
            CaptureToolTipKind.Ocr => ("Extract text (OCR)", "Convert on-screen text into editable text."),
            CaptureToolTipKind.Color => ("Color picker", "Pick a color from the screen and copy its code."),
            CaptureToolTipKind.Qr => ("QR and barcode tooltip", "Scan an on-screen code and view its contents."),
            CaptureToolTipKind.Ruler => ("Ruler", "Measure distances and dimensions in pixels."),
            CaptureToolTipKind.Zoom => ("Zoom", "Magnify the screen and optionally take a screenshot."),
            CaptureToolTipKind.Pin => ("Pin to screen", "Capture or choose an image to keep on top of other windows."),
            CaptureToolTipKind.More => ("More", "Adjust basic capture, recording, and tool options."),
            CaptureToolTipKind.Gallery => ("Gallery", "Browse and manage your captures"),
            CaptureToolTipKind.Editor => ("Annotations Editor", "Annotate an image before saving or sharing it."),
            CaptureToolTipKind.Fullscreen => ("Fullscreen capture", "Capture the full screen without selecting an area."),
            CaptureToolTipKind.Window => ("Active window", "Capture a window without the rest of the desktop."),
            CaptureToolTipKind.Repeat => ("Repeat last area", "Take a new screenshot of the previously selected area."),
            CaptureToolTipKind.Panel => ("Panel options", "Choose which sections are visible and enable compact mode."),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        string lang = language ?? LocalizationService.CurrentLanguageCode;
        return new(LocalizationService.Translate(lang, title), LocalizationService.Translate(lang, description));
    }
}
