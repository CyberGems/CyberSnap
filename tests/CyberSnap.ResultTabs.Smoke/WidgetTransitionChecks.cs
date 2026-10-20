using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Interop;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

internal static partial class Program
{
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
