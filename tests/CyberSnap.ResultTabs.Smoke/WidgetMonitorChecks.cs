using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    private static T WidgetStatic<T>(string name, params object[] args)
        => (T)typeof(CaptureWidgetWindow).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, args)!;

    private static void CheckWidgetMonitors(SettingsService settings)
    {
        var helper = typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.PopupWindowHelper")!;
        var screens = (Forms.Screen[])helper.GetMethod("GetSortedScreens")!.Invoke(null, null)!;
        var original = CyberSnap.Helpers.WidgetPreferences.FromSettings(settings.Settings);
        double originalScale = UiScale.Current;
        bool originalAnimations = settings.Settings.DisableAnimations;
        settings.Settings.DisableAnimations = true;
        settings.Settings.WidgetMonitorIndex = -1;
        settings.Save();
        UiScale.Set(1);
        var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
        try
        {
            widget.Show();
            GetField<System.Windows.Threading.DispatcherTimer>(widget, "_activeMonitorTimer").Stop();
            Pump();
            CheckWidgetMonitorMovePositions(widget, settings, screens);
            foreach (var screen in screens)
            {
                var work = WidgetStatic<System.Drawing.Rectangle>("GetPhysicalWorkingArea", screen);
                var center = new System.Drawing.Point(work.X + work.Width / 2, work.Y + work.Height / 2);
                var dpi = ((double X, double Y))helper.GetMethod("GetScaleForPoint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [center])!;
                Console.WriteLine($"Monitor {screen.DeviceName}: work={work}, DPI={96 * dpi.X:0}/{96 * dpi.Y:0} ({dpi.X:P0}/{dpi.Y:P0}).");
                foreach (double uiScale in new[] { 1.0, 1.1, 1.4 })
                foreach (var edge in Enum.GetValues<CaptureDockSide>())
                foreach (double offset in new[] { 0.0, 0.5, 1.0 })
                foreach (bool expanded in new[] { false, true })
                {
                    UiScale.Set(uiScale);
                    widget.RefreshLayout();
                    settings.Settings.WidgetDockEdge = edge;
                    settings.Settings.WidgetDockPositionOffset = offset;
                    SetField(widget, "_isExpanded", expanded);
                    Call(widget, "PositionWindowAtCursor", center);
                    Pump();
                    AssertPlacement(screen, work, edge, expanded, uiScale, dpi.X, dpi.Y);
                    Assert(settings.Settings.WidgetMonitorIndex == -1, "following a monitor does not pin the preference to it", false);
                }

                // Exercise the production drag update with sampled cursor positions on
                // each real monitor, without moving the user's mouse or changing its DPI.
                UiScale.Set(1);
                widget.RefreshLayout();
                settings.Settings.WidgetDockEdge = CaptureDockSide.Top;
                SetField(widget, "_isExpanded", true);
                SetField(widget, "_isDragging", true);
                var first = new System.Drawing.Point(work.X + work.Width / 3, work.Y + 4);
                Call(widget, "UpdateDragPosition", first);
                double before = settings.Settings.WidgetDockPositionOffset;
                int delta = (int)Math.Round(40 * dpi.X);
                Call(widget, "UpdateDragPosition", new System.Drawing.Point(first.X + delta, first.Y));
                Pump();
                double expected = Math.Clamp(before + 40 / (work.Width / dpi.X - 196), 0, 1);
                Assert(Math.Abs(settings.Settings.WidgetDockPositionOffset - expected) < 0.002,
                    "dragging on each monitor advances by the DIP distance at that monitor's DPI", false);
                AssertPlacement(screen, work, CaptureDockSide.Top, true, 1, dpi.X, dpi.Y);
                SetField(widget, "_isDragging", false);

                // Simulate the OS notification as well: when WPF adopts the new
                // render DPI, the temporary layout correction must disappear.
                var suggested = Marshal.AllocHGlobal(16);
                try
                {
                    Point start = widget.PointToScreen(new Point());
                    Point end = widget.PointToScreen(new Point(widget.ActualWidth, widget.ActualHeight));
                    Marshal.WriteInt32(suggested, 0, (int)Math.Round(start.X));
                    Marshal.WriteInt32(suggested, 4, (int)Math.Round(start.Y));
                    Marshal.WriteInt32(suggested, 8, (int)Math.Round(end.X));
                    Marshal.WriteInt32(suggested, 12, (int)Math.Round(end.Y));
                    int x = (int)Math.Round(96 * dpi.X), y = (int)Math.Round(96 * dpi.Y);
                    SendMessageW(new WindowInteropHelper(widget).Handle, 0x02E0, new IntPtr(x | (y << 16)), suggested);
                }
                finally { Marshal.FreeHGlobal(suggested); }
                Pump();
                Assert(Math.Abs(VisualTreeHelper.GetDpi(widget).DpiScaleX - dpi.X) < 0.001
                    && Math.Abs(Element<FrameworkElement>(widget, "RootGrid").LayoutTransform.Value.M11 - 1) < 0.001,
                    "the DPI notification removes the rendering correction without double scaling", false);
                AssertPlacement(screen, work, CaptureDockSide.Top, true, 1, dpi.X, dpi.Y);

                // Fixed monitor must take precedence over a cursor on the other monitor.
                settings.Settings.WidgetMonitorIndex = Array.IndexOf(screens, screen);
                var other = screens.Last();
                var elsewhere = new System.Drawing.Point(other.Bounds.Right - 10, other.Bounds.Bottom - 10);
                Call(widget, "PositionWindowAtCursor", elsewhere);
                Pump();
                Assert(Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).DeviceName == screen.DeviceName,
                    "fixed monitor placement ignores the sampled cursor monitor", false);
                VerifySessionRestore(screen);
                settings.Settings.WidgetMonitorIndex = -1;
            }
            // Expansion/capture restoration preserves the monitor. Foreground tracking
            // is tested separately, with controlled focus changes and a stable clock.
            var currentScreen = Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle);
            VerifySessionRestore(currentScreen);
            Call(widget, "OnDisplaySettingsChanged", null!, EventArgs.Empty);
            Pump();
            Assert(Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).DeviceName == currentScreen.DeviceName,
                "a live display-settings event repositions follow mode on an available monitor", false);

            // Same production resolver, supplied with a topology after one display vanished.
            foreach (var remaining in screens)
            {
                var target = WidgetStatic<Forms.Screen>("ResolveTargetScreen", new[] { remaining }, -1,
                    new System.Drawing.Point(screens[0].Bounds.X + 20, screens[0].Bounds.Y + 20));
                Assert(target.DeviceName == remaining.DeviceName, "follow mode falls back to an available display after disconnect", false);
            }
            Console.WriteLine("PASS: actual WPF placement on all available monitors, four edges, three UI scales, endpoint offsets, expanded/peek, drag updates and capture hide/restore.");

            void VerifySessionRestore(Forms.Screen expectedScreen)
            {
                Call(widget, "PrepareForSession");
                Assert(!widget.IsVisible && widget.Opacity == 0, "capture preparation hides the widget", false);
                SetField(widget, "_awaitingSessionRestore", true);
                Call(widget, "FinishSessionRestore");
                Pump();
                Assert(widget.IsVisible && widget.Opacity == 1 && !GetField<bool>(widget, "_isExpanded")
                    && !GetField<bool>(widget, "_awaitingSessionRestore"), "capture completion restores an opaque collapsed widget", false);
                Assert(Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).DeviceName == expectedScreen.DeviceName,
                    "capture completion restores the widget on its selected/followed monitor", false);
            }

            void AssertPlacement(Forms.Screen screen, System.Drawing.Rectangle work, CaptureDockSide edge,
                bool expanded, double uiScale, double scaleX, double scaleY)
            {
                widget.UpdateLayout();
                var panel = Element<Border>(widget, "MainPanelBorder");
                Point start = panel.PointToScreen(new Point());
                Point end = panel.PointToScreen(new Point(panel.ActualWidth, panel.ActualHeight));
                // Compare the visible panel with the final native client bounds.
                var visible = Rect.Intersect(new Rect(start, end), new Rect(widget.PointToScreen(new Point()),
                    widget.PointToScreen(new Point(widget.ActualWidth, widget.ActualHeight))));
                start = visible.TopLeft;
                end = visible.BottomRight;
                string context = $"{screen.DeviceName} {edge} expanded={expanded} ui={uiScale} dpi={scaleX}/{scaleY}, panel={start}..{end}, work={work}";
                context += $", WPF={VisualTreeHelper.GetDpi(widget).DpiScaleX}, nativeDpi={GetDpiForWindow(new WindowInteropHelper(widget).Handle)}, awareness={GetAwarenessFromDpiAwarenessContext(GetWindowDpiAwarenessContext(new WindowInteropHelper(widget).Handle))}";
                context += $", offset={settings.Settings.WidgetDockPositionOffset}, window={widget.Left},{widget.Top}/{widget.ActualWidth},{widget.ActualHeight}, margin={Element<FrameworkElement>(widget, "RootGrid").Margin}, transform={Element<FrameworkElement>(widget, "RootGrid").LayoutTransform.Value}";
                const double tolerance = 2;
                Assert(start.X >= work.Left - tolerance && start.Y >= work.Top - tolerance
                    && end.X <= work.Right + tolerance && end.Y <= work.Bottom + tolerance,
                    "the visible panel remains inside the physical working area: " + context, false);
                double edgeError = edge switch
                {
                    CaptureDockSide.Top => start.Y - work.Top,
                    CaptureDockSide.Bottom => end.Y - work.Bottom,
                    CaptureDockSide.Left => start.X - work.Left,
                    _ => end.X - work.Right
                };
                Assert(Math.Abs(edgeError) <= tolerance, "the widget touches its selected physical edge: " + context, false);
                double expectedWidth = (expanded || edge is CaptureDockSide.Top or CaptureDockSide.Bottom ? 196 : 9) * uiScale * scaleX;
                double expectedHeight = (expanded || edge is CaptureDockSide.Left or CaptureDockSide.Right ? 248 : 9) * uiScale * scaleY;
                Assert(Math.Abs(end.X - start.X - expectedWidth) <= tolerance
                    && Math.Abs(end.Y - start.Y - expectedHeight) <= tolerance,
                    "the native widget size uses its target monitor's DPI: " + context, false);
                var center = new System.Drawing.Point((int)Math.Round((start.X + end.X) / 2), (int)Math.Round((start.Y + end.Y) / 2));
                Assert((bool)Call(widget, "IsPointOverWidgetWithPadding", center)!,
                    "hover hit testing includes the visible panel at the target DPI: " + context, false);
                var outside = edge is CaptureDockSide.Top or CaptureDockSide.Bottom
                    ? new System.Drawing.Point((int)end.X + 30, center.Y)
                    : new System.Drawing.Point(center.X, (int)end.Y + 30);
                Assert(!(bool)Call(widget, "IsPointOverWidgetWithPadding", outside)!,
                    "hover hit testing excludes the area outside the visible panel and padding: " + context, false);
            }
        }
        finally
        {
            widget.Close();
            original.ApplyTo(settings.Settings);
            settings.Settings.DisableAnimations = originalAnimations;
            UiScale.Set(originalScale);
            settings.Save();
        }
        CheckSimulatedWidgetDpi();
    }

    private static void CheckSimulatedWidgetDpi()
    {
        // Assert logical invariants under transformations of the same gesture, rather
        // than comparing the method to a second copy of its implementation.
        double originalScale = UiScale.Current;
        try
        {
            UiScale.Set(1);
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
            foreach (var origin in new[] { new Point(0, 0), new Point(-2560, -600), new Point(1920, 240) })
            foreach (var edge in Enum.GetValues<CaptureDockSide>())
            {
                var work = new Rect(origin, new Size(1600 * scale, 1000 * scale));
                Point Pixel(double x, double y) => new(origin.X + x * scale, origin.Y + y * scale);
                var start = Pixel(800, 500);
                bool horizontal = edge is CaptureDockSide.Top or CaptureDockSide.Bottom;
                var cursor = horizontal ? Pixel(900, 500) : Pixel(800, 600);
                var actual = WidgetStatic<(CaptureDockSide Edge, double Offset)>("CalculateDragPosition",
                    work, new DpiScale(scale, scale), cursor, start, 0.25, edge, false);
                var baseline = WidgetStatic<(CaptureDockSide Edge, double Offset)>("CalculateDragPosition",
                    new Rect(0, 0, 1600, 1000), new DpiScale(1, 1),
                    horizontal ? new Point(900, 500) : new Point(800, 600), new Point(800, 500), 0.25, edge, false);
                Assert(actual.Edge == edge && Math.Abs(actual.Offset - baseline.Offset) < 0.000001 && actual.Offset > 0.25,
                    "equivalent DIP drags are invariant across monitor scales and desktop origins", false);
                var center = WidgetStatic<(CaptureDockSide Edge, double Offset)>("CalculateDragPosition",
                    work, new DpiScale(scale, scale), start, new Point(-9999, -9999), 0.91, edge, true);
                Assert(center.Edge == edge && Math.Abs(center.Offset - 0.5) < 0.000001,
                    "crossing onto another DPI rebases the grip under the cursor, independently of the old origin", false);
                var nearEdge = edge switch
                {
                    CaptureDockSide.Top => Pixel(800, 998),
                    CaptureDockSide.Bottom => Pixel(800, 2),
                    CaptureDockSide.Left => Pixel(1598, 500),
                    _ => Pixel(2, 500)
                };
                var snapped = WidgetStatic<(CaptureDockSide Edge, double Offset)>("CalculateDragPosition",
                    work, new DpiScale(scale, scale), nearEdge, start, 0.5, edge, true);
                Assert(snapped.Edge != edge && Math.Abs(snapped.Offset - 0.5) < 0.000001,
                    "edge snapping survives mixed DPI and negative desktop origins", false);
            }
            Assert(WidgetStatic<int>("ResolveMonitorIndexAfterDisplayChange", 1, "B", new[] { "B", "A" }) == 0,
                "monitor list reordering preserves the selected device", false);
            Assert(WidgetStatic<int>("ResolveMonitorIndexAfterDisplayChange", 0, "A", new[] { "B" }) == -1,
                "disconnecting a selected primary falls back even when its index is still in range", false);
            Assert(WidgetStatic<int>("ResolveMonitorIndexAfterDisplayChange", 1, "B", new[] { "A" }) == -1,
                "disconnecting a selected secondary falls back", false);
            Assert(WidgetStatic<int>("ResolveMonitorIndexAfterDisplayChange", -1, null!, new[] { "A" }) == -1,
                "automatic monitor mode remains automatic after disconnect", false);
            Assert(WidgetStatic<int>("ResolveMonitorIndexAfterDisplayChange", 9, null!, new[] { "A" }) == -1,
                "an unavailable persisted index falls back", false);
            Console.WriteLine("PASS: simulated mixed DPI (100/125/150/175/200%), negative origins, monitor crossings, edge snapping, disconnection and reordering. No physical display settings were changed.");
        }
        finally { UiScale.Set(originalScale); }
    }
}
