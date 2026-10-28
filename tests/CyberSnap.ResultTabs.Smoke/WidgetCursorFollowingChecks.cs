using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckWidgetCursorFollowing(SettingsService settings, string? renders)
    {
        var original = WidgetPreferences.FromSettings(settings.Settings);
        var screens = WidgetScreens();
        settings.Settings.WidgetMonitorIndex = WidgetPreferences.FollowCursor;
        settings.Settings.WidgetDockEdge = CaptureDockSide.Top;
        settings.Settings.WidgetDockPositionOffset = 0.5;
        settings.Save();
        var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
        try
        {
            Assert(((Forms.Screen)Call(widget, "ResolvePlacementScreen", (object)screens)!).DeviceName == Forms.Screen.FromPoint(Forms.Control.MousePosition).DeviceName,
                "cursor-follow mode initially selects the cursor's monitor");
            widget.Show(); Pump(); Pump();
            GetField<DispatcherTimer>(widget, "_activeMonitorTimer").Stop();
            widget.CollapseImmediately();
            foreach (var target in screens.Reverse())
            {
                var cursor = new System.Drawing.Point(target.Bounds.X + target.Bounds.Width / 2, target.Bounds.Y + target.Bounds.Height / 2);
                IntPtr foreground = GetForegroundWindow();
                Call(widget, "FollowCursorOnMonitor", cursor); Pump();
                Assert(Here() == target.DeviceName && GetForegroundWindow() == foreground,
                    "cursor movement follows the requested physical monitor without a click or focus change");
                Assert(settings.Settings.WidgetMonitorIndex == WidgetPreferences.FollowCursor,
                    "following the cursor retains the automatic preference");
                foreach (string flag in new[] { "_mouseInWindow", "_isExpanded", "_contextMenuOpen", "_isDragArmed", "_isDragging", "_suppressHoverExpand", "_awaitingSessionRestore", "_sessionRestoreInProgress" })
                {
                    var elsewhere = screens.FirstOrDefault(s => s.DeviceName != Here()) ?? target;
                    string before = Here();
                    SetField(widget, flag, true);
                    try { Call(widget, "FollowCursorOnMonitor", new System.Drawing.Point(elsewhere.Bounds.X + 20, elsewhere.Bounds.Y + 20)); Pump(); }
                    finally { SetField(widget, flag, false); }
                    Assert(Here() == before, "cursor following pauses while the widget is in use: " + flag);
                }
            }
            var realCursor = Forms.Control.MousePosition;
            string cursorMonitor = Forms.Screen.FromPoint(realCursor).DeviceName;
            var other = screens.FirstOrDefault(s => s.DeviceName != cursorMonitor) ?? screens[0];
            Call(widget, "PositionWindowOnScreen", other); Pump();
            Call(widget, "FollowActiveMonitorTick", null!, EventArgs.Empty); Pump();
            Assert(Here() == cursorMonitor && Field(widget, "_monitorPointerHook") is IntPtr hook && hook == IntPtr.Zero,
                "the existing timer follows the real cursor without installing the click observer");
            settings.Settings.WidgetMonitorIndex = 0;
            Call(widget, "PositionWindowOnScreen", screens[0]); Pump();
            Call(widget, "FollowCursorOnMonitor", new System.Drawing.Point(screens[^1].Bounds.X + 20, screens[^1].Bounds.Y + 20)); Pump();
            Assert(Here() == screens[0].DeviceName, "a fixed monitor takes precedence over cursor following");
            settings.Settings.WidgetMonitorIndex = WidgetPreferences.FollowActiveMonitor;
            Call(widget, "FollowCursorOnMonitor", new System.Drawing.Point(screens[^1].Bounds.X + 20, screens[^1].Bounds.Y + 20)); Pump();
            Assert(Here() == screens[0].DeviceName, "following the active monitor does not start following cursor motion");
            Assert(WidgetStatic<int>("ResolveMonitorIndexAfterDisplayChange", -2, null!, new[] { "A" }) == -2
                && WidgetStatic<int>("ResolveMonitorIndexAfterDisplayChange", -2, null!, new[] { "B", "A" }) == -2,
                "cursor following survives display disconnection and reordering");
            widget.Hide();
            settings.Settings.WidgetMonitorIndex = WidgetPreferences.FollowCursor;
            Call(widget, "FollowCursorOnMonitor", new System.Drawing.Point(screens[^1].Bounds.X + 20, screens[^1].Bounds.Y + 20));
            Assert(Here() == screens[0].DeviceName, "a hidden widget does not move to follow the cursor");

            widget.Show(); Pump();
            GetField<DispatcherTimer>(widget, "_activeMonitorTimer").Stop();
            widget.IsHitTestVisible = true;
            Call(widget, "ExpandWidget");
            AwaitCondition(() => !widget.HasAnimatedProperties, "Widget expansion did not settle for hit testing");
            Pump();
            var button = Element<Button>(widget, "CaptureButton");
            button.ApplyTemplate();
            var tile = (Border)button.Template.FindName("CaptureTile", button);
            var ring = (FrameworkElement)button.Template.FindName("OuterRing", button);
            var ringBounds = ring.TransformToAncestor(button).TransformBounds(new Rect(ring.RenderSize));
            Assert(button.ActualWidth >= 160 && ringBounds.Top >= 24 && button.ActualHeight - ringBounds.Bottom >= 24,
                "the capture tile leaves generous clickable space above and below the capture mark");
            foreach (double x in new[] { 6.0, button.ActualWidth - 6 })
            {
                var hit = widget.InputHitTest(button.TranslatePoint(new Point(x, button.ActualHeight / 2), widget)) as DependencyObject;
                while (hit is not null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
                Assert(ReferenceEquals(hit, button), "the widened capture area hits the capture button outside the ring");
            }
            foreach (double y in new[] { 6.0, button.ActualHeight - 6 })
            {
                var hit = widget.InputHitTest(button.TranslatePoint(new Point(button.ActualWidth / 2, y), widget)) as DependencyObject;
                while (hit is not null && hit is not Button) hit = VisualTreeHelper.GetParent(hit);
                Assert(ReferenceEquals(hit, button), "the added space above and below the icon activates the capture button");
            }
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light, AppThemeMode.Grayscale })
            {
                Theme.SetMode(theme); Call(widget, "ApplyTheme"); Call(widget, "LoadIcons");
                button.Focus(); Pump();
                MovePickerPointer(button, new Point(6, button.ActualHeight / 2));
                widget.UpdateLayout();
                Assert(button.IsMouseOver && tile.Background is SolidColorBrush brush && brush.Color == Theme.BgCard,
                    "the whole capture tile uses the tray-style hover surface in " + theme);
                Render(widget, renders, "widget-capture-tile-" + theme + ".png");
            }
        }
        finally
        {
            widget.Close(); Pump();
            original.ApplyTo(settings.Settings); settings.Save();
            Theme.SetMode(AppThemeMode.Dark);
        }
        string Here() => Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).DeviceName;
    }
}
