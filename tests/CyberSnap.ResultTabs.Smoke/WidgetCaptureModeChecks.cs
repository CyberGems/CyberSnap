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
using CyberSnap.UI.Editor;
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
        preferences.WidgetHideDuringCapture = false;
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

            var languages = new[]
            {
                ("en", new[] { "Fullscreen", "Window", "Repeat", "Gallery", "Editor" }),
                ("es", new[] { "Pantalla", "Ventana", "Repetir", "Galería", "Editor" })
            };
            foreach (var screen in WidgetScreens())
            foreach (var (language, labels) in languages)
            {
                Call(widget, "PositionWindowOnScreen", screen);
                preferences.InterfaceLanguage = language;
                widget.RefreshLocalization();
                widget.UpdateLayout();
                string[] actions = ["FullscreenMode", "WindowMode", "RepeatMode", "GalleryShortcut", "EditorShortcut"];
                for (int i = 0; i < actions.Length; i++)
                {
                    var button = Element<Button>(widget, actions[i] + "Button");
                    var label = Element<TextBlock>(widget, actions[i] + "Label");
                    var bounds = label.TransformToAncestor(button).TransformBounds(new Rect(label.RenderSize));
                    Assert(button.IsVisible && label.Text == labels[i] && AutomationProperties.GetName(button) == labels[i]
                        && bounds.Right <= button.ActualWidth, "the localized widget label fits and names its button: " + labels[i]);
                }
                var area = Element<TextBlock>(widget, "AreaModeLabel");
                var repeatLabel = Element<TextBlock>(widget, "RepeatModeLabel");
                var areaCenter = area.TranslatePoint(new Point(0, area.ActualHeight / 2), widget).Y;
                var repeatCenter = repeatLabel.TranslatePoint(new Point(0, repeatLabel.ActualHeight / 2), widget).Y;
                Assert(Math.Abs(areaCenter - repeatCenter) <= 1, "the Area and Repeat captions align on " + screen.DeviceName);
            }

            var viewport = Element<ScrollViewer>(widget, "ControlsViewport");
            Assert(viewport.ScrollableHeight <= 1, "the full panel fits at normal monitor heights");
            Element<Border>(widget, "MainPanelBorder").MaxHeight = 270;
            widget.UpdateLayout();
            viewport.ScrollToBottom();
            widget.UpdateLayout();
            var galleryButton = Element<Button>(widget, "GalleryShortcutButton");
            var galleryBounds = galleryButton.TransformToAncestor(viewport).TransformBounds(new Rect(galleryButton.RenderSize));
            Assert(viewport.VerticalOffset > 0 && galleryBounds.Top >= 0 && galleryBounds.Bottom <= viewport.ActualHeight,
                "a short work area lets users scroll to Gallery and Editor");
            widget.RefreshLayout();

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

            CheckWidgetGalleryEditorShortcuts(app, settings, widget);
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
            var button = Element<Button>(widget, mode + "ModeButton");
            IntPtr tooltipHwnd = IntPtr.Zero;
            bool verifyComposition = mode is "Window" or "Fullscreen";
            if (verifyComposition)
            {
                AwaitCondition(() => !widget.HasAnimatedProperties && button.IsVisible, "the widget did not expand for " + mode, 3000);
                var tip = (ToolTip)button.ToolTip;
                tip.IsOpen = true;
                AwaitCondition(() => tip.IsOpen && PresentationSource.FromVisual(tip) is HwndSource,
                    "the widget tooltip did not open for " + mode, 3000);
                tooltipHwnd = ((HwndSource)PresentationSource.FromVisual(tip)).Handle;
                CheckWidgetToolTipPlacement(widget, button, tooltipHwnd);
            }
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(!widget.IsVisible, mode + " hides the widget before capture starts");
            if (verifyComposition)
                Assert(!IsWidgetToolTipWindowVisible(tooltipHwnd), mode + " hides the tooltip HWND before capture starts");
            afterClick?.Invoke();
            AwaitCondition(() => !app.IsSessionBusy() && widget.IsVisible && widget.Opacity == 1,
                mode + " did not complete and restore the widget", 10000);
            var errors = app.Windows.OfType<ToastWindow>().Select(toast => Element<TextBlock>(toast, "BodyText").Text);
            Assert(preferences.ScreenshotCount == count + 1, $"{mode} completed the screenshot. {string.Join(" ", errors)}".TrimEnd());
            if (verifyComposition)
                Assert((long)ZoomAppField(app, "_captureFrameNotBefore")! > 0, mode + " waits for the widget's hidden frame before capture");
        }
    }

    private static void CheckWidgetGalleryEditorShortcuts(CyberSnap.App app, SettingsService settings, CaptureWidgetWindow widget)
    {
        using var history = new HistoryService();
        using var search = new ImageSearchIndexService();
        SetZoomAppField(app, "_historyService", history);
        SetZoomAppField(app, "_imageSearchIndexService", search);
        SetZoomAppField(app, "_historyMaintenanceScheduled", true);
        settings.Settings.AutoIndexImages = false;
        var editorInstance = typeof(EditorForm).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        try
        {
            Click("Gallery");
            AwaitCondition(() => ZoomAppField(app, "_historyWindow") is HistoryWindow { IsVisible: true },
                "the widget shortcut did not open Gallery", 10000);
            Assert(!GetField<bool>(widget, "_isExpanded"), "the widget collapses after opening Gallery");

            Click("Editor");
            AwaitCondition(() => editorInstance.GetValue(null) is EditorForm { Visible: true },
                "the widget shortcut did not open Editor", 10000);
            Assert(!GetField<bool>(widget, "_isExpanded"), "the widget collapses after opening Editor");
        }
        finally
        {
            (editorInstance.GetValue(null) as EditorForm)?.Close();
            (ZoomAppField(app, "_historyWindow") as HistoryWindow)?.Close();
            SetZoomAppField(app, "_historyService", null);
            SetZoomAppField(app, "_imageSearchIndexService", null);
        }

        void Click(string shortcut)
        {
            Call(widget, "ExpandWidget");
            Element<Button>(widget, shortcut + "ShortcutButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }
}
