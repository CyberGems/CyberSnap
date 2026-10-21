using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CyberSnap.Services;
using CyberSnap.UI;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TrayRect
    {
        public int Left, Top, Right, Bottom;
        public readonly Drawing.Rectangle Rectangle => Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrayMonitorInfo
    {
        public int Size;
        public TrayRect Bounds, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Drawing.Point point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref TrayMonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out TrayRect rect);

    private static void CheckTrayMenuMonitors(string? renderDirectory = null)
    {
        CheckSimulatedTrayPlacement();
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        try
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var monitor = MonitorFromPoint(new Drawing.Point(screen.Bounds.X + screen.Bounds.Width / 2,
                    screen.Bounds.Y + screen.Bounds.Height / 2), 2);
                var info = new TrayMonitorInfo { Size = Marshal.SizeOf<TrayMonitorInfo>() };
                Assert(GetMonitorInfo(monitor, ref info), "native monitor bounds are available", false);
                var bounds = info.Bounds.Rectangle;
                var work = info.Work.Rectangle;
                Console.WriteLine($"Tray monitor {screen.DeviceName}: bounds={bounds}, work={work}.");
                foreach (string language in new[] { "en", "es" })
                foreach (bool compact in new[] { false, true })
                foreach (var anchor in new[] {
                    new Drawing.Point(bounds.Left, bounds.Top + bounds.Height / 2),
                    new Drawing.Point(bounds.Right - 1, bounds.Bottom - 1) })
                {
                    LocalizationService.ApplyCurrentCulture(language);
                    // No tray icon is needed: this fixture opens the real panel directly
                    // and never invokes capture commands or writes panel preferences.
                    var menu = new TrayContextMenuWindow(null!, anchor) { IsHitTestVisible = false, Left = 400, Top = 100 };
                    // Keep placement checks independent of foreground activity on the shared desktop.
                    menu.Deactivated -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), menu,
                        typeof(TrayContextMenuWindow).GetMethod("Window_Deactivated", BindingFlags.Instance | BindingFlags.NonPublic)!);
                    try
                    {
                        SetField(menu, "_isCompact", compact);
                        Call(menu, "ApplyCompactMode", false);
                        SetField(menu, "_showRecording", true);
                        SetField(menu, "_showTools", true);
                        SetField(menu, "_showGallery", true);
                        Call(menu, "ApplySectionVisibility", false);
                        menu.Show();
                        Assert(Element<System.Windows.Controls.Grid>(menu, "ToolsSection")
                            .IsAncestorOf(Element<System.Windows.Controls.Button>(menu, "ZoomBtn")),
                            "Zoom belongs to the tray's standalone tools section", false);
                        AssertVisible();
                        Pump();
                        AssertVisible();
                        var tools = Element<System.Windows.Controls.Grid>(menu, "ToolsSection");
                        var zoom = Element<System.Windows.Controls.Button>(menu, "ZoomBtn");
                        var zoomContent = (System.Windows.Controls.StackPanel)zoom.Content;
                        var contentBounds = zoomContent.TransformToAncestor(zoom).TransformBounds(new Rect(zoomContent.RenderSize));
                        Assert(zoom.ActualWidth >= tools.ActualWidth - 9
                            && Math.Abs(contentBounds.Left + contentBounds.Width / 2 - zoom.ActualWidth / 2) < 1,
                            "the last tool occupies a full-width row with its icon and label centered", false);
                        if (renderDirectory is not null && language == "es" && !compact && anchor.X == bounds.Left)
                            Render(menu, renderDirectory, "tray-zoom-" + (screen.Primary ? "primary" : "secondary") + ".png");
                        if (compact && anchor.X == bounds.Right - 1)
                        {
                            foreach (double scale in new[] { 1.25, 1.5 })
                            {
                                ChangeTrayDpi(menu, scale);
                                Pump();
                                Assert(Math.Abs(VisualTreeHelper.GetDpi(menu).DpiScaleX - scale) < 0.001,
                                    "the DPI transition reaches WPF's rendering context", false);
                                AssertVisible();
                            }
                        }
                    }
                    finally { menu.Close(); }

                    void AssertVisible()
                    {
                        var hwnd = new WindowInteropHelper(menu).Handle;
                        Assert(GetWindowRect(hwnd, out var rect), "tray menu has native bounds", false);
                        string context = $"{screen.DeviceName}, {language}, compact={compact}, anchor={anchor}, DPI={GetDpiForWindow(hwnd)}, rect={rect.Rectangle}, work={work}";
                        Assert(work.Contains(rect.Rectangle), "the entire tray menu stays in the clicked monitor's work area: " + context, false);
                        Assert(!((bool)Call(menu, "NeedsRefit")!), "initial placement needs no manual repositioning: " + context, false);
                    }
                }
            }
        }
        finally { LocalizationService.ApplyCurrentCulture(originalLanguage); }
    }

    private static void ChangeTrayDpi(Window menu, double scale)
    {
        var hwnd = new WindowInteropHelper(menu).Handle;
        Assert(GetWindowRect(hwnd, out var rect), "the menu is measurable before a DPI transition", false);
        var suggested = Marshal.AllocHGlobal(16);
        try
        {
            // Exercise WM_DPICHANGED without changing the user's display settings.
            Marshal.WriteInt32(suggested, 0, rect.Left);
            Marshal.WriteInt32(suggested, 4, rect.Top);
            Marshal.WriteInt32(suggested, 8, rect.Left + (int)Math.Ceiling(menu.ActualWidth * scale));
            Marshal.WriteInt32(suggested, 12, rect.Top + (int)Math.Ceiling(menu.ActualHeight * scale));
            int dpi = (int)Math.Round(96 * scale);
            SendMessageW(hwnd, 0x02E0, new IntPtr(dpi | (dpi << 16)), suggested);
        }
        finally { Marshal.FreeHGlobal(suggested); }
    }

    private static void CheckSimulatedTrayPlacement()
    {
        var calculate = typeof(TrayContextMenuWindow).GetMethod("CalculatePlacement", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        foreach (int edge in new[] { 0, 1, 2, 3, 4 }) // bottom, left, right, top, auto-hide
        foreach (var origin in new[] { new Drawing.Point(0, 0), new Drawing.Point(-2560, -600), new Drawing.Point(1920, 240) })
        foreach (int height in new[] { 500, 730 })
        {
            var bounds = new Drawing.Rectangle(origin, new Drawing.Size((int)(1920 * scale), (int)(1080 * scale)));
            var work = bounds;
            int bar = (int)(100 * scale);
            switch (edge)
            {
                case 0: work.Height -= bar; break;
                case 1: work.X += bar; work.Width -= bar; break;
                case 2: work.Width -= bar; break;
                case 3: work.Y += bar; work.Height -= bar; break;
            }
            var size = new Drawing.Size((int)(310 * scale), (int)(height * scale));
            foreach (var cursor in new[] { bounds.Location, new Drawing.Point(bounds.Right - 1, bounds.Bottom - 1),
                new Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2) })
            {
                var dpi = new DpiScale(scale, scale);
                var position = (Drawing.Point)calculate.Invoke(null, [bounds, work, cursor, size, dpi])!;
                var rect = new Drawing.Rectangle(position, size);
                string context = $"edge={edge}, DPI={scale}, origin={origin}, height={height}";
                Assert(work.Contains(rect), "tray placement contains the whole panel across DPI and desktop origins: " + context, false);
                int gap = (int)Math.Round(8 * scale);
                Assert(edge switch
                {
                    1 => rect.Left == work.Left + gap,
                    2 => rect.Right == work.Right - gap,
                    3 => rect.Top == work.Top + gap,
                    _ => rect.Bottom == work.Bottom - gap,
                }, "the panel opens beside its monitor's taskbar: " + context, false);

                // Translating the desktop must translate the result by exactly the same amount.
                var localBounds = bounds; localBounds.Offset(-origin.X, -origin.Y);
                var localWork = work; localWork.Offset(-origin.X, -origin.Y);
                var localCursor = new Drawing.Point(cursor.X - origin.X, cursor.Y - origin.Y);
                var localPosition = (Drawing.Point)calculate.Invoke(null, [localBounds, localWork, localCursor, size, dpi])!;
                Assert(position.X - origin.X == localPosition.X && position.Y - origin.Y == localPosition.Y,
                    "negative and offset monitor origins do not change placement: " + context, false);
            }

            var snugSize = new Drawing.Size(work.Width - 2, work.Height - 2);
            var snug = (Drawing.Point)calculate.Invoke(null, [bounds, work, bounds.Location, snugSize, new DpiScale(scale, scale)])!;
            Assert(work.Contains(new Drawing.Rectangle(snug, snugSize)),
                "a panel that fits remains visible even when the normal margin does not", false);
        }
    }
}
