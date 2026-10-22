using System.Drawing;
using CyberSnap.Models;

namespace CyberSnap.Capture;

/// <summary>A borrowed frozen viewport. Its caller owns the frame until the modal capture returns.</summary>
public sealed class ZoomCaptureRequest
{
    public Bitmap Frame { get; }
    public Rectangle Bounds { get; }
    public bool FullView { get; }
    public CaptureMode SelectionMode { get; }
    public bool IsCommitted { get; private set; }
    private Action? _cancel;

    public ZoomCaptureRequest(Bitmap frame, Rectangle bounds, bool fullView, CaptureMode selectionMode)
    {
        Frame = frame;
        Bounds = bounds;
        FullView = fullView;
        SelectionMode = !fullView && selectionMode == CaptureMode.Center ? CaptureMode.Center : CaptureMode.Rectangle;
    }

    public void Commit() => IsCommitted = true;
    internal void ShowDialog(RegionOverlayForm overlay)
    {
        _cancel = overlay.CancelFromShortcut;
        if (FullView) overlay.Shown += (_, _) => overlay.BeginInvoke((Action)overlay.ConfirmFullFrame);
        try { overlay.ShowDialog(); }
        finally { _cancel = null; }
    }
    internal void Cancel() => _cancel?.Invoke();
}
