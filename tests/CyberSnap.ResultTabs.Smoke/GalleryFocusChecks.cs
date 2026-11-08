using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.UI.Editor;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr ReadGalleryWindow(IntPtr hwnd, uint relationship);

    private static void CheckGalleryFocus(CyberSnap.App app, SettingsService settings)
    {
        using var history = new HistoryService();
        using var search = new ImageSearchIndexService();
        // Use empty services and leave the user's saved Gallery and captures alone.
        SetZoomAppField(app, "_historyService", history);
        SetZoomAppField(app, "_imageSearchIndexService", search);
        SetZoomAppField(app, "_historyMaintenanceScheduled", true);
        settings.Settings.AutoIndexImages = false;

        using var bitmap = new System.Drawing.Bitmap(128, 128);
        using var editor = new EditorForm(bitmap);
        var instance = typeof(EditorForm).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        instance.SetValue(null, editor);
        try
        {
            editor.Show();
            AwaitCondition(() => editor.Visible && editor.Opacity == 1, "Editor did not finish opening");
            editor.Activate();
            AwaitCondition(() => GetForegroundWindow() == editor.Handle, "The fixture could not activate the Editor", 5000);
            var galleryButton = GetField<Forms.Button>(editor, "_galleryButton");
            galleryButton.PerformClick();
            HistoryWindow? gallery = null;
            AwaitCondition(() => (gallery = ZoomAppField(app, "_historyWindow") as HistoryWindow) is { IsVisible: true },
                "The Editor's Gallery button did not open the Gallery");
            IntPtr galleryHandle = new WindowInteropHelper(gallery!).Handle;
            Assert(ReadGalleryWindow(galleryHandle, 4 /* GW_OWNER */) == IntPtr.Zero && !gallery!.Topmost,
                "the Gallery opens as an independent window");
            AwaitCondition(() => GetForegroundWindow() == galleryHandle, "The new Gallery did not come to the foreground", 5000);

            editor.Activate();
            AwaitCondition(() => GetForegroundWindow() == editor.Handle, "Could not return focus to the Editor");
            Assert(IsAbove(editor.Handle, galleryHandle), "the Editor can cover the Gallery after regaining focus");

            galleryButton.PerformClick();
            AwaitCondition(() => GetForegroundWindow() == galleryHandle, "The open Gallery did not return to the foreground");
            Assert(IsAbove(galleryHandle, editor.Handle) && ReferenceEquals(gallery, ZoomAppField(app, "_historyWindow")),
                "the Editor's Gallery button raises the existing Gallery");

            gallery!.WindowState = WindowState.Minimized;
            editor.Activate();
            galleryButton.PerformClick();
            AwaitCondition(() => gallery.WindowState != WindowState.Minimized && GetForegroundWindow() == galleryHandle,
                "The Editor's Gallery button did not restore the minimized Gallery");
            Assert(IsAbove(galleryHandle, editor.Handle), "the minimized Gallery restores in front of the Editor");
        }
        finally
        {
            (ZoomAppField(app, "_historyWindow") as HistoryWindow)?.Close();
            instance.SetValue(null, null);
            SetZoomAppField(app, "_historyService", null);
            SetZoomAppField(app, "_imageSearchIndexService", null);
            Pump();
        }

        static bool IsAbove(IntPtr first, IntPtr second)
        {
            for (IntPtr window = ReadGalleryWindow(first, 2 /* GW_HWNDNEXT */);
                window != IntPtr.Zero; window = ReadGalleryWindow(window, 2))
            {
                if (window == second) return true;
            }
            return false;
        }
    }
}
