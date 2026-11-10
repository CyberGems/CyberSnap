using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Drawing = System.Drawing;

internal static partial class Program
{
    private static void CheckWidgetCaptureModes(SettingsService settings, string temp)
    {
        var app = (CyberSnap.App)Application.Current;
        var preferences = settings.Settings;
        preferences.AfterCapture = AfterCaptureAction.None;
        preferences.CelebrationsEnabled = false;
        preferences.SaveToFile = true;
        preferences.SaveHistory = false;
        preferences.SaveInMonthlyFolders = false;
        preferences.SaveDirectory = Path.Combine(temp, "widget-captures");
        AutoCopyPreferences.SetKindEnabled(preferences, AutoCopyKind.Image, false);

        using var tray = new TrayIcon(preferences);
        var target = new Window { Title = "Widget capture target", Width = 320, Height = 208, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var other = new Window { Title = "Widget capture focus change", Width = 440, Height = 275 };
        var widget = new CaptureWidgetWindow(settings);
        SetZoomAppField(app, "_trayIcon", tray);
        SetZoomAppField(app, "_widgetWindow", widget);
        try
        {
            other.Show();
            target.Show();
            widget.Show();
            Call(widget, "ExpandWidget");
            AwaitCondition(() => !widget.HasAnimatedProperties, "the widget did not expand", 3000);

            foreach (var (language, labels) in new[] { ("en", new[] { "Fullscreen", "Window", "Repeat" }), ("es", new[] { "Pantalla", "Ventana", "Repetir" }) })
            {
                preferences.InterfaceLanguage = language;
                widget.RefreshLocalization();
                widget.UpdateLayout();
                string[] modes = ["Fullscreen", "Window", "Repeat"];
                for (int i = 0; i < modes.Length; i++)
                {
                    var button = Element<Button>(widget, modes[i] + "ModeButton");
                    var label = Element<TextBlock>(widget, modes[i] + "ModeLabel");
                    var bounds = label.TransformToAncestor(button).TransformBounds(new Rect(label.RenderSize));
                    Assert(button.IsVisible && label.Text == labels[i] && AutomationProperties.GetName(button) == labels[i]
                        && bounds.Right <= button.ActualWidth, "the localized widget label fits and names its button: " + labels[i]);
                }
            }

            var targetHwnd = new WindowInteropHelper(target).Handle;
            var otherHwnd = new WindowInteropHelper(other).Handle;
            target.Activate();
            AwaitCondition(() => GetForegroundWindow() == targetHwnd, "the capture target did not receive focus", 5000);
            var getBounds = typeof(WindowDetector).GetMethod("GetTrayCaptureBounds", BindingFlags.Static | BindingFlags.NonPublic)!;
            var targetBounds = (Drawing.Rectangle)getBounds.Invoke(null, [targetHwnd])!;
            Assert(!targetBounds.IsEmpty, "the active fixture has valid capture bounds");
            Capture("Window", () =>
            {
                other.Activate();
                Assert(GetForegroundWindow() == otherHwnd, "focus changes after the widget snapshots the target");
            });
            using (var image = new Drawing.Bitmap(Directory.GetFiles(preferences.SaveDirectory, "*.png").Single()))
                Assert(image.Size == targetBounds.Size, "Window captures the originally active app after focus changes");

            var repeat = new Drawing.Rectangle(targetBounds.Left + 40, targetBounds.Top + 45, 110, 85);
            LastCaptureArea.PersistScreenRect(preferences, settings, repeat);
            Capture("Repeat");
            Assert(Directory.GetFiles(preferences.SaveDirectory, "*.png").Any(path =>
            {
                using var image = new Drawing.Bitmap(path);
                return image.Size == repeat.Size;
            }), "Repeat uses the saved capture rectangle");

            preferences.SaveToFile = false;
            Capture("Fullscreen");
            Assert(Directory.GetFiles(preferences.SaveDirectory, "*.png").Length == 2,
                "Fullscreen completes through the regular capture pipeline with disk delivery disabled");
        }
        finally
        {
            widget.Close();
            other.Close();
            target.Close();
            SetZoomAppField(app, "_widgetWindow", null);
            SetZoomAppField(app, "_trayIcon", null);
        }

        void Capture(string mode, Action? afterClick = null)
        {
            int count = preferences.ScreenshotCount;
            Call(widget, "ExpandWidget");
            Element<Button>(widget, mode + "ModeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!widget.IsVisible, mode + " hides the widget before capture starts");
            afterClick?.Invoke();
            AwaitCondition(() => !app.IsSessionBusy() && widget.IsVisible && widget.Opacity == 1,
                mode + " did not complete and restore the widget", 10000);
            var errors = app.Windows.OfType<ToastWindow>().Select(toast => Element<TextBlock>(toast, "BodyText").Text);
            Assert(preferences.ScreenshotCount == count + 1, $"{mode} completed the screenshot. {string.Join(" ", errors)}".TrimEnd());
        }
    }
}
