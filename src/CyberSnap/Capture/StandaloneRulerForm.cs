using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;

namespace CyberSnap.Capture;

/// <summary>
/// Standalone on-screen ruler activated via global hotkey or tray menu.
/// Overlays a screenshot of all monitors and lets the user drag to measure
/// distances and angles without entering the full capture overlay.
/// Right-click or Escape to close. Shift constrains to 45° increments.
/// </summary>
public sealed class StandaloneRulerForm : Form
{
    private readonly Bitmap _screenshot;
    private Point _rulerStart;
    private bool _isDragging;
    private Point _cursorPos;
    private bool _closed;

    private readonly Action<Bitmap>? _onCapture;
    private long _ignoreInputUntilTick;

    private readonly List<(Point From, Point To)> _measurements = new();
    private int _activeIndex = -1;
    private bool _appendNextDrag;

    // Post-drag editing: move or resize the committed ruler
    private enum EditState { None, Moving, ResizingFrom, ResizingTo }
    private EditState _editState = EditState.None;
    private Point _editOffset; // cursor offset from _lastFrom during move

    // ── Banner (reusable animated instruction overlay) ──
    private readonly BannerLayeredForm _banner;

    // ── Close / exit buttons on the measurement chip ──
    private readonly ToolTip _chipTooltip;
    private enum ChipHover { None, Close, Exit }
    private ChipHover _chipHover;
    private readonly float _dpiScale;

    private readonly ContextMenuStrip _contextMenu;
    private readonly ToolStripMenuItem _deleteRulerItem;
    private int _contextDeleteIndex = -1;

    public StandaloneRulerForm(Action<Bitmap>? onCapture = null)
    {
        _onCapture = onCapture;
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
        var bannerWorkingArea = Screen.FromPoint(Cursor.Position).WorkingArea;

        Theme.Refresh();
        var (bmp, _) = ScreenCapture.CaptureAllScreens(includeCursor: false);
        _screenshot = bmp;

        RulerRenderer.EnsureChrome(Theme.IsDark);

        Cursor = CursorFactory.PrecisionCursor;

        // ── Banner ──
        var rulerLabel = LocalizationService.Translate("Ruler") + ": ";
        var rulerAction = LocalizationService.Translate("Click & drag to measure")
            + " · " + LocalizationService.Translate("Right-click or Esc to close")
            + " · " + LocalizationService.Translate("Hold Shift to constrain");
        _banner = new BannerLayeredForm(
            new BannerSegment[]
            {
                new(rulerLabel, StandaloneToolBanner.LabelColor),
                new(rulerAction, null), // theme accent
            },
            bannerWorkingArea,
            iconId: "ruler");

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

        var newRulerItem = WindowsMenuRenderer.Item("New ruler", "+", iconId: "ruler");
        newRulerItem.Click += (_, _) => BeginNewRuler();
        _contextMenu.Items.Add(newRulerItem);

        _deleteRulerItem = WindowsMenuRenderer.Item("Delete ruler", iconId: "trash", danger: true, dangerIconOnly: true);
        _deleteRulerItem.Click += (_, _) =>
        {
            if (_contextDeleteIndex >= 0)
                RemoveMeasurementAt(_contextDeleteIndex);
        };
        _contextMenu.Items.Add(_deleteRulerItem);

        var captureItem = WindowsMenuRenderer.Item("Capture current screen", "Enter", iconId: "captureRect");
        captureItem.Click += (_, _) => CaptureWithRulers();
        _contextMenu.Items.Add(captureItem);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var exitItem = WindowsMenuRenderer.Item("Exit", "Esc", iconId: "signOutLeave", danger: true, dangerIconOnly: true);
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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ignoreInputUntilTick = Environment.TickCount64 + 250;
        _banner.ShowFor(this);
    }

    // ── Banner animation (delegated to BannerLayeredForm) ──
    // The banner timer is self-contained; mouse-move calls DismissIfHovered so the
    // hint clears when it would otherwise block the tool surface.

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
                BeginNewRuler();
                return true;
            case Keys.Enter when (keyData & Keys.Modifiers) == 0:
                CaptureWithRulers();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ── Mouse ──

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Environment.TickCount64 < _ignoreInputUntilTick)
            return;

        if (e.Button == MouseButtons.Right)
        {
            // If context menu is disabled, right-click exits immediately
            if (!IsContextMenuEnabled())
            {
                Close();
                return;
            }
            // Empty area and drawn rulers share the same menu; over a ruler we also
            // offer Delete ruler instead of leaving the tool.
            var (hit, hitIndex) = HitTestRulers(e.Location);
            if (hit != EditState.None && hitIndex >= 0)
            {
                _activeIndex = hitIndex;
                Invalidate();
                ShowContextMenu(e.Location, deleteIndex: hitIndex);
                return;
            }

            ShowContextMenu(e.Location, deleteIndex: -1);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            if (_activeIndex >= 0)
            {
                if (RulerRenderer.HitTestCachedButton(RulerRenderer.LastCloseButtonBounds, e.Location))
                {
                    RemoveMeasurementAt(_activeIndex);
                    return;
                }
                if (RulerRenderer.HitTestCachedButton(RulerRenderer.LastExitButtonBounds, e.Location))
                {
                    Close();
                    return;
                }
            }

            if (_editState == EditState.None)
            {
                var (hit, hitIndex) = HitTestRulers(e.Location);
                if (hit != EditState.None && hitIndex >= 0)
                {
                    _activeIndex = hitIndex;
                    _editState = hit;
                    var (from, _) = _measurements[hitIndex];
                    _editOffset = new Point(e.Location.X - from.X, e.Location.Y - from.Y);
                    Invalidate();
                    return;
                }
            }

            bool replaceSingle = !_appendNextDrag && _measurements.Count == 1;
            _appendNextDrag = false;
            if (replaceSingle)
            {
                _measurements.Clear();
                _activeIndex = -1;
            }

            _editState = EditState.None;
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

        // Revive only when idle — never while measuring/editing so the pill stays out of the way.
        if (!_isDragging && _editState == EditState.None)
            _banner.DismissIfHovered(PointToScreen(e.Location));

        if (_isDragging)
        {
            var oldEnd = GetRulerEnd(prevCursorPos);
            var newEnd = GetRulerEnd(e.Location);
            Invalidate(SweepBounds(_rulerStart, oldEnd, _rulerStart, newEnd));
        }
        else if (_editState != EditState.None && _activeIndex >= 0)
        {
            var (oldFrom, oldTo) = _measurements[_activeIndex];
            var from = oldFrom;
            var to = oldTo;
            switch (_editState)
            {
                case EditState.Moving:
                {
                    int newFromX = e.Location.X - _editOffset.X;
                    int newFromY = e.Location.Y - _editOffset.Y;
                    int moveDx = newFromX - from.X;
                    int moveDy = newFromY - from.Y;
                    if ((ModifierKeys & Keys.Shift) != 0)
                    {
                        var snapped = LineSnapHelper.SnapEndTo45Degrees(Point.Empty, new Point(moveDx, moveDy));
                        moveDx = snapped.X;
                        moveDy = snapped.Y;
                        newFromX = from.X + moveDx;
                        newFromY = from.Y + moveDy;
                    }
                    from = new Point(newFromX, newFromY);
                    to = new Point(to.X + moveDx, to.Y + moveDy);
                    break;
                }
                case EditState.ResizingFrom:
                    from = (ModifierKeys & Keys.Shift) != 0
                        ? LineSnapHelper.SnapEndTo45Degrees(to, e.Location)
                        : e.Location;
                    break;
                case EditState.ResizingTo:
                    to = (ModifierKeys & Keys.Shift) != 0
                        ? LineSnapHelper.SnapEndTo45Degrees(from, e.Location)
                        : e.Location;
                    break;
            }
            _measurements[_activeIndex] = (from, to);
            Invalidate(SweepBounds(oldFrom, oldTo, from, to));
        }
        else if (_measurements.Count > 0)
        {
            var (hit, _) = HitTestRulers(e.Location);
            Cursor = hit switch
            {
                EditState.Moving => Cursors.SizeAll,
                EditState.ResizingFrom or EditState.ResizingTo => Cursors.SizeNWSE,
                _ => CursorFactory.PrecisionCursor
            };

            var hover = ChipHover.None;
            string? tip = null;
            if (_activeIndex >= 0 && RulerRenderer.HitTestCachedButton(RulerRenderer.LastCloseButtonBounds, e.Location))
            {
                hover = ChipHover.Close;
                tip = LocalizationService.Translate("Clear measurement — stay in ruler mode");
            }
            else if (_activeIndex >= 0 && RulerRenderer.HitTestCachedButton(RulerRenderer.LastExitButtonBounds, e.Location))
            {
                hover = ChipHover.Exit;
                tip = LocalizationService.Translate("Exit ruler");
            }

            if (hover != ChipHover.None)
            {
                if (_chipHover != hover)
                {
                    _chipHover = hover;
                    _chipTooltip.SetToolTip(this, tip);
                }
                Cursor = Cursors.Hand;
            }
            else if (_chipHover != ChipHover.None)
            {
                _chipHover = ChipHover.None;
                _chipTooltip.SetToolTip(this, "");
            }
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_isDragging && e.Button == MouseButtons.Left)
        {
            _isDragging = false;
            var end = GetRulerEnd(_cursorPos);
            // If the user just clicked without a meaningful drag (< 10 px), revive the banner
            if (DistSq(_rulerStart, end) < 100) // 10² = 100
            {
                _banner.Revive();
                Invalidate();
                base.OnMouseUp(e);
                return;
            }
            _measurements.Add((_rulerStart, end));
            _activeIndex = _measurements.Count - 1;
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

        for (int i = 0; i < _measurements.Count; i++)
        {
            if (i == _activeIndex && !_isDragging)
                continue;
            var (from, to) = _measurements[i];
            RulerRenderer.Paint(g, from, to, ClientRectangle, Theme.IsDark, dpiScale: _dpiScale);
        }

        if (_isDragging)
        {
            var end = GetRulerEnd(_cursorPos);
            RulerRenderer.Paint(g, _rulerStart, end, ClientRectangle, Theme.IsDark, dpiScale: _dpiScale);
        }
        else if (_activeIndex >= 0 && _activeIndex < _measurements.Count)
        {
            var (from, to) = _measurements[_activeIndex];
            RulerRenderer.Paint(g, from, to, ClientRectangle, Theme.IsDark,
                showCloseButton: true, showExitButton: true, dpiScale: _dpiScale);
        }
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
        return LineSnapHelper.SnapEndTo45Degrees(_rulerStart, current);
    }

    /// <summary>Hit-test committed rulers, last-drawn first so overlapping tips pick the newest.</summary>
    private (EditState State, int Index) HitTestRulers(Point p)
    {
        for (int i = _measurements.Count - 1; i >= 0; i--)
        {
            var state = HitTestRuler(p, _measurements[i].From, _measurements[i].To,
                includeChip: true, withButtons: i == _activeIndex);
            if (state != EditState.None)
                return (state, i);
        }
        return (EditState.None, -1);
    }

    private EditState HitTestRuler(Point p, Point from, Point to, bool includeChip, bool withButtons = false)
    {
        const int endpointRadius = 16;
        const int lineThreshold = 10;

        if (DistSq(p, from) <= endpointRadius * endpointRadius)
            return EditState.ResizingFrom;
        if (DistSq(p, to) <= endpointRadius * endpointRadius)
            return EditState.ResizingTo;

        if (DistToSegmentSq(p, from, to) <= lineThreshold * lineThreshold)
            return EditState.Moving;

        if (includeChip)
        {
            var labelBounds = RulerRenderer.GetLabelBounds(from, to, ClientRectangle, _dpiScale,
                showCloseButton: withButtons, showExitButton: withButtons);
            labelBounds.Inflate(4, 4);
            if (labelBounds.Contains(p))
                return EditState.Moving;
        }

        return EditState.None;
    }

    private bool IsOverRulerOrChip(Point p) => HitTestRulers(p).State != EditState.None;

    private void ShowContextMenu(Point location, int deleteIndex)
    {
        _contextDeleteIndex = deleteIndex;
        _deleteRulerItem.Visible = deleteIndex >= 0;
        WindowsMenuRenderer.NormalizeItemWidths(_contextMenu, minWidth: 240);
        _contextMenu.Show(this, location);
    }

    private void BeginNewRuler()
    {
        _appendNextDrag = true;
        _activeIndex = -1;
        _editState = EditState.None;
        _chipHover = ChipHover.None;
        _chipTooltip.SetToolTip(this, "");
        Cursor = CursorFactory.PrecisionCursor;
        _banner.Revive();
        Invalidate();
    }

    private void RemoveMeasurementAt(int index)
    {
        if (index < 0 || index >= _measurements.Count)
            return;
        _measurements.RemoveAt(index);
        _activeIndex = _measurements.Count == 0 ? -1 : Math.Min(index, _measurements.Count - 1);
        _editState = EditState.None;
        _chipHover = ChipHover.None;
        _chipTooltip.SetToolTip(this, "");
        Cursor = CursorFactory.PrecisionCursor;
        Invalidate();
    }

    private void CaptureWithRulers()
    {
        Bitmap? composed = null;
        try
        {
            composed = RenderCaptureBitmap(currentScreenOnly: !ShouldCaptureAllScreens());
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("standalone-ruler.capture", ex);
            composed?.Dispose();
            return;
        }

        _closed = true;
        var bmp = composed;
        var callback = _onCapture;
        BeginInvoke(() =>
        {
            try
            {
                if (callback is null)
                    bmp.Dispose();
                else
                    callback(bmp);
            }
            catch
            {
                bmp.Dispose();
            }
            Close();
        });
    }

    private Bitmap RenderCaptureBitmap(bool currentScreenOnly)
    {
        var composed = new Bitmap(_screenshot.Width, _screenshot.Height, _screenshot.PixelFormat);
        using (var g = Graphics.FromImage(composed))
        {
            g.DrawImageUnscaled(_screenshot, 0, 0);
            var bounds = new Rectangle(0, 0, composed.Width, composed.Height);
            foreach (var (from, to) in _measurements)
                RulerRenderer.Paint(g, from, to, bounds, Theme.IsDark, dpiScale: _dpiScale);
            if (_isDragging)
                RulerRenderer.Paint(g, _rulerStart, GetRulerEnd(_cursorPos), bounds, Theme.IsDark, dpiScale: _dpiScale);
        }

        if (!currentScreenOnly)
            return composed;

        var screen = Screen.FromPoint(Cursor.Position).Bounds;
        var virtualScreen = SystemInformation.VirtualScreen;
        var crop = Rectangle.Intersect(
            new Rectangle(screen.X - virtualScreen.X, screen.Y - virtualScreen.Y, screen.Width, screen.Height),
            new Rectangle(0, 0, composed.Width, composed.Height));
        if (crop.Width <= 0 || crop.Height <= 0)
            return composed;

        var cropped = composed.Clone(crop, composed.PixelFormat);
        composed.Dispose();
        return cropped;
    }
    /// <summary>Returns true if Enter should capture all screens (per user setting).</summary>
    private static bool ShouldCaptureAllScreens()
    {
        try
        {
            return SettingsService.LoadStatic()?.RulerCaptureAllScreens ?? false;
        }
        catch { return false; }
    }

    private static bool IsContextMenuEnabled()
    {
        try
        {
            return SettingsService.LoadStatic()?.RulerContextMenuEnabled ?? true;
        }
        catch { return true; }
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

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
            App.NotifyFirstTimeTool("ruler");
    }
}