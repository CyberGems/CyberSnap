using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
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
    private readonly bool _smooth, _animate, _includeCursor;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Font _hudFont;
    private readonly string _viewHelp, _regionHelp;
    private readonly Rectangle _screenBounds;
    private Bitmap? _lockedFrame;
    private ZoomRenderer? _renderer;
    private bool _rendererUnavailable;
    private Point? _dragStart;
    private Rectangle _selection;
    private double _lastTick;
    private bool _completed;
    private bool _closing, _ownsPointerClip;
    private Rectangle _previousPointerClip;

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
        _hudFont = UiChrome.ChromeFont(10);
        _viewHelp = LocalizationService.Translate("Wheel / + / -: zoom · C: select region · Enter: capture view · Esc / right-click: exit");
        _regionHelp = LocalizationService.Translate("Drag to capture a region · C: return to Zoom · Esc / right-click: exit");
        _timer.Tick += (_, _) =>
        {
            double now = _clock.Elapsed.TotalSeconds;
            _viewport.SetPointer(PointToClient(System.Windows.Forms.Cursor.Position));
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
        _previousPointerClip = System.Windows.Forms.Cursor.Clip;
        System.Windows.Forms.Cursor.Clip = Bounds;
        _ownsPointerClip = true;
        _lastTick = _clock.Elapsed.TotalSeconds;
        _timer.Start();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (_ownsPointerClip && !_closing) Close();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_ownsPointerClip && !_closing) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
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
        if (_lockedFrame is not null) e.Graphics.DrawImageUnscaled(_lockedFrame, 0, 0);
        else _viewport.Draw(e.Graphics, _original, _smooth);
        DrawGdiChrome(e.Graphics);
        base.OnPaint(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    private bool EnsureRenderer()
    {
        if (_rendererUnavailable) return false;
        if (_renderer is not null) return true;
        try { _renderer = new ZoomRenderer(Handle, _original, _hudFont.SizeInPoints * DeviceDpi / 72f); }
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

    /// <summary>Read the same rendered surface shown on screen, optionally including the help/selection chrome.</summary>
    public Bitmap RenderView(bool includeChrome = false)
    {
        if (EnsureRenderer())
        {
            try { return _renderer!.Snapshot(_viewport, _smooth, includeChrome ? DrawGpuChrome : null); }
            catch (Exception ex) { DiscardRenderer(ex); }
        }
        var result = _lockedFrame is null ? _viewport.Render(_original, _smooth) : (Bitmap)_lockedFrame.Clone();
        if (includeChrome)
        {
            using var graphics = Graphics.FromImage(result);
            DrawGdiChrome(graphics);
        }
        return result;
    }

    private void DrawGdiChrome(Graphics graphics)
    {
        if (_viewport.IsSelecting)
        {
            using var shade = new SolidBrush(Color.FromArgb(85, Color.Black));
            using var region = new Region(ClientRectangle);
            if (!_selection.IsEmpty) region.Exclude(_selection);
            graphics.FillRegion(shade, region);
            if (_selection.Width > 0 && _selection.Height > 0)
            {
                using var outline = new Pen(Accent, 2);
                graphics.DrawRectangle(outline, _selection);
            }
        }
        DrawHud(graphics);
    }

    private void DrawGpuChrome(ZoomRenderer renderer)
    {
        if (_viewport.IsSelecting)
        {
            var shade = Color.FromArgb(85, Color.Black);
            if (_selection.IsEmpty) renderer.Fill(ClientRectangle, shade);
            else
            {
                renderer.Fill(new Rectangle(0, 0, ClientSize.Width, _selection.Top), shade);
                renderer.Fill(new Rectangle(0, _selection.Bottom, ClientSize.Width, ClientSize.Height - _selection.Bottom), shade);
                renderer.Fill(new Rectangle(0, _selection.Top, _selection.Left, _selection.Height), shade);
                renderer.Fill(new Rectangle(_selection.Right, _selection.Top, ClientSize.Width - _selection.Right, _selection.Height), shade);
                renderer.Outline(_selection, Accent, 2);
            }
        }
        var (text, rect, padding) = HudLayout();
        renderer.Fill(rect, Color.FromArgb(240, Theme.BgSecondary.R, Theme.BgSecondary.G, Theme.BgSecondary.B));
        renderer.Outline(rect, Accent);
        rect.Inflate(-padding, -padding);
        renderer.Text(text, rect, Color.FromArgb(Theme.TextPrimary.R, Theme.TextPrimary.G, Theme.TextPrimary.B));
    }

    private static Color Accent => Color.FromArgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B);

    private (string Text, Rectangle Bounds, int Padding) HudLayout()
    {
        float scale = DeviceDpi / 96f;
        string text = $"{_viewport.Zoom:0.##}×   " + (_viewport.IsSelecting ? _regionHelp : _viewHelp);
        int padding = (int)(12 * scale);
        var measured = TextRenderer.MeasureText(text, _hudFont,
            new Size(Math.Max(100, ClientSize.Width - padding * 4), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        var rect = new Rectangle((ClientSize.Width - measured.Width) / 2 - padding,
            ClientSize.Height - measured.Height - padding * 3, measured.Width + padding * 2, measured.Height + padding * 2);
        // Move the help out of the way when the pointer approaches it.
        if (rect.Contains(PointToClient(System.Windows.Forms.Cursor.Position))) rect.Y = padding;
        return (text, rect, padding);
    }

    private void DrawHud(Graphics graphics)
    {
        var (text, rect, padding) = HudLayout();
        using var background = new SolidBrush(Color.FromArgb(240, Theme.BgSecondary.R, Theme.BgSecondary.G, Theme.BgSecondary.B));
        using var border = new Pen(Accent);
        graphics.FillRectangle(background, rect);
        graphics.DrawRectangle(border, rect);
        rect.Inflate(-padding, -padding);
        TextRenderer.DrawText(graphics, text, _hudFont, rect,
            Color.FromArgb(Theme.TextPrimary.R, Theme.TextPrimary.G, Theme.TextPrimary.B),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
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
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.Control && e.KeyCode is Keys.C or Keys.S)
            Complete(new Rectangle(Point.Empty, _viewport.Size), e.KeyCode == Keys.C
                ? RegionOverlayForm.ConfirmCommitAction.Copy : RegionOverlayForm.ConfirmCommitAction.Save);
        else if (e.KeyCode == Keys.C) ToggleSelection();
        else if (e.KeyCode == Keys.Enter)
            Complete(_viewport.IsSelecting ? _selection : new Rectangle(Point.Empty, _viewport.Size));
        else if (e.KeyCode is Keys.Add or Keys.Oemplus or Keys.Up) _viewport.ChangeZoom(1, _animate);
        else if (e.KeyCode is Keys.Subtract or Keys.OemMinus or Keys.Down) _viewport.ChangeZoom(-1, _animate);
        else { e.Handled = false; e.SuppressKeyPress = false; }
        Invalidate();
        base.OnKeyDown(e);
    }

    private void ToggleSelection()
    {
        _dragStart = null;
        _selection = Rectangle.Empty;
        Capture = false;
        if (_viewport.IsSelecting)
        {
            _viewport.EndSelection();
            _lockedFrame?.Dispose();
            _lockedFrame = null;
            Cursor = Cursors.Default;
        }
        else
        {
            _viewport.BeginSelection();
            if (!EnsureRenderer()) _lockedFrame = _viewport.Render(_original, _smooth);
            Cursor = Cursors.Cross;
        }
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Close(); return; }
        if (e.Button == MouseButtons.Left && _viewport.IsSelecting)
        {
            _dragStart = e.Location;
            _selection = Rectangle.Empty;
            Capture = true;
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        _viewport.SetPointer(e.Location);
        if (_dragStart is Point start) _selection = _viewport.Selection(start, e.Location);
        if (_viewport.IsSelecting) Invalidate();
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && _dragStart is Point start)
        {
            _selection = _viewport.Selection(start, e.Location);
            _dragStart = null;
            Capture = false;
            Complete(_selection);
        }
        base.OnMouseUp(e);
    }

    private void Complete(Rectangle region, RegionOverlayForm.ConfirmCommitAction action = RegionOverlayForm.ConfirmCommitAction.Default)
    {
        region.Intersect(new Rectangle(Point.Empty, _viewport.Size));
        if (_completed || region.Width < 2 || region.Height < 2) return;
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
            _lockedFrame?.Dispose();
            _renderer?.Dispose();
            Result?.Dispose();
            _hudFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
