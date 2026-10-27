using System.Windows;
using System.Windows.Controls;
using CyberSnap.Models;
using CyberSnap.UI;

internal static partial class Program
{
    private static void CheckPreviewControlsLayout(CapturePreviewDialog preview, string language, string? renders)
    {
        var host = Element<Border>(preview, "PreviewControlsOverlay");
        var frame = Element<Border>(preview, "PreviewFrame");
        var zoom = Element<Border>(preview, "ZoomControlsOverlay");
        var scale = Element<Border>(preview, "ScaleControlsOverlay");
        var dimensions = Element<TextBlock>(preview, "ScaleScaledText");
        double originalWidth = preview.Width, originalHeight = preview.Height;
        try
        {
            Call(preview, "ApplyOutlineSettings", true, 32, System.Drawing.Color.Black.ToArgb());
            Assert(dimensions.Text == "1264 × 864px"
                && (string)Element<StackPanel>(preview, "ScaleDimensionsPanel").ToolTip == "1200 × 800px → 1264 × 864px",
                "the compact size display shows final pixels and preserves original dimensions in contextual help");
            foreach (double uiScale in new[] { 1.0, UiScale.Default, UiScale.Max })
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            {
                UiScale.Set(uiScale);
                UiScale.ApplyToWindow(preview, Element<FrameworkElement>(preview, "RootBorder"), false);
                preview.Width = 860 * uiScale; preview.Height = 520 * uiScale;
                Theme.SetMode(theme); preview.ApplyTheme(); Pump();
                // Longest legal width and height fields, without allocating a huge bitmap.
                dimensions.Text = "8192 × 6103px"; Pump();
                CheckFit();
                var zoomBounds = Bounds(zoom, host);
                var scaleBounds = Bounds(scale, host);
                bool sameRow = Math.Abs(zoomBounds.Top - scaleBounds.Top) <= 1;
                // Windows can limit the requested default bounds to the monitor,
                // particularly on small desktops with the maximum UI scale.
                bool monitorConstrained = preview.ActualWidth < 860 * uiScale - 1;
                Assert(sameRow || (monitorConstrained && scaleBounds.Top >= zoomBounds.Bottom),
                    $"default controls share a row or wrap on a constrained monitor in {language}, UI={uiScale}, {theme}: "
                    + $"requested={860 * uiScale:0}, actual={preview.ActualWidth:0}, frame={frame.ActualWidth:0}, zoom={zoomBounds}, scale={scaleBounds}");
                Call(preview, "SetZoomOverlayVisibility", true); Pump();
                Render(preview, renders, $"preview-controls-{language}-{uiScale}-{theme}.png");
                foreach (double width in new[] { 580.0, 730.0 })
                {
                    preview.Width = width * uiScale; Pump();
                    CheckFit();
                }
            }
            Call(preview, "SetZoomOverlayVisibility", true); Pump();
            Assert(Element<Button>(preview, "Scale1xBtn").Focus(), "the scale controls accept keyboard focus");
            SetField(preview, "_zoomPointerInside", false);
            Call(preview, "UpdateZoomControlsVisibility");
            Assert(host.IsHitTestVisible, "keyboard focus within either group keeps the whole toolbar available");
        }
        finally
        {
            UiScale.Set(1);
            UiScale.ApplyToWindow(preview, Element<FrameworkElement>(preview, "RootBorder"), false);
            preview.Width = originalWidth; preview.Height = originalHeight;
            Call(preview, "ApplyOutlineSettings", false, 2, System.Drawing.Color.Black.ToArgb());
            Pump();
        }

        static Rect Bounds(FrameworkElement element, FrameworkElement ancestor) =>
            element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

        void CheckFit()
        {
            var zoomBounds = Bounds(zoom, host);
            var scaleBounds = Bounds(scale, host);
            Assert(!zoomBounds.IntersectsWith(scaleBounds), "zoom and scale groups stay separate when the toolbar wraps");
            var frameBounds = Bounds(host, frame);
            Assert(frameBounds.Left >= 0 && frameBounds.Top >= 0
                && frameBounds.Right <= frame.ActualWidth + 1 && frameBounds.Bottom <= frame.ActualHeight + 1,
                $"the complete toolbar stays inside the preview area at width {preview.Width:0}: toolbar={frameBounds}, frame={frame.ActualWidth:0} x {frame.ActualHeight:0}");
            foreach (string name in new[] { "ZoomOutBtn", "ZoomLevelBtn", "ZoomInBtn", "ZoomFitBtn",
                "PreviewBackgroundBtn", "PreviewBackgroundColorBtn", "PreviewOutlineBtn", "Scale1xBtn", "Scale2xBtn", "Scale4xBtn", "ScaleScaledText" })
            {
                var element = Element<FrameworkElement>(preview, name);
                var bounds = Bounds(element, host);
                Assert(element.ActualWidth > 0 && element.ActualHeight > 0
                    && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= host.ActualWidth + 1 && bounds.Bottom <= host.ActualHeight + 1,
                    $"{name} remains fully visible at width {preview.Width:0}", announce: false);
            }
        }
    }
}
