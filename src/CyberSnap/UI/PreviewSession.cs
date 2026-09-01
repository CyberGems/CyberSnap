using System.Drawing;
using System.IO;
using System.Windows.Media;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

/// <summary>
/// One capture sitting in the preview window. The dialog keeps a list of these
/// and shows a tab strip only when more than one is open.
/// </summary>
internal sealed class PreviewSession : IDisposable
{
    private bool _ownsBitmaps = true;

    public PreviewSession(
        Bitmap bitmap,
        string? savedFilePath,
        bool clipboardAlreadyCopied,
        CaptureKind captureKind,
        int ordinal)
    {
        Bitmap = bitmap ?? throw new ArgumentNullException(nameof(bitmap));
        SavedFilePath = string.IsNullOrWhiteSpace(savedFilePath) ? null : savedFilePath;
        EarlySavePath = SavedFilePath;
        ClipboardAlreadyCopied = clipboardAlreadyCopied;
        CaptureKind = captureKind;
        Ordinal = ordinal;
    }

    public Bitmap Bitmap { get; }
    public Bitmap? ScaledBitmap { get; set; }
    public int ScaleFactor { get; set; } = 1;
    public string? SavedFilePath { get; set; }
    public string? EarlySavePath { get; }
    public bool ClipboardAlreadyCopied { get; }
    public CaptureKind CaptureKind { get; }
    public int Ordinal { get; }
    public RegionOverlayForm.ConfirmCommitAction SelectedAction { get; set; } =
        RegionOverlayForm.ConfirmCommitAction.Default;

    public ImageSource? PreviewSource { get; set; }

    public double Zoom { get; set; } = 1.0;
    public bool ZoomToFit { get; set; }
    public bool DidInitialContain { get; set; }
    public double PanHorizontal { get; set; }
    public double PanVertical { get; set; }

    public Bitmap EffectiveBitmap => ScaledBitmap ?? Bitmap;
    public bool IsScaled => ScaleFactor != 1;

    public string TabTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SavedFilePath))
                return Path.GetFileName(SavedFilePath);
            return string.Format(LocalizationService.Translate("Capture {0}"), Ordinal);
        }
    }

    /// <summary>
    /// Caller takes ownership of the bitmaps (commit hands them to
    /// <c>HandleCaptureResult</c>; discard disposes them itself).
    /// </summary>
    public void DetachOwnedBitmaps() => _ownsBitmaps = false;

    public void Dispose()
    {
        PreviewSource = null;
        if (!_ownsBitmaps)
            return;

        if (ScaledBitmap != null)
        {
            try { ScaledBitmap.Dispose(); } catch { }
            ScaledBitmap = null;
        }

        try { Bitmap.Dispose(); } catch { }
        _ownsBitmaps = false;
    }
}
