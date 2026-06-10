using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace CyberSnap.Models.Commands;

/// <summary>
/// Replaces the BaseBitmap with a sub-rectangle and translates existing annotations.
/// Revert restores both the original bitmap and the original annotation positions.
/// Ownership: this command stores private copies (clones) of the before/after bitmaps
/// and passes new clones to the context on each Apply/Revert, so the canvas can
/// freely dispose what it receives without affecting the command.
/// </summary>
public sealed class CropCommand : IEditCommand
{
    private readonly Rectangle _cropRect;
    private Bitmap? _beforeBitmap; // pristine copy of the bitmap pre-crop
    private Bitmap? _afterBitmap;  // pristine copy of the bitmap post-crop
    private List<Annotation>? _beforeAnnotations;
    private List<Annotation>? _afterAnnotations;
    private bool _disposed;

    public CropCommand(Rectangle cropRect)
    {
        _cropRect = cropRect;
    }

    public string Description => "Crop";

    public void Apply(IEditorContext ctx)
    {
        if (_disposed) return;

        var source = ctx.BaseBitmap;
        var rect = Rectangle.Intersect(_cropRect, new Rectangle(0, 0, source.Width, source.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        if (_afterBitmap is null)
        {
            _beforeBitmap = new Bitmap(source);
            _afterBitmap = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(_afterBitmap);
            g.DrawImage(source, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);

            _beforeAnnotations = new List<Annotation>(ctx.Annotations);
            _afterAnnotations = new List<Annotation>();

            foreach (var a in ctx.Annotations)
            {
                var bounds = AnnotationTransforms.GetBounds(a);
                if (bounds.IntersectsWith(rect))
                {
                    _afterAnnotations.Add(AnnotationTransforms.Translate(a, -rect.X, -rect.Y));
                }
            }
        }

        // Hand a clone to the context so the canvas's setter can dispose it freely.
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
}
