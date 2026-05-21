using Xunit;

namespace CyberSnap.Tests;

public sealed class AppCapturePolishTests
{
    [Fact]
    public void CaptureTextCopyFailuresKeepScanAndColorFeedback()
    {
        var source = File.ReadAllText(RepoPath("src", "CyberSnap", "App", "App.Capture.cs"));

        var copyHelper = GetMethodBlock(source, "private static bool TryCopyCaptureTextToClipboard(string text)");
        Assert.Contains("ClipboardService.CopyTextToClipboard(text);", copyHelper);
        Assert.Contains("CyberSnap could not copy this capture result. The result will still be shown and saved when history is enabled.", copyHelper);
        Assert.Contains("return false;", copyHelper);

        Assert.Equal(1, CountOccurrences(source, "ClipboardService.CopyTextToClipboard("));

        var scanCopyIndex = source.IndexOf("var copySucceeded = TryCopyCaptureTextToClipboard(decoded.Text);", StringComparison.Ordinal);
        var scanHistoryIndex = source.IndexOf("_historyService?.SaveCodeEntry(decoded.Text, decoded.Format.ToString());", scanCopyIndex, StringComparison.Ordinal);
        var qrFoundIndex = source.IndexOf("\"QR Code found\"", scanCopyIndex, StringComparison.Ordinal);
        var barcodeFoundIndex = source.IndexOf("\"Barcode found\"", scanCopyIndex, StringComparison.Ordinal);
        var scanPreviewIndex = source.IndexOf("ToastWindow.ShowInlinePreview(preview, title, prev, suppressSound: true);", scanCopyIndex, StringComparison.Ordinal);

        Assert.True(scanCopyIndex >= 0, "QR/barcode scan should use guarded text copy.");
        Assert.True(scanHistoryIndex > scanCopyIndex, "QR/barcode scan history should still save after copy failures.");
        Assert.True(qrFoundIndex > scanCopyIndex, "QR fallback title should avoid claiming copied.");
        Assert.True(barcodeFoundIndex > scanCopyIndex, "Barcode fallback title should avoid claiming copied.");
        Assert.True(scanPreviewIndex > scanCopyIndex, "QR/barcode scan should still show the decoded preview after copy failures.");

        var colorCopyIndex = source.IndexOf("var copySucceeded = TryCopyCaptureTextToClipboard(bare);", StringComparison.Ordinal);
        var colorPickedIndex = source.IndexOf("copySucceeded ? \"Color copied\" : \"Color picked\"", colorCopyIndex, StringComparison.Ordinal);
        var colorHistoryIndex = source.IndexOf("EnsureHistoryService().SaveColorEntry(bare);", colorCopyIndex, StringComparison.Ordinal);

        Assert.True(colorCopyIndex >= 0, "Color picker should use guarded text copy.");
        Assert.True(colorPickedIndex > colorCopyIndex, "Color picker should avoid claiming copied after copy failures.");
        Assert.True(colorHistoryIndex > colorCopyIndex, "Color history should still save after copy failures.");
    }

    [Fact]
    public void OcrAutoCopySkipsResultWindowWhenClipboardCopySucceeds()
    {
        var source = File.ReadAllText(RepoPath("src", "CyberSnap", "App", "App.Capture.Handlers.cs"));
        var ocrBlock = GetMethodBlock(source, "private void HandleOcrResult(Bitmap result)");
        var settingIndex = ocrBlock.IndexOf("_settingsService.Settings.OcrAutoCopyToClipboard", StringComparison.Ordinal);
        var copyIndex = ocrBlock.IndexOf("var copied = TryCopyCaptureTextToClipboard(text);", settingIndex, StringComparison.Ordinal);
        var toastIndex = ocrBlock.IndexOf("ToastSpec.Standard(\"OCR copied\", FormatOcrAutoCopyToastPreview(text))", copyIndex, StringComparison.Ordinal);
        var fallbackIndex = ocrBlock.IndexOf("if (!copied)", toastIndex, StringComparison.Ordinal);
        var normalWindowIndex = ocrBlock.IndexOf("else", fallbackIndex, StringComparison.Ordinal);

        Assert.True(settingIndex >= 0, "OCR should check the auto-copy setting.");
        Assert.True(copyIndex > settingIndex, "Auto-copy OCR should copy recognized text.");
        Assert.True(toastIndex > copyIndex, "Auto-copy OCR should report copy status.");
        Assert.True(fallbackIndex > toastIndex, "Auto-copy OCR should only open a result window when copy fails.");
        Assert.True(normalWindowIndex > fallbackIndex, "Normal OCR should still open the result window.");

        var previewBlock = GetMethodBlock(source, "private static string FormatOcrAutoCopyToastPreview(string text)");
        Assert.Contains("StringSplitOptions.RemoveEmptyEntries", previewBlock);
        Assert.Contains("preview.Length > 80", previewBlock);
    }

    private static void AssertCopySuccessControlsReadyToast(string methodBlock, string copiedText, string readyText)
    {
        Assert.Contains("var copyRequested = ShouldCopyAfterCapture(action);", methodBlock);
        Assert.Contains("var copySucceeded = copyRequested && TryCopyCaptureOutputToClipboard(persisted.Output);", methodBlock);
        Assert.Contains($"ToastWindow.Show(copySucceeded ? \"{copiedText}\" : \"{readyText}\");", methodBlock);

        var copyIndex = methodBlock.IndexOf("TryCopyCaptureOutputToClipboard(persisted.Output);", StringComparison.Ordinal);
        var resetIndex = methodBlock.IndexOf("ResetCapturing();", copyIndex, StringComparison.Ordinal);
        Assert.True(resetIndex > copyIndex, $"{copiedText} flow should reset even after copy failures.");
    }

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string GetMethodBlock(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method: {signature}");

        var bodyStart = source.IndexOf('{', start);
        Assert.True(bodyStart > start, $"Could not find method body: {signature}");

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException($"Could not read method body: {signature}");
    }

    private static string RepoPath(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find repo file: {Path.Combine(parts)}");
    }
}
