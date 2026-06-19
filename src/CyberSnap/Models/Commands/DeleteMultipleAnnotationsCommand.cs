namespace CyberSnap.Models.Commands;

/// <summary>Removes multiple annotations at once; Revert reinserts them at their original indices.</summary>
public sealed class DeleteMultipleAnnotationsCommand : IEditCommand
{
    // Stored in descending index order so Apply can RemoveAt without shifting.
    private readonly (int Index, Annotation Annotation)[] _items;

    /// <param name="items">Pairs of (index, annotation) in any order; they are sorted internally.</param>
    public DeleteMultipleAnnotationsCommand(IEnumerable<(int Index, Annotation Annotation)> items)
    {
        _items = items.OrderByDescending(x => x.Index).ToArray();
    }

    public string Description => $"Delete {_items.Length} annotations";

    public void Apply(IEditorContext ctx)
    {
        // Remove from highest index to lowest so earlier indices stay valid.
        foreach (var (index, _) in _items)
        {
            if (index >= 0 && index < ctx.Annotations.Count)
                ctx.Annotations.RemoveAt(index);
        }
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        // Re-insert from lowest index to highest so each insert lands at the right spot.
        foreach (var (index, annotation) in _items.OrderBy(x => x.Index))
        {
            var clampedIndex = Math.Clamp(index, 0, ctx.Annotations.Count);
            ctx.Annotations.Insert(clampedIndex, annotation);
        }
        ctx.Invalidate();
    }

    public void Dispose() { }
}
