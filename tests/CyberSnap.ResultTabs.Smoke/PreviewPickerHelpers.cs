using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CyberSnap.UI;

internal static partial class Program
{
    // WPF only hands out keyboard focus while the owning window is active, and a live desktop can
    // give the foreground to another app at any moment, so activate the picker's owner and retry a
    // bounded number of times before requiring focus.
    private static void FocusWithOwnerActive(FrameworkElement picker, UIElement field, string description)
        => FocusWithOwnerActive(Window.GetWindow(picker), field, description);

    // Popup content does not resolve through Window.GetWindow when its tree is not rooted in a
    // window, so callers that hold the owner pass it explicitly.
    private static void FocusWithOwnerActive(Window owner, UIElement field, string description)
    {
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
    // Returns the picker the preview currently hosts, reopening it only when it is closed.
    private static FrameworkElement OpenPreviewColorPicker(CapturePreviewDialog preview, Button button)
    {
        Popup? popup = null;
        FrameworkElement? picker = null;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            popup = Field(preview, "_previewBackgroundColorPopup") as Popup;
            picker = Field(preview, "_previewBackgroundColorPicker") as FrameworkElement;
            if (popup is { IsOpen: true } && picker is not null) break;
            preview.Activate();
            Pump();
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
        }
        Assert(popup is { IsOpen: true } && picker is not null,
            $"the preview color picker opens from {button.Name} (active={preview.IsActive})");
        return picker!;
    }
}
