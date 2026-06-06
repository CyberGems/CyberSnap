using System;
using System.Collections.Generic;
using System.Drawing;

namespace CyberSnap.Models.Commands;

public sealed class PasteImageCommand : IEditCommand
{
    private Bitmap? _beforeBitmap;
    private Bitmap? _afterBitmap;
    private readonly List<Annotation> _beforeAnnotations = new();
    private bool _disposed;

    public PasteImageCommand(Bitmap pastedImage)
    {
        _afterBitmap = new Bitmap(pastedImage);
    }

    public string Description => "Paste Image";

    public void Apply(IEditorContext ctx)
    {
        if (_disposed || _afterBitmap is null) return;

        if (_beforeBitmap is null)
        {
            _beforeBitmap = new Bitmap(ctx.BaseBitmap);
            _beforeAnnotations.AddRange(ctx.Annotations);
        }

        ctx.BaseBitmap = new Bitmap(_afterBitmap);
        ctx.Annotations.Clear();
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        if (_disposed || _beforeBitmap is null) return;

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
