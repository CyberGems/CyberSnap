using System.Drawing;

namespace CyberSnap.Models.Commands;

/// <summary>Translates an existing annotation by (dx, dy). Revert restores the original.</summary>
public sealed class TransformAnnotationCommand : IEditCommand
{
    private readonly Annotation _original;
    private readonly int _index;
    private readonly int _dx;
    private readonly int _dy;

    public TransformAnnotationCommand(Annotation original, int index, int dx, int dy)
    {
        _original = original;
        _index = index;
        _dx = dx;
        _dy = dy;
    }

    public string Description => "Move annotation";

    public void Apply(IEditorContext ctx)
    {
        if (_index < 0 || _index >= ctx.Annotations.Count) return;
        ctx.Annotations[_index] = AnnotationTransforms.Translate(_original, _dx, _dy);
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        if (_index < 0 || _index >= ctx.Annotations.Count) return;
        ctx.Annotations[_index] = _original;
        ctx.Invalidate();
    }

    public void Dispose() { }
}
