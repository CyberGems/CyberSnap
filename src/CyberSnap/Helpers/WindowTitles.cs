using System.Windows;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

public static class WindowTitles
{
    public const string Editor = "CyberSnap · Editor";
    public const string Gallery = "CyberSnap · Gallery";
    public const string Trimmer = "CyberSnap · Video & GIF Trimmer";
    public const string Ocr = "CyberSnap · Text extraction (OCR)";
    public const string Color = "CyberSnap · Color picker";
    public const string Qr = "CyberSnap · QR & Barcode Scanner";
    public const string Settings = "CyberSnap · Configuration";
    public const string About = "CyberSnap · About";
    internal const string AboutTooltip = "Title About tooltip";
    public const string Achievements = "CyberSnap · Achievements";
    public const string Preview = "CyberSnap · Capture Preview";

    public static string Taskbar(string key, string? languageCode = null) =>
        languageCode != null
            ? LocalizationService.Translate(languageCode, key)
            : LocalizationService.Translate(key);

    internal static string FeatureTitle(string title)
    {
        const string prefix = "CyberSnap · ";
        return title.StartsWith(prefix, StringComparison.Ordinal) ? title[prefix.Length..] : title;
    }

    public static string EditorTitle(string? fileName, bool dirty)
    {
        return string.IsNullOrWhiteSpace(fileName)
            ? Taskbar(Editor)
            : $"CyberSnap · {fileName}{(dirty ? "*" : "")}";
    }

    public static void ApplyTaskbar(Window window, string key, string? languageCode = null)
    {
        LocalizationService.SetSourceText(window, key);
        window.Title = Taskbar(key, languageCode);
    }
}
