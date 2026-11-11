using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckWidgetLazyMenus(SettingsService settings)
    {
        settings.Settings.DisableAnimations = true;
        foreach (string language in new[] { "en", "es" })
        {
            settings.Settings.InterfaceLanguage = language;
            settings.Settings.WidgetHoverDelayMs = 250;
            AutoCopyPreferences.SetKindEnabled(settings.Settings, AutoCopyKind.Image, true);
            settings.Save();
            var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
            try
            {
                widget.Show();
                GetField<DispatcherTimer>(widget, "_activeMonitorTimer").Stop();
                Call(widget, "ExpandWidget");
                Pump();
                var root = (Forms.ContextMenuStrip)Call(widget, "BuildMoreMenu")!;
                KeepVisibleForMeasurement(root);
                Call(widget, "ShowThemedMenu", root, true);
                var options = (Forms.ToolStripMenuItem)root.Items[0];
                var capture = (Forms.ToolStripMenuItem)root.Items[2];
                Assert(root.Items.OfType<Forms.ToolStripMenuItem>().Where(item => item.HasDropDownItems)
                    .All(item => !item.DropDownItems.OfType<Forms.ToolStripMenuItem>().Any()),
                    "opening More does not construct any submenu contents: " + language);

                settings.FlushPendingWrites();
                using var external = new SettingsService(GetField<string>(settings, "_settingsPath"));
                external.Settings.WidgetHoverDelayMs = 1000;
                AutoCopyPreferences.SetKindEnabled(external.Settings, AutoCopyKind.Image, false);
                external.Save();
                external.FlushPendingWrites();

                options.ShowDropDown();
                KeepVisibleForMeasurement(options.DropDown);
                var delay = options.DropDownItems.OfType<Forms.ToolStripMenuItem>()
                    .Single(item => item.Text == LocalizationService.Translate(language, "Activation delay"));
                Assert(!delay.DropDownItems.OfType<Forms.ToolStripMenuItem>().Any()
                    && !capture.DropDownItems.OfType<Forms.ToolStripMenuItem>().Any(),
                    "opening Widget leaves nested choices and other submenus unbuilt: " + language);
                delay.ShowDropDown();
                KeepVisibleForMeasurement(delay.DropDown);
                Assert(delay.DropDownItems.Count == 5 && delay.DropDownItems[^1].Image is not null,
                    "a nested submenu reads the preferences saved after More was opened: " + language);
                delay.HideDropDown();
                options.HideDropDown();
                capture.ShowDropDown();
                KeepVisibleForMeasurement(capture.DropDown);
                Assert(capture.DropDownItems[0].Image is null,
                    "a delivery submenu reads the latest saved preferences when opened: " + language);
                var copy = capture.DropDownItems[0];
                capture.HideDropDown();
                capture.ShowDropDown();
                Assert(capture.DropDownItems.Count == 8 && ReferenceEquals(copy, capture.DropDownItems[0]),
                    "reopening a submenu reuses its items without duplication: " + language);
                capture.HideDropDown();

                int visited = 0;
                Visit(root);
                Assert(visited >= 11, "every lazy submenu, including nested widget choices, opens: " + language);
                root.Close();
                Pump();
                Assert(root.IsDisposed && ((IDictionary)Field(widget, "_widgetMenuOptions")).Count == 0,
                    "closing More releases all populated preference bindings: " + language);

                void Visit(Forms.ToolStripDropDown menu)
                {
                    foreach (var item in menu.Items.OfType<Forms.ToolStripMenuItem>())
                    {
                        if (!item.HasDropDownItems) continue;
                        item.ShowDropDown();
                        KeepVisibleForMeasurement(item.DropDown);
                        Assert(item.DropDown.Visible && item.DropDownItems.Count > 1,
                            "submenu population completes before it becomes visible: " + item.Text, false);
                        Assert(Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).WorkingArea.Contains(item.DropDown.Bounds),
                            "a lazily opened submenu stays on the widget monitor: " + item.Text, false);
                        visited++;
                        Visit(item.DropDown);
                        item.HideDropDown();
                    }
                }
            }
            finally
            {
                widget.Close();
                Pump();
            }
        }
    }

    private static void MeasureWidgetMoreMenu(SettingsService settings)
    {
        var helper = typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.PopupWindowHelper")!;
        var getScale = helper.GetMethod("GetScaleForPoint", BindingFlags.Static | BindingFlags.NonPublic)!;
        var screens = WidgetScreens();
        int monitor = Array.FindIndex(screens, screen =>
        {
            var point = new System.Drawing.Point(screen.Bounds.Left + screen.Bounds.Width / 2,
                screen.Bounds.Top + screen.Bounds.Height / 2);
            var dpi = ((double X, double Y))getScale.Invoke(null, [point])!;
            return Math.Abs(dpi.X - 1.5) < 0.01;
        });
        Assert(monitor >= 0, "a real monitor at 150% is available for the timing comparison");
        settings.Settings.WidgetMonitorIndex = monitor;
        settings.Settings.WidgetDockEdge = CaptureDockSide.Right;
        settings.Settings.InterfaceLanguage = "es";
        settings.Settings.DisableAnimations = true;
        UiScale.Set(1);
        settings.Save();
        var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
        try
        {
            widget.Show();
            GetField<DispatcherTimer>(widget, "_activeMonitorTimer").Stop();
            for (int sample = 1; sample <= 7; sample++)
            {
                SetField(widget, "_mouseInWindow", true);
                Call(widget, "ExpandWidget");
                AwaitCondition(() => !widget.HasAnimatedProperties, "Widget expansion did not settle");
                Pump();
                Assert(GetDpiForWindow(new WindowInteropHelper(widget).Handle) == 144,
                    "the measured widget uses the actual 150% monitor DPI", false);
                long start = Stopwatch.GetTimestamp();
                Call(widget, "More_Click", Element<Button>(widget, "WidgetMoreButton"), new RoutedEventArgs());
                var menu = GetField<Forms.ContextMenuStrip>(widget, "_contextMenu");
                menu.Update();
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                Assert(menu.Visible, "the timed menu is visible after the click", false);
                Console.WriteLine(FormattableString.Invariant($"MORE sample={sample} dpi=150 elapsed_ms={elapsed:F2}"));
                menu.Close();
                Pump();
            }
        }
        finally
        {
            widget.Close();
            Pump();
        }
    }
}
