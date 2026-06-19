namespace CyberSnap.Models.Commands;

/// <summary>Translates multiple annotations by (dx, dy); Revert restores the originals.</summary>
public sealed class TransformMultipleAnnotationsCommand : IEditCommand
{
    private readonly (int Index, Annotation Original)[] _items;
    private readonly int _dx;
    private readonly int _dy;

    /// <param name="items">Pairs of (index, original annotation) in any order.</param>
    public TransformMultipleAnnotationsCommand(IEnumerable<(int Index, Annotation Original)> items, int dx, int dy)
    {
        _items = items.OrderBy(x => x.Index).ToArray();
        _dx = dx;
        _dy = dy;
    }

    public string Description => $"Move {_items.Length} annotations";

    public void Apply(IEditorContext ctx)
    {
        foreach (var (index, original) in _items)
        {
            if (index >= 0 && index < ctx.Annotations.Count)
                ctx.Annotations[index] = AnnotationTransforms.Translate(original, _dx, _dy);
        }
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        foreach (var (index, original) in _items)
        {
            if (index >= 0 && index < ctx.Annotations.Count)
                ctx.Annotations[index] = original;
        }
        ctx.Invalidate();
    }

    public void Dispose() { }
}
