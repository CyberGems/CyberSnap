namespace CyberSnap.Models.Commands;

/// <summary>Replaces one annotation at a known index; Revert restores the original.</summary>
public sealed class ReplaceAnnotationCommand : IEditCommand
{
    private readonly int _index;
    private readonly Annotation _original;
    private readonly Annotation _replacement;

    public ReplaceAnnotationCommand(int index, Annotation original, Annotation replacement)
    {
        _index = index;
        _original = original;
        _replacement = replacement;
    }

    public string Description => $"Replace {_original.GetType().Name}";

    public void Apply(IEditorContext ctx)
    {
        if (_index < 0 || _index >= ctx.Annotations.Count)
            return;

        ctx.Annotations[_index] = _replacement;
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        if (_index < 0 || _index >= ctx.Annotations.Count)
            return;

        ctx.Annotations[_index] = _original;
        ctx.Invalidate();
    }

    public void Dispose() { }
}
