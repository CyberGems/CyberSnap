using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using CyberSnap.Capture;
using CyberSnap.Models;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckZoomTransitionContinuity(Bitmap source, string? renderDirectory)
    {
        var screen = Forms.Screen.AllScreens.First(s => s.Bounds.Width >= 900 && s.Bounds.Height >= 700);
        var bounds = new Rectangle(screen.Bounds.X + 50, screen.Bounds.Y + 50, 800, 600);
        foreach (bool cpu in new[] { false, true })
        foreach (bool fullView in new[] { false, true })
        {
            // A controlled surface beneath Zoom makes even a brief desktop reveal observable,
            // without taking pictures of the user's desktop or changing capture preferences.
            using var underlay = new Forms.Form
            {
                Bounds = bounds, StartPosition = Forms.FormStartPosition.Manual,
                FormBorderStyle = Forms.FormBorderStyle.None, BackColor = Color.Magenta,
                ShowInTaskbar = false, TopMost = true
            };
            underlay.Show(); underlay.Update();
            using var zoom = new StandaloneZoomForm((Bitmap)source.Clone(), bounds,
                new Point(bounds.X + 400, bounds.Y + 300), new AppSettings { ZoomAnimate = false });
            if (cpu) SetField(zoom, "_rendererUnavailable", true);
            zoom.Show();
            ((Forms.Timer)Field(zoom, "_timer")).Stop();
            zoom.Refresh();
            // Ignore only the help card, which intentionally disappears during selection.
            // Keep the same strict 300x160 image comparison away from its responsive position.
            var help = (Rectangle)Call(zoom, "HudLayout")!;
            var sample = new Rectangle(250, help.Top >= 300 ? 60 : 380, 300, 160);
            Assert(zoom.ClientRectangle.Contains(sample) && !help.IntersectsWith(sample),
                "the continuity sample excludes only the intentionally hidden Zoom help");
            using var before = ReadZoomTransitionPatch(bounds, sample);
            Assert(before.GetPixel(100, 80).ToArgb() != Color.Magenta.ToArgb(),
                "the transition fixture observes the actual magnified window above its sentinel background", false);
            int hides = 0;
            zoom.VisibleChanged += (_, _) => { if (!zoom.Visible) hides++; };
            zoom.CaptureRequested += request =>
            {
                Assert(zoom.Visible && IsZoomTransitionWindowVisible(zoom.Handle),
                    "Zoom stays on screen before the capture overlay is even constructed: CPU=" + cpu + ", full=" + fullView);
                using var during = ReadZoomTransitionPatch(bounds, sample);
                Assert(EqualZoomBitmaps(before, during),
                    "composited pixels keep the identical enlarged framing throughout capture preparation");
                using var clean = zoom.RenderView(includeChrome: true);
                Assert(EqualZoomBitmaps(request.Frame, clean),
                    "the backing window removes its Zoom help while capture is active");
                if (renderDirectory is not null)
                {
                    Directory.CreateDirectory(renderDirectory);
                    during.Save(Path.Combine(renderDirectory, $"zoom-transition-{(cpu ? "cpu" : "gpu")}-{(fullView ? "enter" : "region")}.png"));
                }
            };
            using var output = ExerciseZoomCapture(zoom, fullView, overlay =>
            {
                Assert(overlay.Owner == zoom && zoom.Visible && IsZoomTransitionWindowVisible(zoom.Handle),
                    "the real capture dialog stays above the frozen backing window");
                Call(overlay, "HandleEscapeKey");
            });
            using var after = ReadZoomTransitionPatch(bounds, sample);
            Assert(output is null && hides == 0 && EqualZoomBitmaps(before, after),
                "cancel returns to the same composited frame without hiding or recreating Zoom");
        }
    }

    private static Bitmap ReadZoomTransitionPatch(Rectangle bounds, Rectangle sample)
    {
        Marshal.ThrowExceptionForHR(FlushZoomTransitionComposition());
        var image = new Bitmap(sample.Width, sample.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.CopyFromScreen(bounds.X + sample.X, bounds.Y + sample.Y, 0, 0, image.Size, CopyPixelOperation.SourceCopy);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")]
    private static extern int FlushZoomTransitionComposition();
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomTransitionWindowVisible(IntPtr window);
}
