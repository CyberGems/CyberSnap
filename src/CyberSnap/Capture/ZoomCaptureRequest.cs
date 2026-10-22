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
    private readonly StandaloneZoomForm? _backdrop;

    public ZoomCaptureRequest(Bitmap frame, Rectangle bounds, bool fullView, CaptureMode selectionMode)
    {
        Frame = frame;
        Bounds = bounds;
        FullView = fullView;
        SelectionMode = !fullView && selectionMode == CaptureMode.Center ? CaptureMode.Center : CaptureMode.Rectangle;
    }

    internal ZoomCaptureRequest(Bitmap frame, Rectangle bounds, bool fullView, CaptureMode selectionMode,
        StandaloneZoomForm backdrop) : this(frame, bounds, fullView, selectionMode) => _backdrop = backdrop;

    public void Commit()
    {
        IsCommitted = true;
        // Live-tool handoffs can start on the app dispatcher before Zoom's modal loop unwinds.
        // Remove the backdrop now so their screenshots cannot include the magnified frame.
        _backdrop?.Hide();
    }
    internal void ShowDialog(RegionOverlayForm overlay)
    {
        _cancel = overlay.CancelFromShortcut;
        Native.Dwm.TrySetTransitionsForcedDisabled(overlay.Handle, true);
        if (FullView) overlay.Shown += (_, _) => overlay.BeginInvoke((Action)overlay.ConfirmFullFrame);
        try { overlay.ShowDialog(_backdrop); }
        finally { _cancel = null; }
    }
    internal void Cancel() => _cancel?.Invoke();
}
