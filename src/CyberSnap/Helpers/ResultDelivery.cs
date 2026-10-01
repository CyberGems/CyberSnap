using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Helpers;

/// <summary>
/// Copy-to-clipboard and the result window are independent. At least one stays on:
/// turning the last one off brings the window back.
/// </summary>
public static class ResultDelivery
{
    public const int SchemaVersion = 1;

    public readonly record struct Plan(bool Copy, bool ShowWindow);

    /// <summary>
    /// One-time map from the old exclusive behavior: auto-copy on meant the window
    /// was skipped. Run after <see cref="AutoCopyPreferences.MigrateIfNeeded"/>.
    /// </summary>
    public static void MigrateIfNeeded(AppSettings settings)
    {
        if (settings.ResultDeliverySchemaVersion >= SchemaVersion)
            return;

        settings.OcrShowResultWindow = !AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Ocr);
        settings.ScanShowResultWindow = !AutoCopyPreferences.ShouldCopy(settings, AutoCopyKind.Scan);
        settings.ResultDeliverySchemaVersion = SchemaVersion;
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
