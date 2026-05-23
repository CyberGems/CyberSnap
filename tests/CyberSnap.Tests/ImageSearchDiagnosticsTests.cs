using Xunit;
using CyberSnap.Services;
using System;
using System.Reflection;

namespace CyberSnap.Tests;

public sealed class ImageSearchDiagnosticsTests
{
    private static string CallBuildDiagnosticsText(ImageSearchIndexRecord? record, string fallbackFileName)
    {
        var method = typeof(ImageSearchIndexService).GetMethod(
            "BuildDiagnosticsText",
            BindingFlags.NonPublic | BindingFlags.Static);
        
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object?[] { record, fallbackFileName })!;
    }

    [Fact]
    public void BuildDiagnosticsText_WithNullRecord_ReturnsPendingIndex()
    {
        var result = CallBuildDiagnosticsText(null, "test.png");
        Assert.Contains("Status: Pending index", result);
        Assert.Contains("File: test.png", result);
    }

    [Fact]
    public void BuildDiagnosticsText_WithRecord_FormatsCleanlyAndCollapsesWhitespace()
    {
        var record = new ImageSearchIndexRecord
        {
            FilePath = "C:\\test.png",
            IndexedAt = new DateTime(2026, 5, 21, 10, 30, 0, DateTimeKind.Utc),
            OcrState = ImageSearchOcrState.Indexed,
            OcrText = "C CyberSnap\n\nX\nHistory\tSettings"
        };

        var result = CallBuildDiagnosticsText(record, "test.png");

        Assert.Contains("Status: OCR ready", result);
        Assert.Contains("Indexed:", result);
        Assert.Contains("Text detected:", result);
        Assert.Contains("\"C CyberSnap X History Settings\"", result);
        
        // Verify that there are no raw newlines within the OCR text portion, only the section separators.
        Assert.DoesNotContain("C CyberSnap\n\nX", result);
    }

    [Fact]
    public void BuildDiagnosticsText_TruncatesOcrText()
    {
        var longText = new string('A', 300);
        var record = new ImageSearchIndexRecord
        {
            FilePath = "C:\\test.png",
            IndexedAt = DateTime.UtcNow,
            OcrState = ImageSearchOcrState.Indexed,
            OcrText = longText
        };

        var result = CallBuildDiagnosticsText(record, "test.png");
        Assert.Contains("...", result);
        Assert.Contains(new string('A', 217) + "...", result);
    }
}
