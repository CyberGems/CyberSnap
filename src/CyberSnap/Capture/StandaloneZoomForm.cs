using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>A single-monitor frozen desktop. Capture output is rendered without the HUD or selection chrome.</summary>
public sealed class StandaloneZoomForm : Form
{
    private readonly Bitmap _original;
    private readonly ZoomViewport _viewport;
    private readonly bool _smooth, _animate, _includeCursor, _showHelpBar;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ZoomHelpPanel? _helpPanel;
    private (Size Size, int Dpi, double Scale, string Level)? _helpCacheKey;
    private readonly Rectangle _screenBounds;
    private ZoomRenderer? _renderer;
    private bool _rendererUnavailable;
    private volatile bool _capturePending;
    private ZoomCaptureRequest? _activeCapture;
    private Point? _resumePointer;
    private bool _waitForEscapeRelease;
    private double _lastTick;
    private bool _completed;
    private bool _closing, _ownsPointerClip;
    private Rectangle _previousPointerClip;

    public event Action<ZoomCaptureRequest>? CaptureRequested;
    public bool CapturePending => _capturePending;
    public bool CaptureOutputDelegated { get; private set; }
    public Bitmap? Result { get; private set; }
    public RegionOverlayForm.ConfirmCommitAction CommitAction { get; private set; }
    public Point MonitorPoint => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);

    // Ownership of frozenFrame transfers to the form. The injectable frame also allows deterministic QA.
    public StandaloneZoomForm(Bitmap frozenFrame, Rectangle screenBounds, Point screenPointer, AppSettings settings)
    {
        _original = frozenFrame;
        _screenBounds = screenBounds;
        _smooth = settings.ZoomSmoothImage;
        _animate = settings.ZoomAnimate && System.Windows.SystemParameters.ClientAreaAnimation;
        _includeCursor = settings.ShowCursor;
        _showHelpBar = settings.ZoomShowHelpBar;
        _viewport = new ZoomViewport(frozenFrame.Size,
            new Point(screenPointer.X - screenBounds.X, screenPointer.Y - screenBounds.Y), settings.ZoomInitialLevel, _animate);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        Text = LocalizationService.Translate("Zoom");
        AccessibleName = Text;
        Cursor = Cursors.Default;
        _timer.Tick += (_, _) =>
        {
            double now = _clock.Elapsed.TotalSeconds;
            UpdatePointer(PointToClient(System.Windows.Forms.Cursor.Position));
            if (_waitForEscapeRelease && (GetAsyncKeyState((int)Keys.Escape) & 0x8000) == 0) _waitForEscapeRelease = false;
            if (_viewport.Advance(now - _lastTick, _animate)) Invalidate();
            _lastTick = now;
        };
        // A monitor layout/DPI change invalidates the frozen screen geometry. Cancel rather than stretch it.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _screenBounds;
        Activate();
        Focus();
        ResumeInput();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.Dwm.TrySetTransitionsForcedDisabled(Handle, true);
    }

    private void ResumeInput()
    {
        if (_ownsPointerClip) return;
        _previousPointerClip = System.Windows.Forms.Cursor.Clip;
        System.Windows.Forms.Cursor.Clip = Bounds;
        _ownsPointerClip = true;
        _lastTick = _clock.Elapsed.TotalSeconds;
        _timer.Start();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_ownsPointerClip && !_closing && !_capturePending) Close();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_ownsPointerClip && !_closing && !_capturePending) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        if (_activeCapture is { IsCommitted: false }) _activeCapture.Cancel();
        ReleasePointer();
        base.OnFormClosing(e);
    }

    private void ReleasePointer()
    {
        if (!_ownsPointerClip) return;
        _ownsPointerClip = false;
        if (System.Windows.Forms.Cursor.Clip == _screenBounds) System.Windows.Forms.Cursor.Clip = _previousPointerClip;
    }

    private void DisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)(() => { if (!IsDisposed) Close(); }));
        }
        catch (InvalidOperationException) when (Disposing || IsDisposed || !IsHandleCreated) { }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (EnsureRenderer())
        {
            try { _renderer!.Paint(_viewport, _smooth, DrawGpuChrome); return; }
            catch (Exception ex) { DiscardRenderer(ex); }
        }
        _viewport.Draw(e.Graphics, _original, _smooth);
        DrawGdiChrome(e.Graphics);
        base.OnPaint(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    private bool EnsureRenderer()
    {
        if (_rendererUnavailable) return false;
        if (_renderer is not null) return true;
        try { _renderer = new ZoomRenderer(Handle, _original); }
        catch (Exception ex) { DiscardRenderer(ex); }
        return _renderer is not null;
    }

    private void DiscardRenderer(Exception ex)
    {
        AppDiagnostics.LogWarning("zoom.renderer-fallback", ex.Message, ex);
        _renderer?.Dispose();
        _renderer = null;
        _rendererUnavailable = true;
        DoubleBuffered = true;
    }

    /// <summary>Read the same rendered surface shown on screen, optionally including the help chrome.</summary>
    public Bitmap RenderView(bool includeChrome = false)
    {
        if (EnsureRenderer())
        {
            try { return _renderer!.Snapshot(_viewport, _smooth, includeChrome ? DrawGpuChrome : null); }
            catch (Exception ex) { DiscardRenderer(ex); }
        }
        var result = _viewport.Render(_original, _smooth);
        if (includeChrome)
        {
            using var graphics = Graphics.FromImage(result);
            DrawGdiChrome(graphics);
        }
        return result;
    }

    private void DrawGdiChrome(Graphics graphics) => DrawHud(graphics);

    private void DrawGpuChrome(ZoomRenderer renderer)
    {
        if (_capturePending || !_showHelpBar) return;
        var bounds = HudLayout();
        renderer.DrawHelp(_helpPanel!.Image, bounds.Location);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private Rectangle HudLayout()
    {
        // WinForms can retain system DPI until a visible DPI-change message arrives.
        // The positioned HWND is authoritative, including the first frame on another monitor.
        int dpi = (int)GetDpiForWindow(Handle);
        if (dpi <= 0) dpi = DeviceDpi;
        var key = (ClientSize, dpi, UiChrome.UiScale, $"{_viewport.Zoom:0.##}×");
        if (_helpCacheKey != key)
        {
            var panel = new ZoomHelpPanel(ClientSize, dpi / 96f * (float)UiChrome.UiScale, key.Item4);
            _helpPanel?.Dispose();
            _helpPanel = panel;
            _helpCacheKey = key;
        }
        int margin = Math.Max(8, (int)(12 * dpi / 96f));
        var size = _helpPanel!.Image.Size;
        margin = Math.Min(margin, Math.Max(0, (ClientSize.Height - size.Height) / 2));
        var bounds = new Rectangle((ClientSize.Width - size.Width) / 2, ClientSize.Height - size.Height - margin, size.Width, size.Height);
        if (bounds.Contains(PointToClient(System.Windows.Forms.Cursor.Position))) bounds.Y = margin;
        return bounds;
    }

    private void DrawHud(Graphics graphics)
    {
        if (_capturePending || !_showHelpBar) return;
        var bounds = HudLayout();
        graphics.DrawImageUnscaled(_helpPanel!.Image, bounds.Location);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        _viewport.ChangeZoom(e.Delta / 120d, _animate);
        Invalidate();
        base.OnMouseWheel(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (_capturePending || _closing) return;
        if (e.KeyCode == Keys.Escape) { if (!_waitForEscapeRelease) Close(); }
        else if (e.Control && e.KeyCode is Keys.C or Keys.S)
            Complete(new Rectangle(Point.Empty, _viewport.Size), e.KeyCode == Keys.C
                ? RegionOverlayForm.ConfirmCommitAction.Copy : RegionOverlayForm.ConfirmCommitAction.Save);
        else if (e.KeyCode == Keys.C) BeginCapture(false);
        else if (e.KeyCode == Keys.Enter)
            BeginCapture(true);
        else if (e.KeyCode is Keys.Add or Keys.Oemplus or Keys.Up) _viewport.ChangeZoom(1, _animate);
        else if (e.KeyCode is Keys.Subtract or Keys.OemMinus or Keys.Down) _viewport.ChangeZoom(-1, _animate);
        else { e.Handled = false; e.SuppressKeyPress = false; }
        Invalidate();
        base.OnKeyDown(e);
    }

    /// <summary>Temporarily lends the clean viewport to the regular modal capture flow.</summary>
    public void BeginCapture(bool fullView, CaptureMode selectionMode = CaptureMode.Rectangle)
    {
        if (_capturePending || _completed || _closing) return;
        _capturePending = true;
        _viewport.BeginSelection();
        _timer.Stop();
        ReleasePointer();
        try
        {
            using var frame = RenderView();
            if (_includeCursor) ScreenCapture.DrawCursor(frame, _screenBounds);
            var request = new ZoomCaptureRequest(frame, _screenBounds, fullView, selectionMode, this);
            _activeCapture = request;
            // Keep the frozen viewport underneath the modal overlay. Hiding it here exposed
            // the unscaled desktop while the capture window and its first frame were built.
            Refresh();
            CaptureRequested?.Invoke(request);
            if (request.IsCommitted)
            {
                CaptureOutputDelegated = true;
                _completed = true;
                Close();
            }
        }
        finally
        {
            _activeCapture = null;
            _capturePending = false;
            if (!_closing && !IsDisposed)
            {
                _viewport.EndSelection();
                _resumePointer = PointToClient(System.Windows.Forms.Cursor.Position);
                _waitForEscapeRelease = (GetAsyncKeyState((int)Keys.Escape) & 0x8000) != 0;
                Show();
                Bounds = _screenBounds;
                Activate();
                Focus();
                ResumeInput();
                _resumePointer = PointToClient(System.Windows.Forms.Cursor.Position);
                Invalidate();
            }
        }
    }

    private void UpdatePointer(Point point)
    {
        // Moving the pointer to select/annotate must not pan the restored viewport on cancellation.
        if (_resumePointer == point) return;
        _resumePointer = null;
        _viewport.SetPointer(point);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && !_capturePending) { Close(); return; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        UpdatePointer(e.Location);
        base.OnMouseMove(e);
    }

    private void Complete(Rectangle region, RegionOverlayForm.ConfirmCommitAction action = RegionOverlayForm.ConfirmCommitAction.Default)
    {
        region.Intersect(new Rectangle(Point.Empty, _viewport.Size));
        if (_completed || _capturePending || _closing || region.Width < 2 || region.Height < 2) return;
        using var view = RenderView();
        if (_includeCursor && !_viewport.IsSelecting) ScreenCapture.DrawCursor(view, Bounds);
        Result = view.Clone(region, PixelFormat.Format32bppArgb);
        CommitAction = action;
        _completed = true;
        Close();
    }

    public Bitmap? TakeResult()
    {
        var result = Result;
        Result = null;
        return result;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReleasePointer();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            _timer.Dispose();
            _original.Dispose();
            _renderer?.Dispose();
            Result?.Dispose();
            _helpPanel?.Dispose();
        }
        base.Dispose(disposing);
    }
}
