using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace CyberSnap.Models.Commands;

/// <summary>Where the existing content is anchored when the canvas is resized without
/// scaling (canvas-size mode). Mirrors the 3×3 anchor grid in the resize dialog.</summary>
public enum AnchorPosition
{
    TopLeft, Top, TopRight,
    Left, Center, Right,
    BottomLeft, Bottom, BottomRight,
}

/// <summary>
/// Resizes the canvas to a new pixel size. Two modes:
///  • <b>Scale content</b> (resample): the bitmap and every annotation are stretched to
///    the new dimensions, like Photoshop's "Image Size".
///  • <b>Canvas size</b> (extend/trim): the bitmap is re-canvassed at the new size with the
///    old content anchored (transparent margin added, or content trimmed), without scaling,
///    like Photoshop's "Canvas Size". Annotations are translated and those that fall fully
///    outside the new bounds are dropped (same rule as <see cref="CropCommand"/>).
/// Ownership follows <see cref="CropCommand"/>: private before/after clones are kept and a
/// fresh clone is handed to the context on each Apply/Revert.
/// </summary>
public sealed class ResizeCanvasCommand : IEditCommand
{
    private readonly int _newWidth;
    private readonly int _newHeight;
    private readonly bool _scaleContent;
    private readonly AnchorPosition _anchor;
    private readonly bool _explicitOffset;
    private readonly int _offX;
    private readonly int _offY;

    private Bitmap? _beforeBitmap;
    private Bitmap? _afterBitmap;
    private List<Annotation>? _beforeAnnotations;
    private List<Annotation>? _afterAnnotations;
    private bool _disposed;

    public ResizeCanvasCommand(int newWidth, int newHeight, bool scaleContent, AnchorPosition anchor)
    {
        _newWidth = newWidth;
        _newHeight = newHeight;
        _scaleContent = scaleContent;
        _anchor = anchor;
    }

    /// <summary>Canvas-size resize with an explicit content origin in the new bitmap
    /// (where the old (0,0) lands). Used by on-canvas handle drags that may move more
    /// than one edge before confirming.</summary>
    public ResizeCanvasCommand(int newWidth, int newHeight, bool scaleContent, int contentOffsetX, int contentOffsetY)
    {
        _newWidth = newWidth;
        _newHeight = newHeight;
        _scaleContent = scaleContent;
        _anchor = AnchorPosition.TopLeft;
        _explicitOffset = true;
        _offX = contentOffsetX;
        _offY = contentOffsetY;
    }

    public string Description => "Resize canvas";

    public void Apply(IEditorContext ctx)
    {
        if (_disposed) return;

        var source = ctx.BaseBitmap;
        if (_newWidth <= 0 || _newHeight <= 0) return;
        bool sameSize = _newWidth == source.Width && _newHeight == source.Height;
        if (sameSize && (_scaleContent || !_explicitOffset || (_offX == 0 && _offY == 0)))
            return;

        if (_afterBitmap is null)
        {
            _beforeBitmap = new Bitmap(source);
            _beforeAnnotations = new List<Annotation>(ctx.Annotations);

            // A blank checkerboard has no real pixels to resample/pad: regenerate a clean
            // pattern at the new size and skip painting the old bitmap over it (which would
            // double the pattern). Annotations drawn on top are still transformed normally.
            bool isBlank = ctx is CyberSnap.UI.Controls.AnnotationCanvas canvas
                && canvas.IsBlankCanvas && canvas.BlankBitmapFactory is not null;
            _afterBitmap = isBlank
                ? ((CyberSnap.UI.Controls.AnnotationCanvas)ctx).BlankBitmapFactory!(_newWidth, _newHeight)
                : new Bitmap(_newWidth, _newHeight, PixelFormat.Format32bppPArgb);
            _afterAnnotations = new List<Annotation>();

            using (var g = Graphics.FromImage(_afterBitmap))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                if (_scaleContent)
                {
                    if (!isBlank)
                        g.DrawImage(source, new Rectangle(0, 0, _newWidth, _newHeight),
                            new Rectangle(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);

                    var oldBounds = new Rectangle(0, 0, source.Width, source.Height);
                    var newBounds = new Rectangle(0, 0, _newWidth, _newHeight);
                    foreach (var a in ctx.Annotations)
                        _afterAnnotations.Add(AnnotationTransforms.Scale(a, oldBounds, newBounds));
                }
                else
                {
                    var (offX, offY) = ResolveOffset(source.Width, source.Height);
                    if (!isBlank)
                        g.DrawImage(source, offX, offY, source.Width, source.Height);

                    var newBounds = new Rectangle(0, 0, _newWidth, _newHeight);
                    foreach (var a in ctx.Annotations)
                    {
                        var moved = AnnotationTransforms.Translate(a, offX, offY);
                        if (AnnotationTransforms.GetBounds(moved).IntersectsWith(newBounds))
                            _afterAnnotations.Add(moved);
                    }
                }
            }
        }

        // Hand a clone to the context so the canvas's setter can dispose it freely.
        ctx.BaseBitmap = new Bitmap(_afterBitmap);
        ctx.Annotations.Clear();
        ctx.Annotations.AddRange(_afterAnnotations!);
        ctx.Invalidate();
        RefitView(ctx);
    }

    public void Revert(IEditorContext ctx)
    {
        if (_disposed || _beforeBitmap is null || _beforeAnnotations is null) return;

        ctx.BaseBitmap = new Bitmap(_beforeBitmap);
        ctx.Annotations.Clear();
        ctx.Annotations.AddRange(_beforeAnnotations);
        ctx.Invalidate();
        RefitView(ctx);
    }

    // The canvas size changes, so re-frame the view to fit; otherwise an undone/redone resize
    // leaves the image at a stale zoom and looks like nothing happened.
    private static void RefitView(IEditorContext ctx)
    {
        if (ctx is CyberSnap.UI.Controls.AnnotationCanvas canvas)
            canvas.Invalidate();
    }

    private (int offX, int offY) ResolveOffset(int oldW, int oldH) =>
        _explicitOffset
            ? (_offX, _offY)
            : AnchorOffset(oldW, oldH, _newWidth, _newHeight, _anchor);

    private static (int offX, int offY) AnchorOffset(int oldW, int oldH, int newW, int newH, AnchorPosition anchor)
    {
        int dw = newW - oldW;
        int dh = newH - oldH;
        int offX = anchor switch
        {
            AnchorPosition.TopLeft or AnchorPosition.Left or AnchorPosition.BottomLeft => 0,
            AnchorPosition.Top or AnchorPosition.Center or AnchorPosition.Bottom => dw / 2,
            _ => dw, // Right column
        };
        int offY = anchor switch
        {
            AnchorPosition.TopLeft or AnchorPosition.Top or AnchorPosition.TopRight => 0,
            AnchorPosition.Left or AnchorPosition.Center or AnchorPosition.Right => dh / 2,
            _ => dh, // Bottom row
        };
        return (offX, offY);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _beforeBitmap?.Dispose();
        _afterBitmap?.Dispose();
    }
}
