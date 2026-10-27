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
    private Bitmap? _framedBitmap;
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
    public bool MarginEnabled { get; private set; }
    public int MarginSize { get; private set; } = 16;
    public int MarginColorArgb { get; private set; }
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

    public Bitmap EffectiveBitmap => _framedBitmap ?? ScaledBitmap ?? Bitmap;
    public bool IsScaled => ScaleFactor != 1;
    public void MarkCopied() => _copiedRevision = _revision;

    public void SetOutline(bool enabled, int thickness, int colorArgb)
    {
        thickness = Math.Clamp(thickness, 1, CaptureOutlineRenderer.MaxThickness);
        colorArgb = System.Drawing.Color.FromArgb(255, System.Drawing.Color.FromArgb(colorArgb)).ToArgb();
        if (enabled == OutlineEnabled && thickness == OutlineThickness && colorArgb == OutlineColorArgb) return;
        bool pixelsChanged = enabled || OutlineEnabled;
        if (pixelsChanged)
            ReplaceFrame(RenderFrame(ScaledBitmap ?? Bitmap, enabled, thickness, colorArgb, MarginEnabled, MarginSize, MarginColorArgb), true);
        OutlineEnabled = enabled;
        OutlineThickness = thickness;
        OutlineColorArgb = colorArgb;
    }

    public void SetMargin(bool enabled, int size, int colorArgb)
    {
        size = Math.Clamp(size, 1, CaptureFrameRenderer.MaxMargin);
        colorArgb = System.Drawing.Color.FromArgb(255, System.Drawing.Color.FromArgb(colorArgb)).ToArgb();
        if (enabled == MarginEnabled && size == MarginSize && colorArgb == MarginColorArgb) return;
        if (enabled || MarginEnabled)
            ReplaceFrame(RenderFrame(ScaledBitmap ?? Bitmap, OutlineEnabled, OutlineThickness, OutlineColorArgb, enabled, size, colorArgb), true);
        MarginEnabled = enabled;
        MarginSize = size;
        MarginColorArgb = colorArgb;
    }

    // The preview background is shared by tabs. Prepare all new frames before
    // replacing any, so an allocation failure cannot leave mixed margin colors.
    public static void SynchronizeMarginColor(IEnumerable<PreviewSession> sessions, int colorArgb)
    {
        colorArgb = System.Drawing.Color.FromArgb(255, System.Drawing.Color.FromArgb(colorArgb)).ToArgb();
        var pending = new List<(PreviewSession Session, Bitmap? Frame)>();
        try
        {
            foreach (var session in sessions)
            {
                if (session.MarginColorArgb == colorArgb) continue;
                var frame = session.MarginEnabled
                    ? RenderFrame(session.ScaledBitmap ?? session.Bitmap, session.OutlineEnabled, session.OutlineThickness,
                        session.OutlineColorArgb, true, session.MarginSize, colorArgb)
                    : null;
                pending.Add((session, frame));
            }
        }
        catch
        {
            foreach (var item in pending) item.Frame?.Dispose();
            throw;
        }
        foreach (var item in pending)
        {
            if (item.Session.MarginEnabled) item.Session.ReplaceFrame(item.Frame, true);
            item.Session.MarginColorArgb = colorArgb;
        }
    }

    private static Bitmap? RenderFrame(Bitmap source, bool outlineEnabled, int outline, int outlineColor,
        bool marginEnabled, int margin, int marginColor) => marginEnabled
            ? CaptureFrameRenderer.Render(source, outlineEnabled ? outline : 0, System.Drawing.Color.FromArgb(outlineColor),
                margin, System.Drawing.Color.FromArgb(marginColor))
            : outlineEnabled ? CaptureOutlineRenderer.Render(source, outline, System.Drawing.Color.FromArgb(outlineColor)) : null;

    private void ReplaceFrame(Bitmap? frame, bool pixelsChanged)
    {
        _framedBitmap?.Dispose();
        _framedBitmap = frame;
        if (!pixelsChanged) return;
        _revision++;
        PreviewSource = null;
    }

    public void SetScaleFactor(int factor)
    {
        if (factor == ScaleFactor) return;
        if (!CanScaleFactor(factor))
            throw new InvalidOperationException("The scaled image, margin and outline would exceed the image size limit.");
        Bitmap? scaled = factor == 1 ? null : ImageScaleService.Scale(Bitmap, factor);
        Bitmap? outlined;
        try
        {
            outlined = RenderFrame(scaled ?? Bitmap, OutlineEnabled, OutlineThickness, OutlineColorArgb,
                MarginEnabled, MarginSize, MarginColorArgb);
        }
        catch { scaled?.Dispose(); throw; }
        _framedBitmap?.Dispose();
        ScaledBitmap?.Dispose();
        ScaledBitmap = scaled;
        _framedBitmap = outlined;
        ScaleFactor = factor;
        _revision++;
        PreviewSource = null;
    }

    public bool CanScaleFactor(int factor)
    {
        if (!ImageScaleService.TryGetScaledSize(Bitmap.Width, Bitmap.Height, factor, out int width, out int height, out _))
            return factor == 1 && !OutlineEnabled && !MarginEnabled;
        return (!OutlineEnabled && !MarginEnabled) || CaptureFrameRenderer.TryGetSize(width, height,
            OutlineEnabled ? OutlineThickness : 0, MarginEnabled ? MarginSize : 0, out _);
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
        if (_framedBitmap != _detachedBitmap) _framedBitmap?.Dispose();
        if (ScaledBitmap != _detachedBitmap) ScaledBitmap?.Dispose();
        if (Bitmap != _detachedBitmap) Bitmap.Dispose();
        _framedBitmap = null;
        ScaledBitmap = null;
    }
}
