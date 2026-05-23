using Xunit;
using Xunit.Abstractions;
using CyberSnap.Services;

namespace CyberSnap.Tests;

public sealed class OcrServiceTests
{
    private readonly ITestOutputHelper _output;

    public OcrServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void FormatRecognizedText_PreservesLinesAndParagraphBreaks()
    {
        var lines = new[]
        {
            new OcrService.OcrLineLayout("First line", 10, 0, 90, 12),
            new OcrService.OcrLineLayout("Second line", 10, 18, 110, 30),
            new OcrService.OcrLineLayout("New paragraph", 10, 54, 122, 66),
            new OcrService.OcrLineLayout("Continues here", 10, 72, 128, 84),
        };

        var text = OcrService.FormatRecognizedText(lines);

        Assert.Equal(
            $"First line{Environment.NewLine}Second line{Environment.NewLine}{Environment.NewLine}New paragraph{Environment.NewLine}Continues here",
            text);
    }

    [Fact]
    public void FormatRecognizedText_AddsLeadingSpacesForIndentedParagraphStarts()
    {
        var lines = new[]
        {
            new OcrService.OcrLineLayout("Heading", 10, 0, 80, 12),
            new OcrService.OcrLineLayout("Indented paragraph", 40, 32, 210, 44),
            new OcrService.OcrLineLayout("Wrapped line", 10, 50, 104, 62),
        };

        var text = OcrService.FormatRecognizedText(lines);

        Assert.Equal(
            $"Heading{Environment.NewLine}{Environment.NewLine}   Indented paragraph{Environment.NewLine}Wrapped line",
            text);
    }

    [Fact]
    public void FormatRecognizedText_FallsBackWhenNoLineLayoutsExist()
    {
        var text = OcrService.FormatRecognizedText(Array.Empty<OcrService.OcrLineLayout>(), "  plain text  ");

        Assert.Equal("plain text", text);
    }

    [Fact]
    public async Task RecognizeAsync_ClockCaptureTest()
    {
        string path = @"C:\Users\CARLOS\.gemini\antigravity-ide\brain\d52f57b7-727d-470a-92b4-7c8f44756e3b\media__1779501094171.png";
        if (!System.IO.File.Exists(path))
        {
            path = @"C:\Users\CARLOS\.gemini\antigravity-ide\brain\d52f57b7-727d-470a-92b4-7c8f44756e3b\media__1779500871274.png";
        }
        if (System.IO.File.Exists(path))
        {
            using (var bmp = new System.Drawing.Bitmap(path))
            {
                var textFull = await OcrService.RecognizeAsync(bmp);
                _output.WriteLine($"OcrService.RecognizeAsync Result: [{textFull}]");
                Assert.Contains("07:24:53", textFull);
                Assert.Contains("mayo 22, 2026", textFull);
            }
        }
        else
        {
            _output.WriteLine("TEST_OCR_OUTPUT: File not found");
        }
    }
}
