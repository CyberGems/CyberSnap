using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CyberSnap.Capture;

/// <summary>Geometry in monitor-local physical pixels. Always samples the original frozen frame.</summary>
public sealed class ZoomViewport
{
    public const double MinimumZoom = 1.25;
    public const double MaximumZoom = 4;
    private PointF _pointer;
    private PointF _targetPointer;
    public Size Size { get; }
    public double Zoom { get; private set; }
    public double TargetZoom { get; private set; }
    public bool IsSelecting { get; private set; }

    public ZoomViewport(Size size, Point pointer, double initialZoom, bool animate)
    {
        if (size.Width < 1 || size.Height < 1) throw new ArgumentOutOfRangeException(nameof(size));
        Size = size;
        SetPointer(pointer);
        _pointer = _targetPointer;
        TargetZoom = NormalizeZoom(initialZoom);
        Zoom = animate ? 1 : TargetZoom;
    }

    public static double NormalizeZoom(double zoom) => double.IsFinite(zoom)
        ? Math.Clamp(zoom, MinimumZoom, MaximumZoom) : 2;

    public RectangleF SourceBounds => new(
        (float)((Size.Width - Size.Width / Zoom) * _pointer.X),
        (float)((Size.Height - Size.Height / Zoom) * _pointer.Y),
        (float)(Size.Width / Zoom), (float)(Size.Height / Zoom));

    public void SetPointer(Point point)
    {
        if (IsSelecting) return;
        _targetPointer = new PointF(Math.Clamp(point.X / (float)Math.Max(1, Size.Width - 1), 0, 1),
            Math.Clamp(point.Y / (float)Math.Max(1, Size.Height - 1), 0, 1));
    }

    public void ChangeZoom(double steps, bool animate)
    {
        if (IsSelecting || !double.IsFinite(steps)) return;
        TargetZoom = NormalizeZoom(TargetZoom * Math.Pow(1.2, Math.Clamp(steps, -20, 20)));
        if (!animate) Zoom = TargetZoom;
    }

    public bool Advance(double elapsedSeconds, bool animate)
    {
        if (IsSelecting) return false;
        double before = Zoom;
        var old = _pointer;
        double blend = 1 - Math.Exp(-Math.Clamp(elapsedSeconds, 0, 0.1) / 0.055);
        Zoom = animate ? Zoom + (TargetZoom - Zoom) * blend : TargetZoom;
        if (Math.Abs(TargetZoom - Zoom) < 0.0001) Zoom = TargetZoom;
        _pointer = new PointF((float)(_pointer.X + (_targetPointer.X - _pointer.X) * blend),
            (float)(_pointer.Y + (_targetPointer.Y - _pointer.Y) * blend));
        if (Math.Abs(_pointer.X - _targetPointer.X) < 0.00001f) _pointer.X = _targetPointer.X;
        if (Math.Abs(_pointer.Y - _targetPointer.Y) < 0.00001f) _pointer.Y = _targetPointer.Y;
        return before != Zoom || old != _pointer;
    }

    // Freeze the exact displayed transform, including an animation partway through its transition.
    public void BeginSelection() => IsSelecting = true;
    public void EndSelection()
    {
        IsSelecting = false;
        TargetZoom = Zoom;
        _targetPointer = _pointer;
    }

    public Rectangle Selection(Point start, Point end)
    {
        int x1 = Math.Clamp(start.X, 0, Size.Width), x2 = Math.Clamp(end.X, 0, Size.Width);
        int y1 = Math.Clamp(start.Y, 0, Size.Height), y2 = Math.Clamp(end.Y, 0, Size.Height);
        return Rectangle.FromLTRB(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }

    public void Draw(Graphics graphics, Bitmap original, bool smooth)
    {
        graphics.InterpolationMode = smooth ? InterpolationMode.HighQualityBicubic : InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY);
        var source = SourceBounds;
        graphics.DrawImage(original, new Rectangle(Point.Empty, Size), source.X, source.Y,
            source.Width, source.Height, GraphicsUnit.Pixel, attributes);
    }

    public Bitmap Render(Bitmap original, bool smooth)
    {
        var result = new Bitmap(Size.Width, Size.Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            Draw(graphics, original, smooth);
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
