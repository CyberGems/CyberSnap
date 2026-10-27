using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyberSnap.Models;
using CyberSnap.UI;

internal static partial class Program
{
    private static void CheckPreviewPickerLayout(CapturePreviewDialog preview, string language, string? renders)
    {
        var helper = typeof(CapturePreviewDialog).Assembly.GetType("CyberSnap.UI.PopupWindowHelper")!;
        var nativeInfo = helper.GetMethod("TryGetNativeMonitorInfo", BindingFlags.Static | BindingFlags.NonPublic)!;
        var center = helper.GetMethod("CenterWindowOnPhysicalMonitor", BindingFlags.Static | BindingFlags.Public)!;
        int monitor = 0;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            object?[] info = [screen, null, null];
            Assert((bool)nativeInfo.Invoke(null, info)!, "picker placement uses the monitor's native working area");
            var work = (System.Drawing.Rectangle)info[2]!;
            foreach (double scale in new[] { 1.0, 1.4 })
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            {
                UiScale.Set(scale);
                UiScale.ApplyToWindow(preview, Element<FrameworkElement>(preview, "RootBorder"), false);
                Theme.SetMode(theme); preview.ApplyTheme(); Pump();
                center.Invoke(null, [preview, new System.Drawing.Point(work.X + work.Width / 2, work.Y + work.Height / 2)]);
                Thread.Sleep(80); Pump(); Pump();
                Element<Button>(preview, "PreviewBackgroundColorBtn").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                var popup = (Popup)Field(preview, "_previewBackgroundColorPopup");
                var picker = (FrameworkElement)Field(preview, "_previewBackgroundColorPicker");
                Assert(popup.IsOpen, "the picker opens after changing monitor DPI and UI scale");
                CheckBounds("natural");
                picker.MaxHeight = Math.Min(picker.MaxHeight, 260);
                Pump(); preview.UpdateLayout(); Pump();
                CheckBounds("constrained");
                var scroll = (ScrollViewer)Field(picker, "_bodyScroll");
                var footer = (Grid)Field(picker, "_actionRow");
                var footerBounds = footer.TransformToAncestor(picker).TransformBounds(new Rect(footer.RenderSize));
                Assert(scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
                    && footerBounds.Top >= 0 && footerBounds.Bottom <= picker.ActualHeight + 1,
                    "scrolling stays inside the picker while Reset and Accept remain visible");
                var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                var thumb = VisualDescendants(track.Thumb).OfType<Border>().Single();
                Assert(thumb.Background is SolidColorBrush brush && brush.Color == Theme.TextSecondary,
                    "the picker scrollbar uses the same secondary-text color as Settings and Gallery");
                if (renders is not null)
                {
                    Directory.CreateDirectory(renders);
                    var image = new RenderTargetBitmap((int)Math.Ceiling(picker.ActualWidth * scale),
                        (int)Math.Ceiling(picker.ActualHeight * scale), 96, 96, PixelFormats.Pbgra32);
                    image.Render(picker);
                    using var output = File.Create(Path.Combine(renders, $"picker-fit-{monitor}-{scale}-{theme}-{language}.png"));
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(output);
                }
                Call(preview, "ClosePreviewBackgroundColorPicker", false); Pump();

                void CheckBounds(string size)
                {
                    var tl = picker.PointToScreen(new Point(0, 0));
                    var br = picker.PointToScreen(new Point(picker.ActualWidth, picker.ActualHeight));
                    Assert(tl.X >= work.Left - 2 && tl.Y >= work.Top - 2
                        && br.X <= work.Right + 2 && br.Y <= work.Bottom + 2,
                        $"picker fits the actual {screen.DeviceName} working area, {VisualTreeHelper.GetDpi(picker).PixelsPerInchY:0} DPI, UI={scale}, {size}: {tl}..{br}, work={work}");
                }
            }
            monitor++;
        }
        UiScale.Set(1);
        UiScale.ApplyToWindow(preview, Element<FrameworkElement>(preview, "RootBorder"), false);
    }
}
