namespace CyberSnap.Models.Commands;

/// <summary>Removes an annotation at a known index; Revert reinserts it at the same index.</summary>
public sealed class DeleteAnnotationCommand : IEditCommand
{
    private readonly int _index;
    private readonly Annotation _annotation;

    public DeleteAnnotationCommand(int index, Annotation annotation)
    {
        _index = index;
        _annotation = annotation;
    }

    public string Description => $"Delete {_annotation.GetType().Name}";

    public void Apply(IEditorContext ctx)
    {
        if (_index < 0 || _index >= ctx.Annotations.Count)
            return;

        ctx.Annotations.RemoveAt(_index);
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        var clampedIndex = Math.Clamp(_index, 0, ctx.Annotations.Count);
        ctx.Annotations.Insert(clampedIndex, _annotation);
        ctx.Invalidate();
    }

    public void Dispose() { }
}
