using PdfSharp;
using PdfSharp.Pdf;
using CyberSnap.Helpers;

internal static partial class Program
{
    // The Span layout slices a tall image across pages. Every page after the first must keep the
    // size and orientation chosen for page one, in both orientations and for each page size.
    private static void CheckPdfSpanPageSizing()
    {
        // PDFsharp 6.5 refuses to create a document until a license has been chosen. The test
        // harness only builds throwaway documents in memory, so it selects the free AGPL license.
        PdfSharp.PdfSharpLicense.SetAgplLicense();
        foreach (var (label, size, orientation, slices) in new[]
        {
            ("A4 portrait", PageSize.A4, PageOrientation.Portrait, 4),
            ("A4 landscape", PageSize.A4, PageOrientation.Landscape, 3),
            ("Letter landscape", PageSize.Letter, PageOrientation.Landscape, 5),
            ("A3 portrait", PageSize.A3, PageOrientation.Portrait, 2),
        })
        {
            using var document = new PdfDocument();
            var first = document.AddPage();
            first.Size = size;
            first.Orientation = orientation;
            double width = first.Width.Point, height = first.Height.Point;

            for (int slice = 1; slice < slices; slice++) PdfExportPages.AddLikeFirstPage(document);

            Assert(document.PageCount == slices, $"{label}: the span export adds one page per slice");
            for (int index = 1; index < document.PageCount; index++)
            {
                var page = document.Pages[index];
                Assert(page.Orientation == first.Orientation
                    && Math.Abs(page.Width.Point - width) < 0.01
                    && Math.Abs(page.Height.Point - height) < 0.01,
                    $"{label}: page {index + 1} keeps the first page's {width:0.##}x{height:0.##} pt {first.Orientation} size");
            }
        }
    }
}
