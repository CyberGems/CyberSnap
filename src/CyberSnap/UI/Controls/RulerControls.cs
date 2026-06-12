using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using CyberSnap.UI.Editor;
using CyberSnap.Services;

namespace CyberSnap.UI.Controls;

public abstract class EditorRuler : UserControl
{
    protected readonly AnnotationCanvas _canvas;
    protected Point? _currentMouseImgPos;

    protected EditorRuler(AnnotationCanvas canvas)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = EditorColors.BgPrimary;

        _canvas.StateChanged += (s, e) => Invalidate();
        _canvas.MouseMove += (s, e) =>
        {
            var imgPos = _canvas.PointFromScreenToImage(e.Location);
            if (_currentMouseImgPos != imgPos)
            {
                _currentMouseImgPos = imgPos;
                Invalidate();
            }
        };
        _canvas.MouseLeave += (s, e) =>
        {
            if (_currentMouseImgPos != null)
            {
                _currentMouseImgPos = null;
                Invalidate();
            }
        };
    }

    protected static double GetMajorStep(double zoom)
    {
        // Allowed major step divisions (in image pixels)
        double[] steps = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
        foreach (var s in steps)
        {
            if (s * zoom >= 50.0)
                return s;
        }
        return steps[^1];
    }

    protected static (double medium, double minor) GetSubdivisions(double major)
    {
        return major switch
        {
            1 => (0.5, 0.1),
            2 => (1, 0.2),
            5 => (2.5, 0.5),
            10 => (5, 1),
            20 => (10, 2),
            50 => (25, 5),
            _ => (major / 2.0, major / 10.0)
        };
    }
}

public sealed class HorizontalRuler : EditorRuler
{
    private bool _isDraggingNewGuide = false;

    public HorizontalRuler(AnnotationCanvas canvas) : base(canvas)
    {
        Height = 28;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && _canvas.BaseBitmap != null)
        {
            _isDraggingNewGuide = true;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDraggingNewGuide)
        {
            Point screenPt = PointToScreen(e.Location);
            Point canvasPt = _canvas.PointToClient(screenPt);
            Point imgPt = _canvas.PointFromScreenToImage(canvasPt);
            _canvas.DraggingTempHorizontalGuide = imgPt.Y;
            _canvas.Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isDraggingNewGuide)
        {
            _isDraggingNewGuide = false;
            Capture = false;

            float? tempY = _canvas.DraggingTempHorizontalGuide;
            _canvas.DraggingTempHorizontalGuide = null;

            Point screenPt = PointToScreen(e.Location);
            Point canvasPt = _canvas.PointToClient(screenPt);

            if (tempY.HasValue)
            {
                bool added = false;
                if (_canvas.ClientRectangle.Contains(canvasPt))
                {
                    Point imgPt = _canvas.PointFromScreenToImage(canvasPt);
                    if (imgPt.Y >= 0 && imgPt.Y <= _canvas.BaseBitmap.Height)
                    {
                        _canvas.AddHorizontalGuide(tempY.Value);
                        added = true;
                    }
                }
                if (!added)
                {
                    _canvas.ShowToolBanner(LocalizationService.Translate("Place guides inside the canvas"));
                }
            }
            _canvas.Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (_canvas.BaseBitmap == null) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        double zoom = _canvas.Zoom;
        float panX = _canvas.Pan.X;

        double majorStep = GetMajorStep(zoom);
        var (mediumStep, minorStep) = GetSubdivisions(majorStep);

        // Find visible image X range
        double imgStart = (0 - panX) / zoom;
        double imgEnd = (Width - panX) / zoom;

        // Clamp boundaries to image bounds
        imgStart = Math.Max(0, imgStart);
        imgEnd = Math.Min(_canvas.BaseBitmap.Width, imgEnd);

        // Round start down to nearest minor step
        double valStart = Math.Floor(imgStart / minorStep) * minorStep;
        double valEnd = Math.Ceiling(imgEnd / minorStep) * minorStep;

        using var tickPen = new Pen(EditorColors.TextMuted, 1f);
        using var font = new Font("Segoe UI Variable Text", 7.5f, FontStyle.Regular);
        using var textBrush = new SolidBrush(EditorColors.TextSecondary);

        // Draw ticks
        for (double val = valStart; val <= valEnd; val += minorStep)
        {
            float x = (float)(val * zoom + panX);
            if (x < 0 || x > Width) continue;

            float tickLen = 3f;
            bool isMajor = Math.Abs(val % majorStep) < 0.0001 || Math.Abs((val % majorStep) - majorStep) < 0.0001;
            bool isMedium = !isMajor && (Math.Abs(val % mediumStep) < 0.0001 || Math.Abs((val % mediumStep) - mediumStep) < 0.0001);

            if (isMajor)
            {
                tickLen = 10f;
                // Draw label
                string text = ((int)Math.Round(val)).ToString();
                var size = g.MeasureString(text, font);
                g.DrawString(text, font, textBrush, x - size.Width / 2f, 4);
            }
            else if (isMedium)
            {
                tickLen = 6f;
            }

            g.DrawLine(tickPen, x, Height - tickLen, x, Height);
        }

        // Base line at the bottom
        using var borderPen = new Pen(EditorColors.BorderSubtle, 1f);
        g.DrawLine(borderPen, 0, Height - 1, Width, Height - 1);

        // Draw mouse tracking indicator
        if (_currentMouseImgPos.HasValue)
        {
            float cursorX = (float)(_currentMouseImgPos.Value.X * zoom + panX);
            if (cursorX >= 0 && cursorX <= Width)
            {
                using var trackingPen = new Pen(Color.FromArgb(180, EditorColors.Accent), 1f);
                g.DrawLine(trackingPen, cursorX, 0, cursorX, Height);
            }
        }
    }
}

public sealed class VerticalRuler : EditorRuler
{
    private bool _isDraggingNewGuide = false;

    public VerticalRuler(AnnotationCanvas canvas) : base(canvas)
    {
        Width = 28;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && _canvas.BaseBitmap != null)
        {
            _isDraggingNewGuide = true;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDraggingNewGuide)
        {
            Point screenPt = PointToScreen(e.Location);
            Point canvasPt = _canvas.PointToClient(screenPt);
            Point imgPt = _canvas.PointFromScreenToImage(canvasPt);
            _canvas.DraggingTempVerticalGuide = imgPt.X;
            _canvas.Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isDraggingNewGuide)
        {
            _isDraggingNewGuide = false;
            Capture = false;

            float? tempX = _canvas.DraggingTempVerticalGuide;
            _canvas.DraggingTempVerticalGuide = null;

            Point screenPt = PointToScreen(e.Location);
            Point canvasPt = _canvas.PointToClient(screenPt);

            if (tempX.HasValue)
            {
                bool added = false;
                if (_canvas.ClientRectangle.Contains(canvasPt))
                {
                    Point imgPt = _canvas.PointFromScreenToImage(canvasPt);
                    if (imgPt.X >= 0 && imgPt.X <= _canvas.BaseBitmap.Width)
                    {
                        _canvas.AddVerticalGuide(tempX.Value);
                        added = true;
                    }
                }
                if (!added)
                {
                    _canvas.ShowToolBanner(LocalizationService.Translate("Place guides inside the canvas"));
                }
            }
            _canvas.Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        if (_canvas.BaseBitmap == null) return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        double zoom = _canvas.Zoom;
        float panY = _canvas.Pan.Y;

        double majorStep = GetMajorStep(zoom);
        var (mediumStep, minorStep) = GetSubdivisions(majorStep);

        // Find visible image Y range
        double imgStart = (0 - panY) / zoom;
        double imgEnd = (Height - panY) / zoom;

        // Clamp boundaries to image bounds
        imgStart = Math.Max(0, imgStart);
        imgEnd = Math.Min(_canvas.BaseBitmap.Height, imgEnd);

        // Round start down to nearest minor step
        double valStart = Math.Floor(imgStart / minorStep) * minorStep;
        double valEnd = Math.Ceiling(imgEnd / minorStep) * minorStep;

        using var tickPen = new Pen(EditorColors.TextMuted, 1f);
        using var font = new Font("Segoe UI Variable Text", 7.5f, FontStyle.Regular);
        using var textBrush = new SolidBrush(EditorColors.TextSecondary);

        // Draw ticks
        for (double val = valStart; val <= valEnd; val += minorStep)
        {
            float y = (float)(val * zoom + panY);
            if (y < 0 || y > Height) continue;

            float tickLen = 3f;
            bool isMajor = Math.Abs(val % majorStep) < 0.0001 || Math.Abs((val % majorStep) - majorStep) < 0.0001;
            bool isMedium = !isMajor && (Math.Abs(val % mediumStep) < 0.0001 || Math.Abs((val % mediumStep) - mediumStep) < 0.0001);

            if (isMajor)
            {
                tickLen = 10f;
                string text = ((int)Math.Round(val)).ToString();
                var size = g.MeasureString(text, font);
                
                // Draw numbers rotated 270 degrees (pointing vertically up)
                var state = g.Save();
                g.TranslateTransform(Width - tickLen - size.Height / 2f - 3, y);
                g.RotateTransform(-90);
                g.DrawString(text, font, textBrush, -size.Width / 2f, -size.Height / 2f);
                g.Restore(state);
            }
            else if (isMedium)
            {
                tickLen = 6f;
            }

            g.DrawLine(tickPen, Width - tickLen, y, Width, y);
        }

        // Base line at the right
        using var borderPen = new Pen(EditorColors.BorderSubtle, 1f);
        g.DrawLine(borderPen, Width - 1, 0, Width - 1, Height);

        // Draw mouse tracking indicator
        if (_currentMouseImgPos.HasValue)
        {
            float cursorY = (float)(_currentMouseImgPos.Value.Y * zoom + panY);
            if (cursorY >= 0 && cursorY <= Height)
            {
                using var trackingPen = new Pen(Color.FromArgb(180, EditorColors.Accent), 1f);
                g.DrawLine(trackingPen, 0, cursorY, Width, cursorY);
            }
        }
    }
}

public sealed class RulerCornerBlock : UserControl
{
    private readonly AnnotationCanvas _canvas;
    private bool _isDraggingNewGuides = false;
    private bool _isHovered = false;

    public RulerCornerBlock(AnnotationCanvas canvas)
    {
        _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        Size = new Size(28, 28);
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = EditorColors.BgPrimary;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _isHovered = true;
        Cursor = Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _isHovered = false;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && _canvas.BaseBitmap != null)
        {
            _isDraggingNewGuides = true;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_isDraggingNewGuides)
        {
            Point screenPt = PointToScreen(e.Location);
            Point canvasPt = _canvas.PointToClient(screenPt);
            Point imgPt = _canvas.PointFromScreenToImage(canvasPt);
            _canvas.DraggingTempHorizontalGuide = imgPt.Y;
            _canvas.DraggingTempVerticalGuide = imgPt.X;
            _canvas.Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_isDraggingNewGuides)
        {
            _isDraggingNewGuides = false;
            Capture = false;

            float? tempY = _canvas.DraggingTempHorizontalGuide;
            float? tempX = _canvas.DraggingTempVerticalGuide;
            _canvas.DraggingTempHorizontalGuide = null;
            _canvas.DraggingTempVerticalGuide = null;

            Point screenPt = PointToScreen(e.Location);
            Point canvasPt = _canvas.PointToClient(screenPt);

            if (tempY.HasValue || tempX.HasValue)
            {
                bool addedHorizontal = false;
                bool addedVertical = false;

                if (_canvas.ClientRectangle.Contains(canvasPt))
                {
                    Point imgPt = _canvas.PointFromScreenToImage(canvasPt);
                    
                    if (tempY.HasValue && imgPt.Y >= 0 && imgPt.Y <= _canvas.BaseBitmap.Height)
                    {
                        _canvas.AddHorizontalGuide(tempY.Value);
                        addedHorizontal = true;
                    }
                    if (tempX.HasValue && imgPt.X >= 0 && imgPt.X <= _canvas.BaseBitmap.Width)
                    {
                        _canvas.AddVerticalGuide(tempX.Value);
                        addedVertical = true;
                    }
                }
                
                bool horizontalDiscarded = tempY.HasValue && !addedHorizontal;
                bool verticalDiscarded = tempX.HasValue && !addedVertical;
                
                if (horizontalDiscarded || verticalDiscarded)
                {
                    _canvas.ShowToolBanner(LocalizationService.Translate("Place guides inside the canvas"));
                }
            }
            _canvas.Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Subtle cross-border separating horizontal and vertical lines
        using var borderPen = new Pen(EditorColors.BorderSubtle, 1f);
        g.DrawLine(borderPen, Width - 1, 0, Width - 1, Height - 1);
        g.DrawLine(borderPen, 0, Height - 1, Width - 1, Height - 1);

        // Draw arrow icon pointing down-right towards the canvas
        Color arrowColor = _isHovered ? EditorColors.Accent : EditorColors.TextMuted;
        using var arrowPen = new Pen(arrowColor, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawLine(arrowPen, 9, 9, 17, 17);
        g.DrawLine(arrowPen, 17, 17, 12, 17);
        g.DrawLine(arrowPen, 17, 17, 17, 12);
    }
}
