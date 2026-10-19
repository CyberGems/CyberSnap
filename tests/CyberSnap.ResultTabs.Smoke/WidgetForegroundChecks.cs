using System.Windows;
using System.Windows.Interop;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckWidgetForegroundTransitions(CaptureWidgetWindow widget, Window first, SettingsService settings, Forms.Screen[] screens)
    {
        var second = new Window { Width = 320, Height = 220, ShowInTaskbar = false, Title = "Widget second monitor verification" };
        var helper = typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.PopupWindowHelper")!;
        IntPtr firstHandle = new WindowInteropHelper(first).Handle;
        long time = 50000;
        try
        {
            Place(first, screens[0]);
            second.Show();
            Place(second, screens[1]);
            IntPtr secondHandle = new WindowInteropHelper(second).Handle;
            Activate(first, screens[0]);
            Call(widget, "FollowActiveMonitorTick", null!, EventArgs.Empty);
            Assert(GetField<IntPtr>(widget, "_monitorPointerHook") != IntPtr.Zero,
                "the native click observer installs for per-monitor desktop selection", false);
            foreach (var (window, screen) in new[] { (second, screens[1]), (first, screens[0]), (second, screens[1]), (first, screens[0]) })
            {
                Assert(window.Activate(), "the window-switch fixture gains foreground", false);
                Settle();
                Assert(Here() == screen.DeviceName, "repeated real foreground switches follow the selected window's monitor", false);
            }

            // An Alt+Tab-like sequence of foreground HWNDs: intermediate targets
            // are real windows, but no keyboard input is injected by this fixture.
            Assert(second.Activate(), "the intermediate focus target activates", false);
            Tick();
            Assert(first.Activate(), "the final focus target activates", false);
            Tick();
            Assert(Here() == screens[0].DeviceName, "brief intermediate foreground targets do not move the widget", false);
            Settle();
            Assert(Here() == screens[0].DeviceName, "only the stable final foreground target is followed", false);

            Place(first, screens[1]);
            Settle();
            Assert(Here() == screens[1].DeviceName, "moving the same active HWND across monitors is followed", false);
            Place(first, screens[0]);
            Settle();
            Assert(Here() == screens[0].DeviceName, "moving the active HWND back restores the original monitor", false);

            first.WindowState = WindowState.Minimized;
            Pump();
            Assert(first.WindowState == WindowState.Minimized, "the foreground fixture is really minimized", false);
            Assert(second.Activate(), "the replacement foreground window activates after minimization", false);
            Settle();
            Assert(Here() == screens[0].DeviceName,
                "automatic replacement focus on another display preserves the minimized window's desktop", false);
            Assert(GetField<IntPtr>(widget, "_minimizeFallbackWindow") == secondHandle,
                "the replacement foreground is held until an explicit selection", false);
            var center = second.PointToScreen(new Point(second.ActualWidth / 2, second.ActualHeight / 2));
            var pointType = typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.Native.User32+POINT")!;
            var nativePoint = Activator.CreateInstance(pointType, (int)center.X, (int)center.Y)!;
            Call(widget, "RecordMonitorPointerActivation", nativePoint);
            Settle();
            Assert(Here() == screens[1].DeviceName, "native hit testing of an explicit app selection releases the desktop hold", false);

            first.WindowState = WindowState.Normal;
            Assert(first.Activate(), "the minimized fixture restores and activates", false);
            Settle();
            Assert(Here() == screens[0].DeviceName, "restoring and focusing the original window follows its monitor", false);

            Call(widget, "ExpandWidget");
            AwaitCondition(() => !widget.HasAnimatedProperties, "Widget expansion did not settle for focus isolation");
            Assert(second.Activate(), "an external window activates during expanded widget use", false);
            Settle();
            Assert(Here() == screens[0].DeviceName, "real external focus cannot move an expanded widget", false);
            widget.CollapseImmediately();
            Tick();
            Assert(Here() == screens[0].DeviceName, "collapsing does not replay a stale foreground move", false);
            Settle();
            Assert(Here() == screens[1].DeviceName, "fresh stable focus is followed after interaction ends", false);

            // Desktop HWNDs span monitors. Exercise shell samples without
            // minimizing the user's applications or sending input to their shell.
            var desktop = new IntPtr(0x123456);
            Sample(firstHandle, screens[0], false, false);
            Sample(desktop, screens[1], true, true);
            Assert(Here() == screens[0].DeviceName, "keyboard/minimize desktop activation preserves the working monitor", false);
            Call(widget, "ObserveMonitorPointerActivation", desktop, screens[1], true);
            Sample(desktop, screens[0], true, false);
            Assert(Here() == screens[1].DeviceName, "clicking the second desktop selects it despite a shared shell HWND", false);
            Sample(desktop, screens[0], true, false);
            Assert(Here() == screens[1].DeviceName, "a desktop stays selected when the cursor or shell bounds refer elsewhere", false);
            Call(widget, "ObserveMonitorPointerActivation", desktop, screens[0], true);
            Sample(desktop, screens[1], true, false);
            Assert(Here() == screens[0].DeviceName, "a second click can select the first desktop with the same HWND", false);
            Sample(secondHandle, screens[1], false, false);
            Assert(Here() == screens[1].DeviceName, "a subsequent app activation releases desktop selection", false);
            Sample(firstHandle, screens[0], false, false);
            Sample(secondHandle, screens[1], false, true);
            Assert(Here() == screens[0].DeviceName, "a minimized window's replacement stays on the working desktop", false);
            Sample(secondHandle, screens[0], false, false);
            Sample(secondHandle, screens[1], false, false);
            Assert(Here() == screens[1].DeviceName, "moving the replacement HWND across monitors releases the desktop hold without a mouse click", false);
            Sample(firstHandle, screens[0], false, false);
            Sample(secondHandle, screens[1], false, true);
            Call(widget, "PauseForKeyboardFocusSwitch");
            Sample(secondHandle, screens[1], false, false);
            Assert(Here() == screens[1].DeviceName, "an intentional keyboard switch can reselect the held replacement window", false);
            Sample(firstHandle, screens[0], false, false);
            Call(widget, "ObserveForegroundContext", desktop, screens[0], true, true, time);
            Call(widget, "ObserveForegroundContext", secondHandle, screens[1], false, false, time + 200);
            Call(widget, "ObserveForegroundContext", secondHandle, screens[1], false, false, time + 600);
            Assert(Here() == screens[0].DeviceName, "a transient shell HWND during minimization does not release the desktop hold", false);
            time += 2000;
            Sample(firstHandle, screens[0], false, false);
            Sample(secondHandle, screens[1], false, false);
            Assert(Here() == screens[1].DeviceName, "a later intentional foreground change still releases the minimize transition", false);

            Call(widget, "PositionWindowOnScreen", screens[0]);
            Call(widget, "ObserveForegroundMonitor", firstHandle, screens[1], time);
            Call(widget, "CancelPendingForegroundMonitor");
            Call(widget, "ObserveForegroundMonitor", firstHandle, screens[1], time + 1000);
            Assert(Here() == screens[0].DeviceName, "an interrupted shell transition cannot reuse an old pending deadline", false);
            Call(widget, "ObserveForegroundMonitor", firstHandle, screens[1], time + 1400);
            Assert(Here() == screens[1].DeviceName, "the post-transition candidate must stabilize again", false);

            settings.Settings.WidgetMonitorIndex = 0;
            Tick();
            Assert(GetField<IntPtr>(widget, "_monitorPointerHook") == IntPtr.Zero, "fixed monitor selection releases the input observer", false);
            settings.Settings.WidgetMonitorIndex = -1;
            Tick();
            Assert(GetField<IntPtr>(widget, "_monitorPointerHook") != IntPtr.Zero, "following mode reinstalls the observer", false);
            widget.Hide();
            Tick();
            Assert(GetField<IntPtr>(widget, "_monitorPointerHook") == IntPtr.Zero, "hiding for capture releases the observer", false);
            widget.Show();
            Tick();
            Assert(GetField<IntPtr>(widget, "_monitorPointerHook") != IntPtr.Zero, "capture restoration reinstalls the observer", false);
            Console.WriteLine("PASS: actual two-window activation, minimize/restore and moving an active HWND across both monitors; modeled shell desktop selection, interrupted focus transitions and observer lifetime.");
        }
        finally { second.Close(); }

        string Here() => Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).DeviceName;
        void Tick() { Call(widget, "FollowActiveMonitorTick", null!, EventArgs.Empty); Pump(); }
        void Settle() { Tick(); Thread.Sleep(450); Tick(); }
        void Place(Window window, Forms.Screen screen)
        {
            helper.GetMethod("CenterWindowOnPhysicalMonitor")!.Invoke(null,
                [window, new System.Drawing.Point(screen.Bounds.X + screen.Bounds.Width / 2, screen.Bounds.Y + screen.Bounds.Height / 2)]);
            Pump();
        }
        void Activate(Window window, Forms.Screen screen)
        {
            Assert(window.Activate(), "the initial focus target activates", false);
            Call(widget, "ObserveMonitorPointerActivation", new WindowInteropHelper(window).Handle, screen, false);
            Settle();
        }
        void Sample(IntPtr hwnd, Forms.Screen screen, bool desktop, bool minimized)
        {
            Call(widget, "ObserveForegroundContext", hwnd, screen, desktop, minimized, time);
            Call(widget, "ObserveForegroundContext", hwnd, screen, desktop, false, time + 400);
            time += 1000;
        }
    }
}
