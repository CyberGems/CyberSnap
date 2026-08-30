using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CyberSnap.Models.Commands;

/// <summary>
/// Removes a full-span internal strip and joins the remaining halves.
/// Horizontal: the strip spans the image width (rows are removed).
/// Vertical: the strip spans the image height (columns are removed).
/// Ownership matches <see cref="CropCommand"/>: private before/after clones,
/// and the canvas receives a fresh clone on each Apply/Revert.
/// </summary>
public sealed class CutOutCommand : IEditCommand
{
    private readonly Rectangle _strip;
    private readonly bool _horizontal;
    private Bitmap? _beforeBitmap;
    private Bitmap? _afterBitmap;
    private List<Annotation>? _beforeAnnotations;
    private List<Annotation>? _afterAnnotations;
    private bool _disposed;

    public CutOutCommand(Rectangle strip, bool horizontal)
    {
        _strip = strip;
        _horizontal = horizontal;
    }

    public string Description => "Cut Out";

    public void Apply(IEditorContext ctx)
    {
        if (_disposed) return;

        var source = ctx.BaseBitmap;
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var strip = Rectangle.Intersect(_strip, bounds);
        if (_horizontal)
            strip = new Rectangle(0, strip.Y, source.Width, strip.Height);
        else
            strip = new Rectangle(strip.X, 0, strip.Width, source.Height);

        int remaining = _horizontal
            ? source.Height - strip.Height
            : source.Width - strip.Width;
        if (strip.Width <= 0 || strip.Height <= 0 || remaining < 1)
            return;

        if (_afterBitmap is null)
        {
            _beforeBitmap = new Bitmap(source);
            _beforeAnnotations = new List<Annotation>(ctx.Annotations);

            bool isBlank = ctx is CyberSnap.UI.Controls.AnnotationCanvas blankCanvas
                && blankCanvas.IsBlankCanvas
                && blankCanvas.BlankBitmapFactory is not null
                && ctx.Annotations.Count == 0;

            if (isBlank)
            {
                // Empty checkerboard: regenerate a clean pattern at the post-cut size
                // instead of joining two halves of the old tiles (which would misalign).
                int nw = _horizontal ? source.Width : source.Width - strip.Width;
                int nh = _horizontal ? source.Height - strip.Height : source.Height;
                _afterBitmap = ((CyberSnap.UI.Controls.AnnotationCanvas)ctx)
                    .BlankBitmapFactory!(nw, nh);
                _afterAnnotations = new List<Annotation>();
            }
            else
            {
                _afterBitmap = _horizontal
                    ? BuildHorizontalJoin(source, strip)
                    : BuildVerticalJoin(source, strip);

                _afterAnnotations = new List<Annotation>();
                foreach (var a in ctx.Annotations)
                {
                    if (TryKeepAnnotation(a, strip, _horizontal, out var kept))
                        _afterAnnotations.Add(kept);
                }
            }
        }

        ctx.BaseBitmap = new Bitmap(_afterBitmap);
        ctx.Annotations.Clear();
        ctx.Annotations.AddRange(_afterAnnotations!);
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        if (_disposed || _beforeBitmap is null || _beforeAnnotations is null) return;

        ctx.BaseBitmap = new Bitmap(_beforeBitmap);
        ctx.Annotations.Clear();
        ctx.Annotations.AddRange(_beforeAnnotations);
        ctx.Invalidate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _beforeBitmap?.Dispose();
        _afterBitmap?.Dispose();
    }

    private static Bitmap BuildHorizontalJoin(Bitmap source, Rectangle strip)
    {
        int topH = strip.Y;
        int botH = source.Height - strip.Bottom;
        var after = new Bitmap(source.Width, topH + botH, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(after);
        PrepareJoinGraphics(g);
        if (topH > 0)
        {
            g.DrawImage(source,
                new Rectangle(0, 0, source.Width, topH),
                new Rectangle(0, 0, source.Width, topH),
                GraphicsUnit.Pixel);
        }
        if (botH > 0)
        {
            g.DrawImage(source,
                new Rectangle(0, topH, source.Width, botH),
                new Rectangle(0, strip.Bottom, source.Width, botH),
                GraphicsUnit.Pixel);
        }
        return after;
    }

    private static Bitmap BuildVerticalJoin(Bitmap source, Rectangle strip)
    {
        int leftW = strip.X;
        int rightW = source.Width - strip.Right;
        var after = new Bitmap(leftW + rightW, source.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(after);
        PrepareJoinGraphics(g);
        if (leftW > 0)
        {
            g.DrawImage(source,
                new Rectangle(0, 0, leftW, source.Height),
                new Rectangle(0, 0, leftW, source.Height),
                GraphicsUnit.Pixel);
        }
        if (rightW > 0)
        {
            g.DrawImage(source,
                new Rectangle(leftW, 0, rightW, source.Height),
                new Rectangle(strip.Right, 0, rightW, source.Height),
                GraphicsUnit.Pixel);
        }
        return after;
    }

    private static void PrepareJoinGraphics(Graphics g)
    {
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
    }

    private static bool TryKeepAnnotation(Annotation a, Rectangle strip, bool horizontal, out Annotation kept)
    {
        var bounds = AnnotationTransforms.GetBounds(a);
        if (bounds.IsEmpty)
        {
            kept = a;
            return true;
        }

        if (horizontal)
        {
            if (bounds.Bottom <= strip.Top)
            {
                kept = a;
                return true;
            }
            if (bounds.Top >= strip.Bottom)
            {
                kept = AnnotationTransforms.Translate(a, 0, -strip.Height);
                return true;
            }
        }
        else
        {
            if (bounds.Right <= strip.Left)
            {
                kept = a;
                return true;
            }
            if (bounds.Left >= strip.Right)
            {
                kept = AnnotationTransforms.Translate(a, -strip.Width, 0);
                return true;
            }
        }

        kept = a;
        return false;
    }
}
