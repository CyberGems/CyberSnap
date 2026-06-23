using System.Linq;

namespace CyberSnap.Models.Commands;

/// <summary>Adds multiple annotations at once as a single undo step;
/// Revert removes them in reverse insertion order.</summary>
public sealed class AddMultipleAnnotationsCommand : IEditCommand
{
    private readonly Annotation[] _annotations;
    private int[]? _appliedIndices;

    public AddMultipleAnnotationsCommand(IEnumerable<Annotation> annotations)
    {
        _annotations = annotations.ToArray();
    }

    public string Description => $"Add {_annotations.Length} annotations";

    public void Apply(IEditorContext ctx)
    {
        _appliedIndices = new int[_annotations.Length];
        for (int i = 0; i < _annotations.Length; i++)
        {
            _appliedIndices[i] = ctx.Annotations.Count;
            ctx.Annotations.Add(_annotations[i]);
        }
        ctx.Invalidate();
    }

    public void Revert(IEditorContext ctx)
    {
        if (_appliedIndices == null) return;
        // Remove from the last applied index to the first so earlier indices stay valid.
        for (int i = _appliedIndices.Length - 1; i >= 0; i--)
        {
            int index = _appliedIndices[i];
            if (index >= 0 && index < ctx.Annotations.Count
                && ReferenceEquals(ctx.Annotations[index], _annotations[i]))
            {
                ctx.Annotations.RemoveAt(index);
            }
        }
        _appliedIndices = null;
        ctx.Invalidate();
    }

    public void Dispose() { }
}