using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckToastPlacement(SettingsService settings)
    {
        var screens = Forms.Screen.AllScreens.OrderByDescending(screen => screen.Primary)
            .ThenBy(screen => screen.Bounds.X).ThenBy(screen => screen.Bounds.Y).ToArray();
        var specType = typeof(ToastWindow).Assembly.GetType("CyberSnap.UI.ToastSpec")!;
        var show = typeof(ToastWindow).GetMethod("Show", BindingFlags.Static | BindingFlags.NonPublic)!;
        var primary = new Window { Left = 20, Top = 20, Width = 40, Height = 40,
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };

        try
        {
            primary.Show();
            Pump();
            // Start with the secondary monitor so its first toast has to leave the primary DPI context.
            for (int index = screens.Length - 1; index >= 0; index--)
            {
                var screen = screens[index];
                var center = new Drawing.Point(screen.Bounds.Left + screen.Bounds.Width / 2,
                    screen.Bounds.Top + screen.Bounds.Height / 2);
                var monitor = MonitorFromPoint(center, 2);
                var info = new TrayMonitorInfo { Size = Marshal.SizeOf<TrayMonitorInfo>() };
                Assert(GetMonitorInfo(monitor, ref info), "toast target has a native monitor work area");
                ToastWindow.SetMonitorIndex(index);

                var first = ShowToast(ToastPosition.TopCenter, "Top center");
                AssertPlacement(first, ToastPosition.TopCenter, "first");
                foreach (var position in Enum.GetValues<ToastPosition>())
                {
                    var reused = ShowToast(position, "This longer message changes the window size.");
                    Assert(ReferenceEquals(first, reused), "the active toast is reused after a new notification");
                    AssertPlacement(reused, position, "reused");
                }

                first.Close();
                Pump();

                void AssertPlacement(ToastWindow toast, ToastPosition position, string state)
                {
                    var hwnd = new WindowInteropHelper(toast).Handle;
                    Assert(GetWindowRect(hwnd, out var bounds), "toast has a native window rectangle");
                    var work = info.Work.Rectangle;
                    var rect = bounds.Rectangle;
                    var dpi = GetDpiForWindow(hwnd);
                    double edge = 8 * dpi / 96.0;
                    double x = position switch
                    {
                        ToastPosition.TopLeft or ToastPosition.BottomLeft or ToastPosition.Left => work.Left + edge,
                        ToastPosition.TopRight or ToastPosition.BottomRight or ToastPosition.Right => work.Right - rect.Width - edge,
                        _ => work.Left + (work.Width - rect.Width) / 2.0,
                    };
                    double y = position switch
                    {
                        ToastPosition.TopLeft or ToastPosition.TopCenter or ToastPosition.TopRight => work.Top + edge,
                        ToastPosition.BottomLeft or ToastPosition.BottomCenter or ToastPosition.BottomRight => work.Bottom - rect.Height - edge,
                        _ => work.Top + (work.Height - rect.Height) / 2.0,
                    };
                    Assert(Forms.Screen.FromHandle(hwnd).DeviceName == screen.DeviceName
                        && Math.Abs(rect.Left - x) <= 1 && Math.Abs(rect.Top - y) <= 1,
                        $"{state} {position} toast anchors to {screen.DeviceName} at {dpi} DPI, "
                        + $"actual={rect}, expected origin=({x}, {y}), work={work}");
                }
            }
        }
        finally
        {
            foreach (var toast in Application.Current.Windows.OfType<ToastWindow>().ToArray())
                toast.Close();
            primary.Close();
            ToastWindow.SetMonitorIndex(settings.Settings.ToastMonitorIndex);
            ToastWindow.SetPosition(settings.Settings.ToastPosition);
            Pump();
        }

        ToastWindow ShowToast(ToastPosition position, string body)
        {
            var spec = Activator.CreateInstance(specType, nonPublic: true)!;
            specType.GetProperty("Title")!.SetValue(spec, "Toast placement");
            specType.GetProperty("Body")!.SetValue(spec, body);
            specType.GetProperty("SuppressSound")!.SetValue(spec, true);
            specType.GetProperty("BypassQuietHours")!.SetValue(spec, true);
            specType.GetProperty("DurationSeconds")!.SetValue(spec, 60d);
            ToastWindow.SetPosition(position);
            show.Invoke(null, [spec]);
            var toast = Application.Current.Windows.OfType<ToastWindow>().Single();
            AwaitCondition(() => toast.Opacity == 1, "toast has not completed its first placement", 3000);
            Pump();
            return toast;
        }
    }
}
