namespace CyberSnap.Models.Commands;

/// <summary>Adds a single annotation to the canvas; Revert removes it.</summary>
public sealed class AddAnnotationCommand : IEditCommand
{
    private readonly Annotation _annotation;
    private int _appliedIndex = -1;

    public AddAnnotationCommand(Annotation annotation)
    {
        _annotation = annotation;
    }

    public Annotation Annotation => _annotation;

    public string Description => $"Add {_annotation.GetType().Name}";

    public void Apply(IEditorContext ctx)
    {
        _appliedIndex = ctx.Annotations.Count;
        ctx.Annotations.Add(_annotation);
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        if (_appliedIndex < 0 || _appliedIndex >= ctx.Annotations.Count)
            return;

        ctx.Annotations.RemoveAt(_appliedIndex);
        _appliedIndex = -1;
        ctx.Invalidate();
    }

    public void Dispose() { }
}
