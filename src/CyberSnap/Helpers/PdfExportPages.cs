using PdfSharp.Pdf;

namespace CyberSnap.Helpers;

/// <summary>
/// The Span PDF export slices one image across several pages. PdfPage.Size's getter is obsolete,
/// so every extra page copies the first page's orientation and dimensions instead.
/// </summary>
public static class PdfExportPages
{
    /// <summary>Appends a page that matches the size and orientation of the document's first page.</summary>
    public static PdfPage AddLikeFirstPage(PdfDocument document)
    {
        var page = document.AddPage();
        var template = document.Pages[0];
        // Orientation first: PdfSharp swaps width and height when the orientation changes.
        page.Orientation = template.Orientation;
        page.Width = template.Width;
        page.Height = template.Height;
        return page;
    }
}
