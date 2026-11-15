using System.Windows;
using System.Windows.Controls;
using CyberSnap.UI;

internal static partial class Program
{
    // Clicking a preview color button creates the picker, but the preview closes it again as soon
    // as it loses focus (CapturePreviewDialog.Background closes on Deactivated and LocationChanged
    // and nulls both fields). A live desktop can hand focus to another app at any moment, so
    // re-activate and click a bounded number of times instead of dereferencing the null it leaves.
    private static void OpenPreviewColorPicker(CapturePreviewDialog preview, Button button)
    {
        for (int attempt = 0; attempt < 5 && Field(preview, "_previewBackgroundColorPicker") is null; attempt++)
        {
            preview.Activate();
            Pump();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }
        Assert(Field(preview, "_previewBackgroundColorPicker") is not null,
            $"the preview color picker opens from {button.Name}");
    }
}
