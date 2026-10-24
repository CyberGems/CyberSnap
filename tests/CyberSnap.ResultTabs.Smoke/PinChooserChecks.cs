using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int GetRgnBox(IntPtr region, out TrayRect rectangle);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    private static void CheckPinChooserPresentation(string? renders)
    {
        double originalScale = UiScale.Current;
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        var helper = typeof(PinSourceWindow).Assembly.GetType("CyberSnap.UI.PopupWindowHelper")!;
        var hoverKey = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            foreach (var screen in Forms.Screen.AllScreens)
            {
                var monitor = MonitorFromPoint(new System.Drawing.Point(screen.Bounds.X + screen.Bounds.Width / 2,
                    screen.Bounds.Y + screen.Bounds.Height / 2), 2);
                var info = new TrayMonitorInfo { Size = Marshal.SizeOf<TrayMonitorInfo>() };
                Assert(GetMonitorInfo(monitor, ref info), "Pin chooser monitor bounds are available");
                var work = info.Work.Rectangle;
                var center = new System.Drawing.Point(work.X + work.Width / 2, work.Y + work.Height / 2);
                var dpi = ((double X, double Y))helper.GetMethod("GetScaleForPoint", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [center])!;
                Console.WriteLine($"Pin chooser monitor {screen.DeviceName}: work={work}, scale={dpi.X:P0}/{dpi.Y:P0}.");
                foreach (string language in new[] { "en", "es" })
                foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
                foreach (double scale in new[] { 1.2, 1.0, 1.1, 1.3, 1.4 })
                {
                    UiScale.Set(scale); Theme.SetMode(theme); LocalizationService.ApplyCurrentCulture(language);
                    helper.GetMethod("SetMonitorHintPoint")!.Invoke(null, [center]);
                    var chooser = new PinSourceWindow
                    {
                        IsHitTestVisible = false,
                        // Also exercise HWND creation on the non-primary monitor,
                        // rather than only moving a window created on the primary.
                        Left = (work.Left + 100) / dpi.X,
                        Top = (work.Top + 100) / dpi.Y
                    };
                    Forms.Form? backdrop = null;
                    IntPtr handle = IntPtr.Zero, backdropHandle = IntPtr.Zero;
                    try
                    {
                        // Changing a later hint must not retarget a chooser already constructed.
                        helper.GetMethod("SetMonitorHintPoint")!.Invoke(null, [new System.Drawing.Point(0, 0)]);
                        chooser.Show();
                        var deadline = DateTime.UtcNow.AddSeconds(3);
                        while (chooser.Opacity != 1 && DateTime.UtcNow < deadline)
                        {
                            Forms.Application.DoEvents(); Pump(); Thread.Sleep(10);
                        }
                        Pump();
                        handle = new WindowInteropHelper(chooser).Handle;
                        backdrop = (Forms.Form)Field(chooser, "_backdrop"); backdropHandle = backdrop.Handle;
                        Assert(GetWindowRect(handle, out var rectangle), "Pin chooser has a native HWND rectangle");
                        var bounds = rectangle.Rectangle;
                        var region = CreateRectRgn(0, 0, 0, 0);
                        try
                        {
                            int kind = GetWindowRgn(handle, region);
                            GetRgnBox(region, out var clip);
                            Assert(kind == 0,
                                $"the transparent chooser has no second native clipping region at {dpi.X:P0}: region={clip.Rectangle}, window={bounds.Size}");
                        }
                        finally { DeleteObject(region); }
                        Assert(Math.Abs(bounds.Left + bounds.Width / 2.0 - (work.Left + work.Width / 2.0)) <= 2
                            && Math.Abs(bounds.Top + bounds.Height / 2.0 - (work.Top + work.Height / 2.0)) <= 2,
                            $"Pin chooser is physically centered: {screen.DeviceName}, {language}, {theme}, UI={scale}, window={bounds}, work={work}");
                        var panel = (StackPanel)((Border)chooser.Content).Child;
                        Assert(Math.Abs(VisualTreeHelper.GetDpi(chooser).DpiScaleX - dpi.X) < .001
                            && Math.Abs(panel.LayoutTransform.Value.M11 - scale) < .001
                            && Math.Abs(bounds.Width - 620 * scale * dpi.X) <= 2 && chooser.Opacity == 1,
                            $"Pin chooser renders at its opening monitor's scale before becoming visible: expected={dpi.X}, render={VisualTreeHelper.GetDpi(chooser).DpiScaleX}, correction={panel.LayoutTransform.Value.M11}, native={GetDpiForWindow(handle)}, opacity={chooser.Opacity}, Width={chooser.Width}, ActualWidth={chooser.ActualWidth}");
                        Assert(bounds.Width <= work.Width && bounds.Height <= work.Height,
                            "the localized chooser fits its monitor at each tested UI scale");
                        // Inspect the HWND's actual composited pixels. A 96-DPI
                        // RenderTargetBitmap can look correct while Windows crops it.
                        int frames = 0;
                        EventHandler onRender = (_, _) => frames++;
                        CompositionTarget.Rendering += onRender;
                        try
                        {
                            chooser.InvalidateVisual();
                            var ready = DateTime.UtcNow.AddSeconds(2);
                            while (frames < 2 && DateTime.UtcNow < ready)
                            {
                                Forms.Application.DoEvents(); Pump(); Thread.Sleep(8);
                            }
                            Assert(frames >= 2, "the revealed chooser has presented its native surface before inspection");
                        }
                        finally { CompositionTarget.Rendering -= onRender; }
                        bool hasAffinity = GetWindowDisplayAffinity(handle, out uint affinity);
                        Assert(PinChooserExcluded(handle), "the chooser is registered for capture exclusion before inspection");
                        try
                        {
                            // PrintWindow can return a protected black surface. Only
                            // this synthetic test chooser is temporarily inspectable;
                            // its exclusion is checked and restored even on failure.
                            Assert(!hasAffinity || SetWindowDisplayAffinity(handle, 0), "the synthetic chooser permits native pixel inspection");
                            Marshal.ThrowExceptionForHR(FlushZoomTransitionComposition());
                            using var snapshot = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
                            using (var graphics = System.Drawing.Graphics.FromImage(snapshot))
                            {
                                var dc = graphics.GetHdc();
                                try { Assert(PrintWindow(handle, dc, 2), "the native chooser surface can be inspected"); }
                                finally { graphics.ReleaseHdc(dc); }
                            }
                            if (renders is not null && language == "es" && theme == AppThemeMode.Dark && scale == 1.2)
                                snapshot.Save(Path.Combine(renders, $"pin-native-{dpi.X:0.0}.png"));
                            var rightGutter = snapshot.GetPixel(bounds.Width - (int)(10 * dpi.X), (int)(bounds.Height * .65));
                            Assert(Math.Abs(rightGutter.R - Theme.BgPrimary.R) <= 2
                                && Math.Abs(rightGutter.G - Theme.BgPrimary.G) <= 2
                                && Math.Abs(rightGutter.B - Theme.BgPrimary.B) <= 2,
                                $"the native surface retains the right frame gutter without clipping a source card: pixel={rightGutter}, background={Theme.BgPrimary}");
                        }
                        finally
                        {
                            Assert((!hasAffinity || (SetWindowDisplayAffinity(handle, affinity)
                                && GetWindowDisplayAffinity(handle, out uint restored) && restored == affinity))
                                && PinChooserExcluded(handle), "capture exclusion is preserved after pixel inspection");
                        }
                        Assert(GetWindowRect(backdropHandle, out var veil) && veil.Rectangle == ScreenCapture.GetVirtualScreenBounds()
                            && backdrop.Visible && Math.Abs(backdrop.Opacity - .40) < .001,
                            "a 40% veil covers the whole physical desktop across mixed DPI monitors");
                        Assert(IsWidgetToolTipAbove(handle, backdropHandle),
                            "the selector stays above its topmost backdrop");
                        Assert(PinChooserExcluded(handle) && PinChooserExcluded(backdropHandle),
                            "the chooser and veil are both excluded from captures");

                        var buttons = VisualDescendants(chooser).OfType<Button>()
                            .Where(button => !string.IsNullOrEmpty(AutomationProperties.GetName(button))).ToArray();
                        Assert(buttons.Length == 3, "all three image sources remain accessible and localized");
                        var last = buttons.Last();
                        var sourceEdge = last.PointToScreen(new System.Windows.Point(last.ActualWidth, last.ActualHeight));
                        Assert(sourceEdge.X <= bounds.Right && sourceEdge.Y <= bounds.Bottom,
                            $"the third source card fits in the physical client area: card={sourceEdge}, window={bounds}");
                        foreach (var button in buttons)
                        {
                            button.ApplyTemplate();
                            var border = (Border)button.Template.FindName("Bd", button);
                            var glyph = VisualDescendants(button).OfType<System.Windows.Shapes.Path>().Single();
                            Assert(((SolidColorBrush)button.Foreground).Color == Theme.TextPrimary,
                                "source actions use neutral text at rest");
                            button.SetValue(hoverKey, true); Pump();
                            Assert(((SolidColorBrush)border.Background).Color == Theme.AccentSubtle
                                && ((SolidColorBrush)border.BorderBrush).Color == Theme.Accent
                                && ((SolidColorBrush)button.Foreground).Color == Theme.Accent
                                && ((SolidColorBrush)glyph.Fill).Color == Theme.Accent,
                                "hover uses About-style subtle fill, accent border, text and icon");
                            if (button == buttons[0]) Render(chooser, renders,
                                $"pin-chooser-hover-{language}-{theme}-dpi{dpi.X:0.0}-ui{scale:0.0}.png");
                            button.SetValue(hoverKey, false); Pump();
                        }
                        if (theme == AppThemeMode.Dark && scale == 1.2)
                            CheckPinChooserAttention(chooser, backdrop, bounds, dpi.X);
                        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(chooser),
                            Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        chooser.RaiseEvent(escape);
                        Assert(escape.Handled && !chooser.IsVisible && backdrop.IsDisposed
                            && Field(chooser, "_attentionTimer") is null,
                            "Escape closes the chooser, stops pending attention and removes its desktop veil immediately");
                    }
                    finally { chooser.Close(); Pump(); }
                    Assert(!PinChooserExcluded(handle) && !PinChooserExcluded(backdropHandle),
                        "closing releases both native capture exclusions without orphaning a backdrop");
                }
            }
        }
        finally
        {
            helper.GetMethod("ClearMonitorHintPoint")!.Invoke(null, null);
            UiScale.Set(originalScale); Theme.SetMode(AppThemeMode.System);
            LocalizationService.ApplyCurrentCulture(originalLanguage);
        }
    }

    private static void CheckPinChooserAttention(PinSourceWindow chooser, Forms.Form backdrop,
        System.Drawing.Rectangle origin, double dpi)
    {
        var disabled = typeof(PinSourceWindow).Assembly.GetType("CyberSnap.UI.Motion")!
            .GetProperty("Disabled", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool originalMotion = (bool)disabled.GetValue(null)!;
        var mouseDown = typeof(Forms.Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void ClickOutside() => mouseDown.Invoke(backdrop, [new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 5, 5, 0)]);
        var handle = new WindowInteropHelper(chooser).Handle;
        try
        {
            disabled.SetValue(null, false);
            ClickOutside();
            var timer = Field(chooser, "_attentionTimer");
            Assert(timer is not null, "an outside click starts gentle attention without dismissing Pin");
            ClickOutside();
            Assert(ReferenceEquals(timer, Field(chooser, "_attentionTimer")),
                "repeated outside clicks do not restart the shake or accumulate position offsets");
            int peak = 0;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (Field(chooser, "_attentionTimer") is not null && DateTime.UtcNow < deadline)
            {
                Forms.Application.DoEvents(); Pump(); Thread.Sleep(8);
                GetWindowRect(handle, out var current);
                peak = Math.Max(peak, Math.Abs(current.Left - origin.Left));
                Assert(current.Top == origin.Top, "attention keeps the chooser's vertical position", false);
            }
            Assert(peak > 0 && peak <= Math.Ceiling(4 * dpi)
                && Field(chooser, "_attentionTimer") is null
                && GetWindowRect(handle, out var final) && final.Rectangle == origin
                && chooser.IsVisible && backdrop.Visible,
                "the short shake is bounded and restores the exact physical position on each monitor");
            disabled.SetValue(null, true);
            ClickOutside();
            Assert(Field(chooser, "_attentionTimer") is null
                && GetWindowRect(handle, out var still) && still.Rectangle == origin,
                "Disable animations keeps outside-click attention stationary");
            disabled.SetValue(null, false);
            ClickOutside();
            disabled.SetValue(null, true);
            ClickOutside();
            Assert(Field(chooser, "_attentionTimer") is null
                && GetWindowRect(handle, out var stopped) && stopped.Rectangle == origin,
                "disabling motion during attention cancels it and restores the original position");
            // The caller closes immediately afterward, exercising timer cleanup
            // while attention is still active rather than only after completion.
            disabled.SetValue(null, false);
            ClickOutside();
        }
        finally { disabled.SetValue(null, originalMotion); }
    }

    private static bool PinChooserExcluded(IntPtr handle)
    {
        var exclusion = typeof(ScreenCapture).Assembly.GetType("CyberSnap.Capture.CaptureWindowExclusion")!;
        return ((IEnumerable)exclusion.GetField("RegisteredWindows", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
            .Cast<object>().Any(window => (IntPtr)window.GetType().GetProperty("Handle")!.GetValue(window)! == handle);
    }
}
