using System.Runtime.InteropServices;
using System.Reflection;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Interop;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckWidgetSessionRestore(SettingsService settings)
    {
        var app = (CyberSnap.App)Application.Current;
        settings.Settings.WidgetHideDuringCapture = true;
        settings.Settings.DisableAnimations = true;
        var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
        SetZoomAppField(app, "_widgetWindow", widget);
        try
        {
            widget.Show(); Pump();
            GetField<DispatcherTimer>(widget, "_activeMonitorTimer").Stop();

            SetZoomAppField(app, "_isCapturing", 1);
            Call(widget, "HideForExternalSession", app);
            Assert(!widget.IsVisible && widget.Opacity == 0,
                "capture hides the widget before the overlay opens");

            bool desktopFrameStarted = false;
            CallZoomApp(app, "RunAfterPreviewChromeHidden", new Action(() => desktopFrameStarted = true));
            Assert(!desktopFrameStarted,
                "the desktop frame waits for the hidden widget to leave composition");
            WaitForWidgetAnimation(() => desktopFrameStarted);

            // The previous capture ends and starts a deferred render-stage restore.
            // A new capture then claims the slot before that callback executes.
            SetZoomAppField(app, "_isCapturing", 0);
            Call(widget, "FinishSessionRestore");
            SetZoomAppField(app, "_isCapturing", 1);
            Call(widget, "HideForExternalSession", app);
            Pump();
            Assert(!widget.IsVisible && widget.Opacity == 0 && GetField<bool>(widget, "_awaitingSessionRestore"),
                "a queued restore cannot reveal the peek over a new capture");

            // Also cover a slot claim that races ahead of the widget's hide dispatch.
            SetZoomAppField(app, "_isCapturing", 0);
            Call(widget, "FinishSessionRestore");
            SetZoomAppField(app, "_isCapturing", 1);
            Pump();
            Assert(!widget.IsVisible && widget.Opacity == 0 && GetField<bool>(widget, "_awaitingSessionRestore"),
                "render-stage restore rechecks whether capture became busy");

            SetZoomAppField(app, "_isCapturing", 0);
            Call(widget, "FinishSessionRestore");
            var hwnd = new WindowInteropHelper(widget).Handle;
            var inspectionRegion = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Assert(widget.IsVisible && widget.Opacity == 0 && GetWindowRgn(hwnd, inspectionRegion) == 1,
                    "the remapped widget stays clipped while its WPF surface settles");
            }
            finally { DeleteObject(inspectionRegion); }
            WaitForWidgetAnimation(() => widget.IsVisible && widget.Opacity == 1);
            Assert(widget.IsVisible && widget.Opacity == 1 && !GetField<bool>(widget, "_awaitingSessionRestore"),
                "the widget returns after the capture session ends");
            inspectionRegion = CreateRectRgn(0, 0, 0, 0);
            try { Assert(GetWindowRgn(hwnd, inspectionRegion) == 0, "the widget region is fully restored"); }
            finally { DeleteObject(inspectionRegion); }

            var dxgi = typeof(CyberSnap.App).Assembly.GetType("CyberSnap.Capture.DxgiScreenCapture")!;
            var currentFrame = dxgi.GetMethod("IsFrameCurrent", BindingFlags.Static | BindingFlags.NonPublic)!;
            Assert(!(bool)currentFrame.Invoke(null, new object[] { 99L, 100L })!
                && !(bool)currentFrame.Invoke(null, new object[] { 0L, 100L })!
                && (bool)currentFrame.Invoke(null, new object[] { 101L, 100L })!,
                "capture rejects old and pointer-only DXGI frames after the composition barrier");

            if (Environment.GetEnvironmentVariable("CYBERSNAP_CHECK_DESKTOP_PIXELS") == "1")
                CheckWidgetDesktopPixels(app, widget);
        }
        finally
        {
            SetZoomAppField(app, "_isCapturing", 0);
            SetZoomAppField(app, "_widgetWindow", null);
            widget.Close(); Pump();
        }
    }

    private static void CheckWidgetFullscreen(SettingsService settings)
    {
        var app = (CyberSnap.App)Application.Current;
        var screens = WidgetScreens();
        var screen = screens.FirstOrDefault(item => !item.Primary) ?? screens[0];
        var otherScreen = screens.FirstOrDefault(item => item.DeviceName != screen.DeviceName);
        var original = WidgetPreferences.FromSettings(settings.Settings);
        settings.Settings.WidgetMonitorIndex = Array.IndexOf(screens, screen);
        settings.Settings.WidgetAlwaysOnTop = true;
        using var normal = new Forms.Form
        {
            StartPosition = Forms.FormStartPosition.Manual,
            Bounds = new Drawing.Rectangle(screen.WorkingArea.Location, new Drawing.Size(400, 300)),
            Text = "Widget maximized window fixture"
        };
        using var fullscreen = new Forms.Form
        {
            AutoScaleMode = Forms.AutoScaleMode.None,
            FormBorderStyle = Forms.FormBorderStyle.None,
            StartPosition = Forms.FormStartPosition.Manual,
            Bounds = screen.Bounds,
            Text = "Widget fullscreen fixture"
        };
        var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
        var region = CreateRectRgn(0, 0, 0, 0);
        SetZoomAppField(app, "_widgetWindow", widget);
        try
        {
            normal.Show();
            normal.WindowState = Forms.FormWindowState.Maximized;
            widget.Show();
            var hwnd = new WindowInteropHelper(widget).Handle;
            int RegionKind() => GetWindowRgn(hwnd, region);
            Call(widget, "FollowActiveMonitorTick", null!, EventArgs.Empty);
            Assert(RegionKind() == 0, "a maximized window leaves the widget visible");

            normal.Hide();
            fullscreen.Show();
            var bounds = screen.Bounds;
            Assert(SetShadowTestWindowPos(fullscreen.Handle, IntPtr.Zero, bounds.X, bounds.Y,
                bounds.Width, bounds.Height, 0x0014), "the fixture covers the physical monitor after DPI changes");
            AwaitCondition(() => RegionKind() == 1, "the widget timer did not clip the HWND on the fullscreen monitor", 3000);

            if (otherScreen is not null)
            {
                normal.WindowState = Forms.FormWindowState.Normal;
                normal.Location = otherScreen.WorkingArea.Location;
                normal.Show();
                normal.Activate();
                AwaitCondition(() => GetForegroundWindow() != fullscreen.Handle, "the fullscreen fixture remained in the foreground", 3000);
                Call(widget, "FollowActiveMonitorTick", null!, EventArgs.Empty);
                Assert(RegionKind() == 1, "a visible fullscreen window keeps the widget hidden without foreground focus");

                Call(widget, "PositionWindowOnScreen", otherScreen);
                Assert(Forms.Screen.FromHandle(hwnd).DeviceName == otherScreen.DeviceName && RegionKind() == 0,
                    "the widget stays available on the monitor without a fullscreen window");
                Call(widget, "PositionWindowOnScreen", screen);
                Assert(RegionKind() == 1, "moving back to the fullscreen monitor clips the widget before it appears");
            }

            SetZoomAppField(app, "_isCapturing", 1);
            Call(widget, "HideForExternalSession", app);
            SetZoomAppField(app, "_isCapturing", 0);
            Call(widget, "FinishSessionRestore");
            AwaitCondition(() => widget.IsVisible && widget.Opacity == 1 && !GetField<bool>(widget, "_awaitingSessionRestore"),
                "the capture-session restore did not finish while fullscreen was active", 3000);
            Assert(RegionKind() == 1, "ending a capture leaves the widget clipped while fullscreen is active");

            fullscreen.Bounds = new Drawing.Rectangle(bounds.Location, new Drawing.Size(480, 300));
            AwaitCondition(() => RegionKind() == 0, "the widget did not return when the app left fullscreen", 3000);
            Console.WriteLine($"PASS: fullscreen widget visibility on {screens.Length} connected display(s), including capture restore.");
        }
        finally
        {
            SetZoomAppField(app, "_isCapturing", 0);
            SetZoomAppField(app, "_widgetWindow", null);
            widget.Close();
            DeleteObject(region);
            original.ApplyTo(settings.Settings);
            Pump();
        }
    }

    private static void CheckWidgetDesktopPixels(CyberSnap.App app, CaptureWidgetWindow widget)
    {
        var dwm = typeof(CyberSnap.App).Assembly.GetType("CyberSnap.Native.Dwm")!
            .GetMethod("DwmFlush", BindingFlags.Public | BindingFlags.Static)!;
        foreach (var screen in Forms.Screen.AllScreens)
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Call(widget, "PositionWindowOnScreen", screen);
            Pump();
            var grip = Element<Border>(widget, "PeekGrip");
            var point = grip.PointToScreen(new System.Windows.Point(grip.ActualWidth / 2, grip.ActualHeight / 2));
            var sample = new Drawing.Rectangle((int)point.X, (int)point.Y, 1, 1);
            using var backdrop = new Forms.Form
            {
                FormBorderStyle = Forms.FormBorderStyle.None, ShowInTaskbar = false,
                StartPosition = Forms.FormStartPosition.Manual, TopMost = true,
                Bounds = new Drawing.Rectangle(sample.X - 10, sample.Y - 10, 21, 21),
                BackColor = Drawing.Color.Magenta
            };
            backdrop.Show();
            widget.AssertWidgetTopmost();
            Pump();
            Marshal.ThrowExceptionForHR((int)dwm.Invoke(null, null)!);
            using (var visible = CyberSnap.Capture.ScreenCapture.CaptureRegion(sample,
                frameNotBefore: Stopwatch.GetTimestamp()))
                Assert(visible.GetPixel(0, 0).ToArgb() != Drawing.Color.Magenta.ToArgb(),
                    "desktop pixel oracle sees the visible peek", false);

            SetZoomAppField(app, "_isCapturing", 1);
            Call(widget, "HideForExternalSession", app);
            bool completed = false;
            Exception? failure = null;
            bool fixedDelayReference = attempt % 2 == 1;
            var elapsed = Stopwatch.StartNew();
            Action capturePixel = () =>
            {
                try
                {
                    long barrierMs = elapsed.ElapsedMilliseconds;
                    var barrier = fixedDelayReference ? 0 : (long)ZoomAppField(app, "_captureFrameNotBefore")!;
                    using var hidden = CyberSnap.Capture.ScreenCapture.CaptureRegion(sample, frameNotBefore: barrier);
                    Assert(hidden.GetPixel(0, 0).ToArgb() == Drawing.Color.Magenta.ToArgb(),
                        "the captured desktop contains the backdrop instead of the peek", false);
                    Console.WriteLine($"PASS: clean widget capture on {screen.DeviceName}, fixed-delay reference={fixedDelayReference}, barrier {barrierMs} ms, capture {elapsed.ElapsedMilliseconds - barrierMs} ms.");
                }
                catch (Exception ex) { failure = ex; }
                finally { completed = true; }
            };
            if (fixedDelayReference)
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                timer.Tick += (_, _) => { timer.Stop(); capturePixel(); };
                timer.Start();
            }
            else
                CallZoomApp(app, "RunAfterPreviewChromeHidden", capturePixel);
            WaitForWidgetAnimation(() => completed);
            if (failure is not null) throw failure;
            SetZoomAppField(app, "_isCapturing", 0);
            Call(widget, "FinishSessionRestore");
            WaitForWidgetAnimation(() => !GetField<bool>(widget, "_awaitingSessionRestore"));
        }
    }

    private static void CheckWidgetCollapseAppearance(SettingsService settings, string? renders)
    {
        var motion = typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.Motion")!
            .GetProperty("Disabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool originalDisabled = (bool)motion.GetValue(null)!;
        double originalScale = UiScale.Current;
        var originalPreferences = CyberSnap.Helpers.WidgetPreferences.FromSettings(settings.Settings);
        var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
        try
        {
            widget.Show();
            GetField<DispatcherTimer>(widget, "_activeMonitorTimer").Stop();
            SetField(widget, "_contextMenuOpen", true);
            foreach (var screen in WidgetScreens())
            foreach (double scale in new[] { 1.0, 1.4 })
            foreach (var edge in Enum.GetValues<CaptureDockSide>())
            {
                UiScale.Set(scale);
                settings.Settings.WidgetDockEdge = edge;
                widget.RefreshLayout();
                Call(widget, "PositionWindowOnScreen", screen);
                var peek = Element<Border>(widget, "PeekGrip");
                var panel = Element<Border>(widget, "MainPanelBorder");
                var controls = Element<ScrollViewer>(widget, "ControlsViewport");
                var background = panel.Background;
                string context = $"{screen.DeviceName}, {edge}, UI={scale}";
                motion.SetValue(null, false);
                SetField(widget, "_isExpanded", true);
                widget.PositionWindow(); Pump();
                double expandedHeight = panel.ActualHeight;
                Call(widget, "CollapseWidget");
                Assert(peek.Visibility == Visibility.Collapsed && controls.Visibility == Visibility.Visible,
                    "the cyan peek stays hidden while the expanded panel begins collapsing: " + context, false);
                Assert(panel.Background == background, "the collapsing panel retains its normal background", false);
                if (renders is not null && scale == 1 && screen.Primary)
                    Render(widget, renders, "widget-collapse-start-" + edge + ".png");
                WaitForWidgetAnimation(() => peek.Visibility == Visibility.Visible);
                Assert(controls.Visibility == Visibility.Collapsed && (edge is CaptureDockSide.Top or CaptureDockSide.Bottom
                    ? Math.Abs(panel.ActualHeight - 9) < 1 : Math.Abs(panel.ActualWidth - 9) < 1),
                    "the peek appears only after the panel reaches its final collapsed bounds: " + context, false);

                // An expansion that interrupts collapse must invalidate the old completion callback.
                motion.SetValue(null, true);
                Call(widget, "ExpandWidget"); Pump();
                motion.SetValue(null, false);
                Call(widget, "CollapseWidget");
                Call(widget, "ExpandWidget");
                WaitForWidgetAnimation(() => Math.Abs(panel.ActualWidth - 196) < 1 && Math.Abs(panel.ActualHeight - expandedHeight) < 1);
                WaitForWidgetAnimation(() => !widget.HasAnimatedProperties);
                Assert(peek.Visibility == Visibility.Collapsed && controls.Visibility == Visibility.Visible,
                    "interrupting collapse keeps the cyan peek out of the expanded panel: " + context, false);

                motion.SetValue(null, true);
                Call(widget, "CollapseWidget"); Pump();
                Assert(peek.Visibility == Visibility.Visible && controls.Visibility == Visibility.Collapsed,
                    "disabled animations switch directly to the normal peek: " + context, false);
            }
        }
        finally
        {
            widget.Close();
            motion.SetValue(null, originalDisabled);
            UiScale.Set(originalScale);
            originalPreferences.ApplyTo(settings.Settings);
        }
    }

    private static void WaitForWidgetAnimation(Func<bool> complete)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            Pump();
            if (complete()) return;
            Thread.Sleep(10);
        } while (elapsed.ElapsedMilliseconds < 3000);
        Assert(false, "the widget animation finishes within its bounded timeout");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WidgetTransitionRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Drawing.Rectangle Rectangle => Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadWidgetTransitionRect(IntPtr hwnd, out WidgetTransitionRect rect);

    private static void CheckWidgetMonitorMovePositions(CaptureWidgetWindow widget, SettingsService settings, Forms.Screen[] screens)
    {
        if (screens.Length < 2)
        {
            Console.WriteLine("SKIP: intermediate monitor-move positions require two connected displays.");
            return;
        }
        var hwnd = new WindowInteropHelper(widget).Handle;
        var source = HwndSource.FromHwnd(hwnd)!;
        var moves = new List<Drawing.Rectangle>();
        bool recording = false;
        source.AddHook(TrackMove);
        long time = 100000;
        double originalScale = UiScale.Current;
        var animationSetting = typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.Motion")!
            .GetProperty("Disabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool originalAnimations = (bool)animationSetting.GetValue(null)!;
        var originalEdge = settings.Settings.WidgetDockEdge;
        double originalOffset = settings.Settings.WidgetDockPositionOffset;
        try
        {
            foreach (double uiScale in new[] { 1.0, 1.4 })
            foreach (bool disableAnimations in new[] { false, true })
            foreach (var edge in Enum.GetValues<CaptureDockSide>())
            foreach (double offset in new[] { 0.0, 0.5, 1.0 })
            {
                UiScale.Set(uiScale);
                animationSetting.SetValue(null, disableAnimations);
                settings.Settings.WidgetDockEdge = edge;
                settings.Settings.WidgetDockPositionOffset = offset;
                widget.RefreshLayout();
                widget.CollapseImmediately();
                Call(widget, "PositionWindowOnScreen", screens[0]);
                Pump();
                foreach (var target in new[] { screens[1], screens[0] })
                {
                    SetField(widget, "_mouseInWindow", false);
                    moves.Clear();
                    recording = true;
                    // Use the production foreground-follow path, including its stability delay.
                    Call(widget, "ObserveForegroundMonitor", new IntPtr(0x12345), target, time);
                    Call(widget, "ObserveForegroundMonitor", new IntPtr(0x12345), target, time + 400);
                    Pump();
                    recording = false;
                    time += 1000;
                    Assert(ReadWidgetTransitionRect(hwnd, out var final), "the moved widget has native bounds", false);
                    string context = $"{target.DeviceName}, {edge}, offset={offset}, UI={uiScale}, animations={!disableAnimations}";
                    Assert(Forms.Screen.FromHandle(hwnd).DeviceName == target.DeviceName && moves.Count > 0,
                        "foreground following actually moves the widget: " + context, false);
                    Assert(moves.All(rect => Math.Abs(rect.Left - final.Left) <= 2 && Math.Abs(rect.Top - final.Top) <= 2
                        && Math.Abs(rect.Right - final.Right) <= 2 && Math.Abs(rect.Bottom - final.Bottom) <= 2),
                        "every native move goes directly to the final docked position: " + context
                        + $", final={final.Rectangle}, moves={string.Join("; ", moves)}", false);
                }
            }
            Console.WriteLine("PASS: foreground-follow transitions reach the final position without a corner detour on both monitors, all dock edges, offsets, UI scales and animation settings.");
        }
        finally
        {
            recording = false;
            source.RemoveHook(TrackMove);
            UiScale.Set(originalScale);
            animationSetting.SetValue(null, originalAnimations);
            settings.Settings.WidgetDockEdge = originalEdge;
            settings.Settings.WidgetDockPositionOffset = originalOffset;
        }

        IntPtr TrackMove(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (recording && message == 0x0047 /* WM_WINDOWPOSCHANGED */ && ReadWidgetTransitionRect(window, out var rect))
                moves.Add(rect.Rectangle);
            return IntPtr.Zero;
        }
    }
}
