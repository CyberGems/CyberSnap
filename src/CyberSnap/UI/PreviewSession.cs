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
    private bool _disposed;
    private Bitmap? _outlinedBitmap;
    private Bitmap? _detachedBitmap;
    private int _revision;
    private int _copiedRevision;
    private int _savedRevision;
    private string? _savedFilePath;

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
        _copiedRevision = clipboardAlreadyCopied ? 0 : -1;
        CaptureKind = captureKind;
        Ordinal = ordinal;
    }

    public Bitmap Bitmap { get; }
    public Bitmap? ScaledBitmap { get; private set; }
    public int ScaleFactor { get; private set; } = 1;
    public bool OutlineEnabled { get; private set; }
    public int OutlineThickness { get; private set; } = 2;
    public int OutlineColorArgb { get; private set; } = unchecked((int)0xFF000000);
    public string? SavedFilePath
    {
        get => _savedFilePath;
        set
        {
            _savedFilePath = value;
            _savedRevision = value is null ? -1 : _revision;
        }
    }
    public string? CurrentSavedFilePath => _savedRevision == _revision ? SavedFilePath : null;
    public string? EarlySavePath { get; }
    public bool ClipboardAlreadyCopied => _copiedRevision == _revision;
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

    public Bitmap EffectiveBitmap => _outlinedBitmap ?? ScaledBitmap ?? Bitmap;
    public bool IsScaled => ScaleFactor != 1;
    public void MarkCopied() => _copiedRevision = _revision;

    public void SetOutline(bool enabled, int thickness, int colorArgb)
    {
        thickness = Math.Clamp(thickness, 1, CaptureOutlineRenderer.MaxThickness);
        colorArgb = System.Drawing.Color.FromArgb(255, System.Drawing.Color.FromArgb(colorArgb)).ToArgb();
        if (enabled == OutlineEnabled && thickness == OutlineThickness && colorArgb == OutlineColorArgb) return;
        var next = enabled
            ? CaptureOutlineRenderer.Render(ScaledBitmap ?? Bitmap, thickness, System.Drawing.Color.FromArgb(colorArgb))
            : null;
        bool pixelsChanged = enabled || OutlineEnabled;
        _outlinedBitmap?.Dispose();
        _outlinedBitmap = next;
        OutlineEnabled = enabled;
        OutlineThickness = thickness;
        OutlineColorArgb = colorArgb;
        if (pixelsChanged)
        {
            _revision++;
            PreviewSource = null;
        }
    }

    public void SetScaleFactor(int factor)
    {
        if (factor == ScaleFactor) return;
        Bitmap? scaled = factor == 1 ? null : ImageScaleService.Scale(Bitmap, factor);
        Bitmap? outlined;
        try
        {
            outlined = OutlineEnabled
                ? CaptureOutlineRenderer.Render(scaled ?? Bitmap, OutlineThickness, System.Drawing.Color.FromArgb(OutlineColorArgb))
                : null;
        }
        catch { scaled?.Dispose(); throw; }
        _outlinedBitmap?.Dispose();
        ScaledBitmap?.Dispose();
        ScaledBitmap = scaled;
        _outlinedBitmap = outlined;
        ScaleFactor = factor;
        _revision++;
        PreviewSource = null;
    }

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
    /// Caller takes ownership of the effective bitmap. Dispose still releases intermediate images.
    /// </summary>
    public void DetachOwnedBitmaps() => _detachedBitmap = EffectiveBitmap;

    public void Dispose()
    {
        PreviewSource = null;
        if (_disposed) return;
        _disposed = true;
        if (_outlinedBitmap != _detachedBitmap) _outlinedBitmap?.Dispose();
        if (ScaledBitmap != _detachedBitmap) ScaledBitmap?.Dispose();
        if (Bitmap != _detachedBitmap) Bitmap.Dispose();
        _outlinedBitmap = null;
        ScaledBitmap = null;
    }
}
