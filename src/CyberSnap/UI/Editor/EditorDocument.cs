using System.IO;
using CyberSnap.Services;
using CyberSnap.UI.Controls;

namespace CyberSnap.UI.Editor;

/// <summary>
/// One open editor document (canvas + save path). The form keeps a list of these
/// and shows a tab strip only when more than one is open.
/// </summary>
internal sealed class EditorDocument : IDisposable
{
    public EditorDocument(AnnotationCanvas canvas, string? savedFilePath)
    {
        Canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        SavedFilePath = savedFilePath;
        ResetScaleBaseline();
    }

    public AnnotationCanvas Canvas { get; }

    public string? SavedFilePath { get; set; }

    /// <summary>Pixel size of the document at 1×. 2× and 4× are measured from this, not from the current bitmap.</summary>
    public int ScaleBaseWidth { get; set; }

    public int ScaleBaseHeight { get; set; }

    public int ScaleFactor { get; set; } = 1;

    public void ResetScaleBaseline()
    {
        if (Canvas.IsDisposed) return;
        ScaleBaseWidth = Canvas.BaseBitmap.Width;
        ScaleBaseHeight = Canvas.BaseBitmap.Height;
        ScaleFactor = 1;
    }

    /// <summary>
    /// Keeps 1×/2×/4× in step with the bitmap. Undo and a manual resize land back on 1×
    /// when the size is no longer an exact multiple of the stored original.
    /// </summary>
    public void SyncScaleFactorFromSize()
    {
        if (Canvas.IsDisposed || ScaleBaseWidth <= 0 || ScaleBaseHeight <= 0)
            return;

        int width = Canvas.BaseBitmap.Width;
        int height = Canvas.BaseBitmap.Height;
        if (width == ScaleBaseWidth * 4 && height == ScaleBaseHeight * 4)
            ScaleFactor = 4;
        else if (width == ScaleBaseWidth * 2 && height == ScaleBaseHeight * 2)
            ScaleFactor = 2;
        else if (width == ScaleBaseWidth && height == ScaleBaseHeight)
            ScaleFactor = 1;
        else
        {
            ScaleBaseWidth = width;
            ScaleBaseHeight = height;
            ScaleFactor = 1;
        }
    }

    public bool IsDirty => Canvas is { IsDisposed: false, IsDirty: true, IsDefaultBlank: false };

    public string TabTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SavedFilePath))
                return Path.GetFileName(SavedFilePath);
            return LocalizationService.Translate("Untitled");
        }
    }

    public void Dispose()
    {
        if (!Canvas.IsDisposed)
            Canvas.Dispose();
    }
}
