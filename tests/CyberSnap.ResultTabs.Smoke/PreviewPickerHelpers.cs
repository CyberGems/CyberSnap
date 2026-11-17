using System.Windows;
using System.Windows.Controls;
using CyberSnap.UI;

internal static partial class Program
{
    // WPF only hands out keyboard focus while the owning window is active, and a live desktop can
    // give the foreground to another app at any moment, so activate the picker's owner and retry a
    // bounded number of times before requiring focus.
    private static void FocusWithOwnerActive(FrameworkElement picker, UIElement field, string description)
    {
        var owner = Window.GetWindow(picker);
        bool focused = field.Focus();
        for (int attempt = 0; attempt < 5 && !focused; attempt++)
        {
            if (owner is not null) owner.Activate();
            Pump();
            Thread.Sleep(20);
            focused = field.Focus();
        }
        Assert(focused, description + $" (ownerActive={owner?.IsActive})");
    }

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
