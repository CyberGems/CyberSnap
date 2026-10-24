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
                foreach (double scale in new[] { 1.0, 1.4 })
                {
                    UiScale.Set(scale); Theme.SetMode(theme); LocalizationService.ApplyCurrentCulture(language);
                    helper.GetMethod("SetMonitorHintPoint")!.Invoke(null, [center]);
                    var chooser = new PinSourceWindow { IsHitTestVisible = false };
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
                        Assert(GetWindowRect(backdropHandle, out var veil) && veil.Rectangle == ScreenCapture.GetVirtualScreenBounds()
                            && backdrop.Visible && Math.Abs(backdrop.Opacity - .20) < .001,
                            "a subtle veil covers the whole physical desktop across mixed DPI monitors");
                        Assert(IsWidgetToolTipAbove(handle, backdropHandle),
                            "the selector stays above its topmost backdrop");
                        Assert(PinChooserExcluded(handle) && PinChooserExcluded(backdropHandle),
                            "the chooser and veil are both excluded from captures");

                        var buttons = VisualDescendants(chooser).OfType<Button>()
                            .Where(button => !string.IsNullOrEmpty(AutomationProperties.GetName(button))).ToArray();
                        Assert(buttons.Length == 3, "all three image sources remain accessible and localized");
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
                        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(chooser),
                            Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                        chooser.RaiseEvent(escape);
                        Assert(escape.Handled && !chooser.IsVisible && backdrop.IsDisposed,
                            "Escape closes the chooser and removes its desktop veil immediately");
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

    private static bool PinChooserExcluded(IntPtr handle)
    {
        var exclusion = typeof(ScreenCapture).Assembly.GetType("CyberSnap.Capture.CaptureWindowExclusion")!;
        return ((IEnumerable)exclusion.GetField("RegisteredWindows", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)
            .Cast<object>().Any(window => (IntPtr)window.GetType().GetProperty("Handle")!.GetValue(window)! == handle);
    }
}
