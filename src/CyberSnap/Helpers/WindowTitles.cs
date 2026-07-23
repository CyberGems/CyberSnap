using System.Windows;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

public static class WindowTitles
{
    public const string Editor = "Editor ᐧ CyberSnap";
    public const string Gallery = "Gallery ᐧ CyberSnap";
    public const string Trimmer = "Video & GIF Trimmer ᐧ CyberSnap";
    public const string Ocr = "Text extraction (OCR) ᐧ CyberSnap";
    public const string Settings = "Configuration ᐧ CyberSnap";
    public const string Preview = "Capture Preview ᐧ CyberSnap";

    public static string Taskbar(string key, string? languageCode = null) =>
        languageCode != null
            ? LocalizationService.Translate(languageCode, key)
            : LocalizationService.Translate(key);

    public static void ApplyTaskbar(Window window, string key, string? languageCode = null)
    {
        LocalizationService.SetSourceText(window, key);
        window.Title = Taskbar(key, languageCode);
    }
}
