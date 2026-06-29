using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>
/// Standalone on-screen ruler activated via global hotkey or tray menu.
/// Overlays a screenshot of all monitors and lets the user drag to measure
/// distances and angles without entering the full capture overlay.
/// Right-click or Escape to close. Shift constrains to horizontal/vertical.
/// </summary>
public sealed class StandaloneRulerForm : Form
{
    private readonly Bitmap _screenshot;
    private readonly Rectangle _bannerWorkingArea;
    private Point _rulerStart;
    private bool _isDragging;
    private Point _cursorPos;
    private bool _closed;

    // Last committed measurement persists on screen until next drag
    private Point _lastFrom;
    private Point _lastTo;
    private bool _hasLastMeasurement;

    // Post-drag editing: move or resize the committed ruler
    private enum EditState { None, Moving, ResizingFrom, ResizingTo }
    private EditState _editState = EditState.None;
    private Point _editOffset; // cursor offset from _lastFrom during move

    // ── Banner (reusable animated instruction overlay) ──
    private readonly StandaloneToolBanner _banner;

    // ── Close button on the measurement chip ──
    private readonly ToolTip _chipTooltip;
    private bool _cursorOverCloseButton;
    private readonly float _dpiScale;

    // ── Context menu (empty-area right-click) ──
    private readonly ContextMenuStrip _contextMenu;

    // ── Callback to trigger a fullscreen capture from the main App thread ──
    private readonly Action? _onCaptureFullscreen;

    public StandaloneRulerForm(Action? onCaptureFullscreen = null)
    {
        _onCaptureFullscreen = onCaptureFullscreen;
        _dpiScale = DeviceDpi / 96f;

        // Give the tray context menu time to fully dismiss before screenshot
        Thread.Sleep(80);

        var bounds = SystemInformation.VirtualScreen;
        Bounds = bounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        KeyPreview = true;

        // Capture which screen the cursor is on now (STA thread, right after menu click)
        _bannerWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea;

        Theme.Refresh();
        var (bmp, _) = ScreenCapture.CaptureAllScreens(includeCursor: false);
        _screenshot = bmp;

        RulerRenderer.EnsureChrome(Theme.IsDark);

        Cursor = Cursors.Cross;

        // ── Banner ──
        _banner = new StandaloneToolBanner(
            LocalizationService.Translate("Click & drag to measure  ·  Right-click or Esc to close  ·  Hold Shift to constrain"),
            _bannerWorkingArea,
            Bounds,
            onInvalidate: () => Invalidate());

        // ── Chip close-button tooltip ──
        _chipTooltip = new ToolTip
        {
            AutoPopDelay = 3000,
            InitialDelay = 400,
            ReshowDelay = 100,
            ShowAlways = true,
            OwnerDraw = true,
        };
        _chipTooltip.Draw += (_, e) =>
        {
            var isDark = Theme.IsDark;
            using var bgBrush = new SolidBrush(isDark ? Color.FromArgb(30, 33, 34) : Color.FromArgb(240, 240, 240));
            using var borderPen = new Pen(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
            var fgColor = isDark ? Color.FromArgb(240, 240, 245) : Color.FromArgb(24, 24, 24);

            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            using var path = RoundedRect(r, 4f);
            e.Graphics.FillPath(bgBrush, path);
            e.Graphics.DrawPath(borderPen, path);

            TextRenderer.DrawText(e.Graphics, e.ToolTipText, e.Font, r, fgColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        _chipTooltip.Popup += (_, e) =>
        {
            using var g = CreateGraphics();
            var sz = TextRenderer.MeasureText(g, _chipTooltip.GetToolTip(this), Font);
            e.ToolTipSize = new Size(sz.Width + 16, sz.Height + 10);
        };
        _chipTooltip.SetToolTip(this, ""); // will be set dynamically in OnMouseMove

        // Helper for rounded rect in tooltip drawing
        static GraphicsPath RoundedRect(Rectangle r, float rad)
        {
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            path.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            path.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            path.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ── Context menu (shown on right-click over empty area) ──
        _contextMenu = WindowsMenuRenderer.Create(showImages: true, minWidth: 240);

        var newRulerItem = WindowsMenuRenderer.Item("Nueva regla", "+", iconId: "ruler");
        newRulerItem.Click += (_, _) => ClearMeasurement();
        _contextMenu.Items.Add(newRulerItem);

        var captureItem = WindowsMenuRenderer.Item("Capturar pantalla", "Enter", iconId: "captureRect");
        captureItem.Click += (_, _) =>
        {
            _closed = true;
            BeginInvoke(() =>
            {
                _onCaptureFullscreen?.Invoke();
                Close();
            });
        };
        _contextMenu.Items.Add(captureItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = WindowsMenuRenderer.Item("Salir", "Esc", iconId: "close", danger: true);
        exitItem.Click += (_, _) => Close();
        _contextMenu.Items.Add(exitItem);

        WindowsMenuRenderer.NormalizeItemWidths(_contextMenu, minWidth: 240);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chipTooltip?.Dispose();
            _contextMenu?.Dispose();
            _banner.Dispose();
            _screenshot?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ── Banner animation (delegated to StandaloneToolBanner) ──
    // The banner timer is self-contained; we just need to revive it on hover
    // and trigger repaints via Invalidate. See StandaloneToolBanner.Revive().

    // ── Keyboard ──

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        switch (key)
        {
            case Keys.Escape:
                Close();
                return true;
            case Keys.Oemplus or Keys.Add when (keyData & Keys.Modifiers) == 0:
                // "+" → clear measurement, ready for new ruler
                ClearMeasurement();
                return true;
            case Keys.Enter when (keyData & Keys.Modifiers) == 0:
                // "Enter" → capture the screen where the cursor is
                _closed = true;
                BeginInvoke(() =>
                {
                    _onCaptureFullscreen?.Invoke();
                    Close();
                });
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── Mouse ──

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            // If cursor is over the ruler or its chip, close immediately (existing behavior)
            if (_hasLastMeasurement && IsOverRulerOrChip(e.Location))
            {
                Close();
                return;
            }
            // Otherwise show context menu on empty area
            _contextMenu.Show(this, e.Location);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            // Check if clicking the close button on the measurement chip
            if (_hasLastMeasurement)
            {
                var closeRect = RulerRenderer.LastCloseButtonBounds;
                var hitRect = Rectangle.Round(closeRect);
                hitRect.Inflate(4, 4);
                if (hitRect.Contains(e.Location))
                {
                    ClearMeasurement();
                    return;
                }
            }

            // If there's a committed ruler, check if we're editing it
            if (_hasLastMeasurement && _editState == EditState.None)
            {
                var hit = HitTestRuler(e.Location);
                switch (hit)
                {
                    case EditState.Moving:
                        _editState = EditState.Moving;
                        _editOffset = new Point(e.Location.X - _lastFrom.X, e.Location.Y - _lastFrom.Y);
                        Invalidate();
                        return;
                    case EditState.ResizingFrom:
                        _editState = EditState.ResizingFrom;
                        Invalidate();
                        return;
                    case EditState.ResizingTo:
                        _editState = EditState.ResizingTo;
                        Invalidate();
                        return;
                }
            }

            // Not editing existing ruler — start a fresh drag
            _editState = EditState.None;
            _hasLastMeasurement = false;
            _isDragging = true;
            _rulerStart = e.Location;
            _cursorPos = e.Location;
            _banner.Dismiss();
            Invalidate();
        }

        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var prevCursorPos = _cursorPos;
        _cursorPos = e.Location;

        // Revive banner if cursor moves over it (keep instructions visible)
        if (_banner.ContainsCursor(_cursorPos))
            _banner.Revive();

        if (_isDragging)
        {
            var oldEnd = GetRulerEnd(prevCursorPos);
            var newEnd = GetRulerEnd(e.Location);
            Invalidate(SweepBounds(_rulerStart, oldEnd, _rulerStart, newEnd));
        }
        else if (_editState != EditState.None)
        {
            var oldFrom = _lastFrom;
            var oldTo = _lastTo;
            switch (_editState)
            {
                case EditState.Moving:
                    int dx = e.Location.X - _editOffset.X;
                    int dy = e.Location.Y - _editOffset.Y;
                    if ((ModifierKeys & Keys.Shift) != 0)
                    {
                        int rawDx = dx - _lastFrom.X;
                        int rawDy = dy - _lastFrom.Y;
                        if (Math.Abs(rawDx) >= Math.Abs(rawDy))
                            dy = _lastFrom.Y;
                        else
                            dx = _lastFrom.X;
                    }
                    int moveDx = dx - _lastFrom.X;
                    int moveDy = dy - _lastFrom.Y;
                    _lastFrom = new Point(dx, dy);
                    _lastTo = new Point(_lastTo.X + moveDx, _lastTo.Y + moveDy);
                    break;
                case EditState.ResizingFrom:
                    if ((ModifierKeys & Keys.Shift) != 0)
                        _lastFrom = ConstrainPoint(e.Location, _lastTo);
                    else
                        _lastFrom = e.Location;
                    break;
                case EditState.ResizingTo:
                    if ((ModifierKeys & Keys.Shift) != 0)
                        _lastTo = ConstrainPoint(e.Location, _lastFrom);
                    else
                        _lastTo = e.Location;
                    break;
            }
            Invalidate(SweepBounds(oldFrom, oldTo, _lastFrom, _lastTo));
        }
        else if (_hasLastMeasurement)
        {
            // Update cursor to hint editability
            var hit = HitTestRuler(e.Location);
            Cursor = hit switch
            {
                EditState.Moving => Cursors.SizeAll,
                EditState.ResizingFrom or EditState.ResizingTo => Cursors.SizeNWSE,
                _ => Cursors.Cross
            };

            // Track hover over the close button on the chip — use cached bounds from last paint
            var closeRect = RulerRenderer.LastCloseButtonBounds;
            if (closeRect.IsEmpty) { _cursorOverCloseButton = false; }
            else
            {
                var hitRect = Rectangle.Round(closeRect);
                hitRect.Inflate(4, 4);
                bool overClose = hitRect.Contains(e.Location);
                if (overClose)
                {
                    if (!_cursorOverCloseButton)
                    {
                        _cursorOverCloseButton = true;
                        _chipTooltip.SetToolTip(this, LocalizationService.Translate("Borrar medición — sigue en modo regla"));
                    }
                    Cursor = Cursors.Hand; // override every frame — HitTestRuler may have set Cross
                }
                else if (_cursorOverCloseButton)
                {
                    _cursorOverCloseButton = false;
                    _chipTooltip.SetToolTip(this, "");
                }
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_isDragging && e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            _lastFrom = _rulerStart;
            _lastTo = GetRulerEnd(_cursorPos);
            _hasLastMeasurement = true;
            Invalidate();
        }
        else if (_editState != EditState.None && e.Button == MouseButtons.Left)
        {
            _editState = EditState.None;
            Invalidate();
        }
        base.OnMouseUp(e);
    }

    // ── Paint ──

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_closed) return;
        var g = e.Graphics;

        // Draw screenshot as background
        g.DrawImage(_screenshot, ClientRectangle);

        // Draw ruler if dragging or last measurement persists
        if (_isDragging)
        {
            var end = GetRulerEnd(_cursorPos);
            RulerRenderer.Paint(g, _rulerStart, end, ClientRectangle, Theme.IsDark);
        }
        else if (_hasLastMeasurement)
        {
            RulerRenderer.Paint(g, _lastFrom, _lastTo, ClientRectangle, Theme.IsDark, showCloseButton: true, dpiScale: _dpiScale);
        }

        // Draw banner (handled by reusable StandaloneToolBanner)
        _banner.Render(g);
    }

    // ── Helpers ──

    /// <summary>Conservative bounds covering two line segments and their labels (sweep-safe).</summary>
    private static Rectangle SweepBounds(Point a1, Point a2, Point b1, Point b2)
    {
        int minX = Math.Min(Math.Min(a1.X, a2.X), Math.Min(b1.X, b2.X));
        int minY = Math.Min(Math.Min(a1.Y, a2.Y), Math.Min(b1.Y, b2.Y));
        int maxX = Math.Max(Math.Max(a1.X, a2.X), Math.Max(b1.X, b2.X));
        int maxY = Math.Max(Math.Max(a1.Y, a2.Y), Math.Max(b1.Y, b2.Y));

        // Inflate to cover line ticks and floating label (conservative: 430px each direction)
        const int pad = 430;
        return Rectangle.FromLTRB(minX - pad, minY - pad, maxX + pad, maxY + pad);
    }

    private Point GetRulerEnd(Point current)
    {
        if ((ModifierKeys & Keys.Shift) == 0) return current;

        return ConstrainPoint(current, _rulerStart);
    }

    /// <summary>Constrain a point to horizontal or vertical axis from an anchor.</summary>
    private static Point ConstrainPoint(Point current, Point anchor)
    {
        int dx = current.X - anchor.X;
        int dy = current.Y - anchor.Y;
        if (Math.Abs(dx) >= Math.Abs(dy))
            return new Point(current.X, anchor.Y);
        return new Point(anchor.X, current.Y);
    }

    /// <summary>Hit-test the committed ruler: returns which part the cursor is near.
    /// Also checks the label chip so the ruler can be dragged from the measurement box.</summary>
    private EditState HitTestRuler(Point p)
    {
        if (!_hasLastMeasurement) return EditState.None;

        const int endpointRadius = 16;
        const int lineThreshold = 10;

        // Check endpoints first (they take priority over the line)
        int distFrom = DistSq(p, _lastFrom);
        int distTo = DistSq(p, _lastTo);
        if (distFrom <= endpointRadius * endpointRadius)
            return EditState.ResizingFrom;
        if (distTo <= endpointRadius * endpointRadius)
            return EditState.ResizingTo;

        // Check distance to the line segment
        float lineDist = DistToSegmentSq(p, _lastFrom, _lastTo);
        if (lineDist <= lineThreshold * lineThreshold)
            return EditState.Moving;

        // Check if inside the label chip — allows dragging from the measurement box
        var labelBounds = RulerRenderer.GetLabelBounds(_lastFrom, _lastTo, ClientRectangle);
        labelBounds.Inflate(4, 4);
        if (labelBounds.Contains(p))
            return EditState.Moving;

        return EditState.None;
    }

    /// <summary>Returns true if the cursor is over the ruler line, its endpoints, or the measurement chip.</summary>
    private bool IsOverRulerOrChip(Point p)
    {
        // HitTestRuler now checks the chip too, so this is all we need
        return _hasLastMeasurement && HitTestRuler(p) != EditState.None;
    }

    /// <summary>Clear the current measurement and reset state for a fresh ruler drag.</summary>
    private void ClearMeasurement()
    {
        _hasLastMeasurement = false;
        _editState = EditState.None;
        _cursorOverCloseButton = false;
        _chipTooltip.SetToolTip(this, "");
        Cursor = Cursors.Cross;
        Invalidate();
    }

    private static int DistSq(Point a, Point b)
    {
        int dx = a.X - b.X;
        int dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static float DistToSegmentSq(Point p, Point a, Point b)
    {
        float abx = b.X - a.X;
        float aby = b.Y - a.Y;
        float lenSq = abx * abx + aby * aby;
        if (lenSq < 0.5f) return DistSq(p, a);

        float t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq, 0f, 1f);
        float projX = a.X + t * abx;
        float projY = a.Y + t * aby;
        float dx = p.X - projX;
        float dy = p.Y - projY;
        return dx * dx + dy * dy;
    }

    // RenderBanner and RoundedRect moved to reusable StandaloneToolBanner helper.

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        base.OnFormClosed(e);
    }
}
