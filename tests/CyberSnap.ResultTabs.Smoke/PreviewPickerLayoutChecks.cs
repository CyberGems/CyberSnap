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
    private static void CheckPreviewPickerLayout(CapturePreviewDialog preview, string language, string? renders,
        string buttonName = "PreviewBackgroundColorBtn")
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
                Element<Button>(preview, buttonName).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
                var presenter = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
                var contentBounds = presenter.TransformToAncestor(picker).TransformBounds(new Rect(presenter.RenderSize));
                var barBounds = bar.TransformToAncestor(picker).TransformBounds(new Rect(bar.RenderSize));
                var thumbBounds = thumb.TransformToAncestor(picker).TransformBounds(new Rect(thumb.RenderSize));
                Assert(contentBounds.Right <= barBounds.Left + .5,
                    "the scrollbar has a reserved lane and does not cover RGB or HEX fields");
                Assert(picker.ActualWidth - 6 - thumbBounds.Right <= 10,
                    "the visible scrollbar stays close to the flyout's inner right edge");
                Assert(thumb.Background is SolidColorBrush brush && brush.Color == Theme.TextSecondary,
                    "the picker scrollbar uses the same secondary-text color as Settings and Gallery");
                track.Thumb.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(0, track.ActualHeight * 2) { RoutedEvent = Thumb.DragDeltaEvent });
                track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, track.ActualHeight * 2, false) { RoutedEvent = Thumb.DragCompletedEvent });
                Pump();
                Assert(scroll.ScrollableHeight > 0 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) <= 1,
                    "dragging the scrollbar reaches the bottom of the picker body");
                scroll.ScrollToHome(); Pump();
                if (renders is not null)
                {
                    Directory.CreateDirectory(renders);
                    var image = new RenderTargetBitmap((int)Math.Ceiling(picker.ActualWidth * scale),
                        (int)Math.Ceiling(picker.ActualHeight * scale), 96, 96, PixelFormats.Pbgra32);
                    image.Render(picker);
                    using var output = File.Create(Path.Combine(renders, $"picker-fit-{buttonName}-{monitor}-{scale}-{theme}-{language}.png"));
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
