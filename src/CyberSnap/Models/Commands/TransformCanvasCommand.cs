using System.Collections.Generic;
using System.Drawing;

namespace CyberSnap.Models.Commands;

/// <summary>Canvas-level rotate / flip operation applied to the entire document.</summary>
public enum CanvasTransformKind
{
    Rotate90Clockwise,
    Rotate90CounterClockwise,
    Rotate180,
    FlipHorizontal,
    FlipVertical,
}

/// <summary>
/// Rotates or flips the canvas. Annotations are always <b>flattened</b> (baked into the
/// base bitmap) so geometry stays correct without per-type transform edge cases.
/// Ownership matches <see cref="CropCommand"/>: private before/after clones, and a fresh
/// clone is handed to the context on each Apply/Revert.
/// </summary>
public sealed class TransformCanvasCommand : IEditCommand
{
    private readonly CanvasTransformKind _kind;

    private Bitmap? _beforeBitmap;
    private Bitmap? _afterBitmap;
    private List<Annotation>? _beforeAnnotations;
    private List<Annotation>? _afterAnnotations;
    private bool _disposed;

    public TransformCanvasCommand(CanvasTransformKind kind)
    {
        _kind = kind;
    }

    public string Description => _kind switch
    {
        CanvasTransformKind.FlipHorizontal or CanvasTransformKind.FlipVertical => "Flip canvas",
        _ => "Rotate canvas",
    };

    public void Apply(IEditorContext ctx)
    {
        if (_disposed) return;

        if (_afterBitmap is null)
        {
            var source = ctx.BaseBitmap;
            _beforeBitmap = new Bitmap(source);
            _beforeAnnotations = new List<Annotation>(ctx.Annotations);

            bool isBlank = ctx is CyberSnap.UI.Controls.AnnotationCanvas blankCanvas
                && blankCanvas.IsBlankCanvas
                && blankCanvas.BlankBitmapFactory is not null;
            bool hasAnnotations = ctx.Annotations.Count > 0;

            if (isBlank && !hasAnnotations)
            {
                // Empty checkerboard: regenerate at the post-transform size so the pattern
                // stays clean (RotateFlip would also work, but regeneration matches resize).
                var (nw, nh) = OutputSize(source.Width, source.Height, _kind);
                _afterBitmap = ((CyberSnap.UI.Controls.AnnotationCanvas)ctx)
                    .BlankBitmapFactory!(nw, nh);
            }
            else
            {
                // Flatten annotations into pixels (or clone the base when there are none),
                // then apply the GDI+ RotateFlip transform in place.
                Bitmap working;
                if (hasAnnotations && ctx is CyberSnap.UI.Controls.AnnotationCanvas canvas)
                    working = canvas.RenderFinal();
                else
                    working = new Bitmap(source);

                working.RotateFlip(ToRotateFlipType(_kind));
                _afterBitmap = working;
            }

            _afterAnnotations = new List<Annotation>();
        }

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

    private static void RefitView(IEditorContext ctx)
    {
        if (ctx is CyberSnap.UI.Controls.AnnotationCanvas canvas)
            canvas.ZoomFit();
    }

    private static RotateFlipType ToRotateFlipType(CanvasTransformKind kind) => kind switch
    {
        CanvasTransformKind.Rotate90Clockwise => RotateFlipType.Rotate90FlipNone,
        CanvasTransformKind.Rotate90CounterClockwise => RotateFlipType.Rotate270FlipNone,
        CanvasTransformKind.Rotate180 => RotateFlipType.Rotate180FlipNone,
        CanvasTransformKind.FlipHorizontal => RotateFlipType.RotateNoneFlipX,
        CanvasTransformKind.FlipVertical => RotateFlipType.RotateNoneFlipY,
        _ => RotateFlipType.RotateNoneFlipNone,
    };

    /// <summary>Pixel size of the canvas after the transform (only 90° swaps W/H).</summary>
    internal static (int Width, int Height) OutputSize(int width, int height, CanvasTransformKind kind) =>
        kind is CanvasTransformKind.Rotate90Clockwise or CanvasTransformKind.Rotate90CounterClockwise
            ? (height, width)
            : (width, height);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _beforeBitmap?.Dispose();
        _afterBitmap?.Dispose();
    }
}
