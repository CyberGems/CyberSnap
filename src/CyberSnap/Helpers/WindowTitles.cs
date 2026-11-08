using System.Windows;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

public static class WindowTitles
{
    public const string Editor = "Editor ᐧ CyberSnap";
    public const string Gallery = "Gallery ᐧ CyberSnap";
    public const string Trimmer = "Video & GIF Trimmer ᐧ CyberSnap";
    public const string Ocr = "Text extraction (OCR) ᐧ CyberSnap";
    public const string Color = "Color picker ᐧ CyberSnap";
    public const string Qr = "QR & Barcode Scanner ⋅ CyberSnap";
    public const string Settings = "Configuration ᐧ CyberSnap";
    public const string About = "About CyberSnap";
    internal const string AboutTooltip = "Title About tooltip";
    public const string Achievements = "Achievements ᐧ CyberSnap";
    public const string Preview = "Capture Preview ᐧ CyberSnap";

    public static string Taskbar(string key, string? languageCode = null) =>
        languageCode != null
            ? LocalizationService.Translate(languageCode, key)
            : LocalizationService.Translate(key);

    internal static string FeatureTitle(string title)
    {
        foreach (string suffix in new[] { " ⋅ CyberSnap", " ᐧ CyberSnap", " · CyberSnap" })
        {
            if (title.EndsWith(suffix, StringComparison.Ordinal))
                return title[..^suffix.Length];
        }
        return title;
    }

    public static void ApplyTaskbar(Window window, string key, string? languageCode = null)
    {
        LocalizationService.SetSourceText(window, key);
        window.Title = Taskbar(key, languageCode);
    }
}
