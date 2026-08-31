using System.IO;
using CyberSnap.Services;
using CyberSnap.UI.Controls;

namespace CyberSnap.UI.Editor;

/// <summary>
/// One open editor document (canvas + save path). The form keeps a list of these
/// and shows a tab strip only when more than one is open.
/// </summary>
internal sealed class EditorDocument : IDisposable
{
    public EditorDocument(AnnotationCanvas canvas, string? savedFilePath)
    {
        Canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
        SavedFilePath = savedFilePath;
    }

    public AnnotationCanvas Canvas { get; }

    public string? SavedFilePath { get; set; }

    public bool IsDirty => Canvas is { IsDisposed: false, IsDirty: true, IsDefaultBlank: false };

    public string TabTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SavedFilePath))
                return Path.GetFileName(SavedFilePath);
            return LocalizationService.Translate("Untitled");
        }
    }

    public void Dispose()
    {
        if (!Canvas.IsDisposed)
            Canvas.Dispose();
    }
}
