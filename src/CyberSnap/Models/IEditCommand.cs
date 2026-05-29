using System.Drawing;

namespace CyberSnap.Models;

/// <summary>
/// Editor-side context exposed to commands so they can mutate the canvas state
/// without depending on the concrete control implementation.
/// </summary>
public interface IEditorContext
{
    Bitmap BaseBitmap { get; set; }
    List<Annotation> Annotations { get; }
    void Invalidate();
}

/// <summary>
/// A reversible edit applied to an <see cref="IEditorContext"/>. Pushed onto the
/// canvas undo stack; Revert restores the prior state on Ctrl+Z.
/// </summary>
public interface IEditCommand : IDisposable
{
    string Description { get; }
    void Apply(IEditorContext ctx);
    void Revert(IEditorContext ctx);
}
