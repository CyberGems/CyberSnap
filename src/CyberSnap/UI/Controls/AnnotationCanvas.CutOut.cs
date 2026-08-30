using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models.Commands;
using CyberSnap.Services;

namespace CyberSnap.UI.Controls;

public sealed partial class AnnotationCanvas
{
    private const int CutOutHandleNear = 0;
    private const int CutOutHandleFar = 1;
    private const int CutOutHandleMove = 8;
    private const int CutOutAxisThresholdPx = 8;

    private Rectangle _cutOutRect = Rectangle.Empty;
    private bool _cutOutDragging;
    private bool _cutOutHasRect;
    private bool _cutOutHorizontal;
    private bool _cutOutAxisLocked;
    private int _activeCutOutHandle = -1;
    private Point _cutOutDragStartImg;
    private Rectangle _cutOutDragStartRect;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HasPendingCutOut =>
        _activeTool == CanvasTool.CutOut && (_cutOutHasRect || _cutOutDragging);

    private bool IsCutOutOverlayActive =>
        _activeTool == CanvasTool.CutOut || _preSpaceTool == CanvasTool.CutOut;

    private bool HideCanvasResizeHandles =>
        _activeTool is CanvasTool.Crop or CanvasTool.CutOut || _preSpaceTool != null;

    private bool CutOutStripIsValid
    {
        get
        {
            if (_baseBitmap is null) return false;
            var strip = GetClampedCutOutStrip();
            if (_cutOutHorizontal)
                return strip.Height >= 1 && strip.Height < _baseBitmap.Height;
            return strip.Width >= 1 && strip.Width < _baseBitmap.Width;
        }
    }

    private bool IsValidPendingCutOut =>
        (_cutOutHasRect || _cutOutDragging) && CutOutStripIsValid;

    public bool TryConfirmCutOut()
    {
        if (!IsValidPendingCutOut || _baseBitmap is null)
            return false;

        var strip = GetClampedCutOutStrip();
        bool horizontal = _cutOutHorizontal;
        ClearCutOutPending();

        var command = new CutOutCommand(strip, horizontal);
        if (IsDefaultBlank)
            PushClean(command);
        else
            Push(command);

        // Keep remaining pixels on screen when the strip included the origin edge,
        // matching Crop's "kept region stays put" pan adjustment.
        if (horizontal)
        {
            if (strip.Y == 0)
                _pan.Y += (float)(strip.Height * _zoom);
        }
        else if (strip.X == 0)
        {
            _pan.X += (float)(strip.Width * _zoom);
        }
        _viewFitsWindow = false;
        _userPanned = true;

        HideToolBanner();
        return true;
    }

    public void CancelCutOutPending()
    {
        if (!_cutOutHasRect && !_cutOutDragging) return;
        ClearCutOutPending();
        ShowToolBanner(LocalizationService.Translate("Cut Out canceled"));
        Invalidate();
        OnStateChanged();
    }

    private void ClearCutOutPending()
    {
        _cutOutDragging = false;
        _cutOutHasRect = false;
        _cutOutRect = Rectangle.Empty;
        _cutOutAxisLocked = false;
        _activeCutOutHandle = -1;
    }

    private bool FinalizeLeavingCutOut()
    {
        if (IsValidPendingCutOut)
            return TryConfirmCutOut();
        ClearCutOutPending();
        return false;
    }

    private Rectangle GetClampedCutOutStrip()
    {
        if (_baseBitmap is null) return Rectangle.Empty;
        int w = _baseBitmap.Width;
        int h = _baseBitmap.Height;
        var r = Rectangle.Intersect(_cutOutRect, new Rectangle(0, 0, w, h));
        if (_cutOutHorizontal)
            return new Rectangle(0, r.Y, w, r.Height);
        return new Rectangle(r.X, 0, r.Width, h);
    }

    private void BeginCutOutPointer(Point img, Point screenPt)
    {
        if (_cutOutHasRect)
        {
            int hit = HitTestCutOutHandle(screenPt);
            if (hit >= 0)
            {
                _activeCutOutHandle = hit;
                _cutOutDragging = true;
                _cutOutDragStartImg = img;
                _cutOutDragStartRect = _cutOutRect;
                Invalidate();
                OnStateChanged();
                return;
            }
        }

        _activeCutOutHandle = -1;
        _cutOutAxisLocked = false;
        _cutOutHasRect = false;
        _cutOutRect = Rectangle.Empty;
        _dragStartImg = img;
        _dragLastImg = img;
        _cutOutDragging = true;
        Invalidate();
        OnStateChanged();
    }

    private void UpdateCutOutDrag(Point img)
    {
        if (_baseBitmap is null) return;

        if (_activeCutOutHandle == -1)
        {
            _dragLastImg = img;
            int dx = img.X - _dragStartImg.X;
            int dy = img.Y - _dragStartImg.Y;
            if (!_cutOutAxisLocked)
            {
                _cutOutHorizontal = Math.Abs(dy) >= Math.Abs(dx);
                bool pastThreshold = Math.Abs(dx) >= CutOutAxisThresholdPx
                    || Math.Abs(dy) >= CutOutAxisThresholdPx;
                bool shiftLock = ModifierKeys.HasFlag(Keys.Shift) && (dx != 0 || dy != 0);
                if (pastThreshold || shiftLock)
                    _cutOutAxisLocked = true;
            }
            ApplyNewCutOutRect(_dragStartImg, img);
        }
        else if (_activeCutOutHandle == CutOutHandleMove)
        {
            int dx = img.X - _cutOutDragStartImg.X;
            int dy = img.Y - _cutOutDragStartImg.Y;
            var r = _cutOutDragStartRect;
            if (_cutOutHorizontal)
            {
                int ny = Math.Clamp(r.Y + dy, 0, _baseBitmap.Height - r.Height);
                _cutOutRect = new Rectangle(0, ny, _baseBitmap.Width, r.Height);
            }
            else
            {
                int nx = Math.Clamp(r.X + dx, 0, _baseBitmap.Width - r.Width);
                _cutOutRect = new Rectangle(nx, 0, r.Width, _baseBitmap.Height);
            }
        }
        else
        {
            int dx = img.X - _cutOutDragStartImg.X;
            int dy = img.Y - _cutOutDragStartImg.Y;
            var r = _cutOutDragStartRect;
            const int minSize = 1;
            if (_cutOutHorizontal)
            {
                int top = r.Top;
                int bottom = r.Bottom;
                if (_activeCutOutHandle == CutOutHandleNear)
                    top = Math.Min(r.Top + dy, r.Bottom - minSize);
                else
                    bottom = Math.Max(r.Bottom + dy, r.Top + minSize);
                top = Math.Clamp(top, 0, _baseBitmap.Height - 1);
                bottom = Math.Clamp(bottom, top + minSize, _baseBitmap.Height);
                if (bottom - top >= _baseBitmap.Height)
                    bottom = top + (_baseBitmap.Height - 1);
                _cutOutRect = new Rectangle(0, top, _baseBitmap.Width, bottom - top);
            }
            else
            {
                int left = r.Left;
                int right = r.Right;
                if (_activeCutOutHandle == CutOutHandleNear)
                    left = Math.Min(r.Left + dx, r.Right - minSize);
                else
                    right = Math.Max(r.Right + dx, r.Left + minSize);
                left = Math.Clamp(left, 0, _baseBitmap.Width - 1);
                right = Math.Clamp(right, left + minSize, _baseBitmap.Width);
                if (right - left >= _baseBitmap.Width)
                    right = left + (_baseBitmap.Width - 1);
                _cutOutRect = new Rectangle(left, 0, right - left, _baseBitmap.Height);
            }
        }

        Invalidate();
    }

    private void ApplyNewCutOutRect(Point a, Point b)
    {
        if (_baseBitmap is null) return;
        int w = _baseBitmap.Width;
        int h = _baseBitmap.Height;
        if (_cutOutHorizontal)
        {
            int y1 = Math.Clamp(Math.Min(a.Y, b.Y), 0, h);
            int y2 = Math.Clamp(Math.Max(a.Y, b.Y), 0, h);
            int thickness = Math.Min(y2 - y1, h - 1);
            _cutOutRect = new Rectangle(0, y1, w, thickness);
        }
        else
        {
            int x1 = Math.Clamp(Math.Min(a.X, b.X), 0, w);
            int x2 = Math.Clamp(Math.Max(a.X, b.X), 0, w);
            int thickness = Math.Min(x2 - x1, w - 1);
            _cutOutRect = new Rectangle(x1, 0, thickness, h);
        }
    }

    private void EndCutOutPointer()
    {
        bool wasNew = _activeCutOutHandle == -1;
        bool wasResized = _activeCutOutHandle is CutOutHandleNear or CutOutHandleFar;
        _cutOutDragging = false;
        if (wasNew)
            ApplyNewCutOutRect(_dragStartImg, _dragLastImg);

        _cutOutHasRect = CutOutStripIsValid;
        if (!_cutOutHasRect)
            _cutOutRect = Rectangle.Empty;

        _activeCutOutHandle = -1;
        Invalidate();
        OnStateChanged();

        if (!_cutOutHasRect)
            ShowToolBanner(LocalizationService.Translate("Cut Out canceled"));
        else if (wasNew || wasResized)
            ShowToolBanner(LocalizationService.Translate("Enter / Double-click to confirm"), sticky: true);
    }

    private int HitTestCutOutHandle(Point screenPt)
    {
        if (!_cutOutHasRect || _cutOutRect.Width <= 0 || _cutOutRect.Height <= 0)
            return -1;

        var stripScreen = ImageToScreenRect(_cutOutRect);
        var handles = GetCutOutHandlePositionsScreen(stripScreen);
        const float hitRadius = 7f;
        for (int i = 0; i < handles.Length; i++)
        {
            var h = handles[i];
            if (Math.Abs(screenPt.X - h.X) <= hitRadius && Math.Abs(screenPt.Y - h.Y) <= hitRadius)
                return i;
        }

        if (stripScreen.Contains(screenPt))
            return CutOutHandleMove;
        return -1;
    }

    private PointF[] GetCutOutHandlePositionsScreen(RectangleF rect)
    {
        float midX = rect.Left + rect.Width / 2f;
        float midY = rect.Top + rect.Height / 2f;
        if (_cutOutHorizontal)
        {
            return new PointF[]
            {
                new(midX, rect.Top),
                new(midX, rect.Bottom),
            };
        }
        return new PointF[]
        {
            new(rect.Left, midY),
            new(rect.Right, midY),
        };
    }

    private Cursor GetCutOutCursor(Point screenPt)
    {
        if (_cutOutDragging)
        {
            return _activeCutOutHandle switch
            {
                CutOutHandleNear or CutOutHandleFar =>
                    _cutOutHorizontal ? Cursors.SizeNS : Cursors.SizeWE,
                CutOutHandleMove => Cursors.SizeAll,
                _ => CursorFactory.PrecisionCursor,
            };
        }

        if (!_cutOutHasRect)
            return CursorFactory.PrecisionCursor;

        int hit = HitTestCutOutHandle(screenPt);
        return hit switch
        {
            CutOutHandleNear or CutOutHandleFar =>
                _cutOutHorizontal ? Cursors.SizeNS : Cursors.SizeWE,
            CutOutHandleMove => Cursors.SizeAll,
            _ => CursorFactory.PrecisionCursor,
        };
    }

    private void RenderCutOutOverlay(Graphics g)
    {
        if (!IsCutOutOverlayActive) return;
        if (!_cutOutDragging && !_cutOutHasRect) return;
        if (_baseBitmap is null) return;
        if (_cutOutRect.Width <= 0 || _cutOutRect.Height <= 0) return;

        var imgRect = ImageToScreenRect(new RectangleF(0, 0, _baseBitmap.Width, _baseBitmap.Height));
        var stripScreen = ImageToScreenRect(_cutOutRect);

        using (var dark = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
            g.FillRectangle(dark, stripScreen);

        using (var shadowPen = new Pen(Color.FromArgb(120, 0, 0, 0), 1.5f))
        using (var borderPen = new Pen(Color.FromArgb(255, 0, 255, 255), 1.5f) { DashStyle = DashStyle.Dash })
        {
            if (_cutOutHorizontal)
            {
                g.DrawLine(shadowPen, stripScreen.Left, stripScreen.Top + 1f, stripScreen.Right, stripScreen.Top + 1f);
                g.DrawLine(shadowPen, stripScreen.Left, stripScreen.Bottom + 1f, stripScreen.Right, stripScreen.Bottom + 1f);
                g.DrawLine(borderPen, stripScreen.Left, stripScreen.Top, stripScreen.Right, stripScreen.Top);
                g.DrawLine(borderPen, stripScreen.Left, stripScreen.Bottom, stripScreen.Right, stripScreen.Bottom);
            }
            else
            {
                g.DrawLine(shadowPen, stripScreen.Left + 1f, stripScreen.Top, stripScreen.Left + 1f, stripScreen.Bottom);
                g.DrawLine(shadowPen, stripScreen.Right + 1f, stripScreen.Top, stripScreen.Right + 1f, stripScreen.Bottom);
                g.DrawLine(borderPen, stripScreen.Left, stripScreen.Top, stripScreen.Left, stripScreen.Bottom);
                g.DrawLine(borderPen, stripScreen.Right, stripScreen.Top, stripScreen.Right, stripScreen.Bottom);
            }
        }

        bool showHandles = _cutOutHasRect && (_preSpaceTool == null || _preSpaceTool == CanvasTool.CutOut);
        if (showHandles)
            DrawCutOutHandles(g, stripScreen);

        int thickness = _cutOutHorizontal ? _cutOutRect.Height : _cutOutRect.Width;
        DrawCutOutSizeBadge(g, stripScreen, $"-{thickness} px");
    }

    private void DrawCutOutHandles(Graphics g, RectangleF rect)
    {
        var accent = Color.FromArgb(255, 0, 255, 255);
        var shadow = Color.FromArgb(100, 0, 0, 0);
        using var thickPen = new Pen(accent, 3.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var shadowPen = new Pen(shadow, 5.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        const float barLen = 14f;

        foreach (var h in GetCutOutHandlePositionsScreen(rect))
        {
            if (_cutOutHorizontal)
            {
                g.DrawLine(shadowPen, h.X - barLen / 2f, h.Y, h.X + barLen / 2f, h.Y);
                g.DrawLine(thickPen, h.X - barLen / 2f, h.Y, h.X + barLen / 2f, h.Y);
            }
            else
            {
                g.DrawLine(shadowPen, h.X, h.Y - barLen / 2f, h.X, h.Y + barLen / 2f);
                g.DrawLine(thickPen, h.X, h.Y - barLen / 2f, h.X, h.Y + barLen / 2f);
            }
        }
    }

    private static void DrawCutOutSizeBadge(Graphics g, RectangleF stripScreen, string text)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        using var font = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point);
        var size = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), flags);
        const int padX = 8;
        const int padY = 4;
        int bw = size.Width + padX * 2;
        int bh = size.Height + padY * 2;
        int bx = (int)Math.Round(stripScreen.X + stripScreen.Width / 2f - bw / 2f);
        int by = (int)Math.Round(stripScreen.Y + stripScreen.Height / 2f - bh / 2f);

        var oldSmooth = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Color.FromArgb(220, 10, 14, 22)))
        using (var path = RoundedRect(new RectangleF(bx, by, bw, bh), 5f))
            g.FillPath(bg, path);
        g.SmoothingMode = oldSmooth;

        TextRenderer.DrawText(
            g, text, font,
            new Rectangle(bx + padX, by + padY, size.Width, size.Height),
            Color.FromArgb(255, 0, 255, 255),
            flags);
    }
}
