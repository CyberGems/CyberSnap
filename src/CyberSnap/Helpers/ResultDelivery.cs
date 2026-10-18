using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

/// <summary>
/// Copy-to-clipboard and the result window are independent. At least one stays on:
/// turning the last one off brings the window back.
/// </summary>
public static class ResultDelivery
{
    public readonly record struct Plan(bool Copy, bool ShowWindow);

    public static void SetCopyEnabled(AppSettings settings, AutoCopyKind kind, bool enabled)
    {
        AutoCopyPreferences.SetKindEnabled(settings, kind, enabled);
        if (!enabled)
        {
            switch (kind)
            {
                case AutoCopyKind.Ocr: settings.OcrShowResultWindow = true; break;
                case AutoCopyKind.Scan: settings.ScanShowResultWindow = true; break;
                case AutoCopyKind.Color: settings.ShowColorDetailWindow = true; break;
            }
        }
    }

    public static void SetShowWindow(AppSettings settings, AutoCopyKind kind, bool show)
    {
        switch (kind)
        {
            case AutoCopyKind.Ocr: settings.OcrShowResultWindow = show; break;
            case AutoCopyKind.Scan: settings.ScanShowResultWindow = show; break;
            case AutoCopyKind.Color: settings.ShowColorDetailWindow = show; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (!show)
            AutoCopyPreferences.SetKindEnabled(settings, kind, true);
    }

    public static Plan Normalize(bool copy, bool showWindow)
    {
        if (!copy && !showWindow)
            showWindow = true;
        return new Plan(copy, showWindow);
    }

    public static Plan ForOcr(AppSettings settings) =>
        Normalize(AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Ocr), settings.OcrShowResultWindow);

    public static Plan ForScan(AppSettings settings) =>
        Normalize(AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Scan), settings.ScanShowResultWindow);

    public static Plan ForColor(AppSettings settings) =>
        Normalize(settings.ColorDetailAutoCopy, settings.ShowColorDetailWindow);

    public static bool TryCopyText(string text)
    {
        try
        {
            ClipboardService.CopyTextToClipboard(text);
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("result.copy", ex.Message, ex);
            return false;
        }
    }

    /// <summary>True when the text now on screen is the text that was copied.</summary>
    public static bool MatchesCopied(string? current, string? copied) =>
        copied is not null && string.Equals(current, copied, StringComparison.Ordinal);
}
