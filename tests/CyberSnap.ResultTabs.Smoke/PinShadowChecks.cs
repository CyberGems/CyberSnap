using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using CyberSnap.Capture;
using CyberSnap.Models;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetShadowTestWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    private static void CheckPinWindowShadow(string? renders)
    {
        var app = (CyberSnap.App)System.Windows.Application.Current;
        var pins = (List<PinImageForm>)ZoomAppField(app, "_pinnedImages")!;
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var monitor = MonitorFromPoint(new Point(screen.Bounds.X + screen.Bounds.Width / 2,
                screen.Bounds.Y + screen.Bounds.Height / 2), 2);
            var info = new TrayMonitorInfo { Size = Marshal.SizeOf<TrayMonitorInfo>() };
            Assert(GetMonitorInfo(monitor, ref info), "the shadow fixture resolves physical monitor bounds");
            var work = info.Work.Rectangle;
            var anchor = new Point(work.Left + work.Width / 2, work.Top + work.Height / 2);
            using var background = new Forms.Form { AutoScaleMode = Forms.AutoScaleMode.None,
                FormBorderStyle = Forms.FormBorderStyle.None, StartPosition = Forms.FormStartPosition.Manual,
                ShowInTaskbar = false, TopMost = true, BackColor = Color.White,
                Bounds = new Rectangle(anchor.X - 250, anchor.Y - 190, 500, 380) };
            background.Show();
            background.Bounds = new Rectangle(anchor.X - 250, anchor.Y - 190, 500, 380);
            using var image = new Bitmap(320, 200);
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.SlateBlue);
            using var pin = new PinImageForm((Bitmap)image.Clone(), anchor, new AppSettings { PinIncludeInCaptures = true });
            pins.Add(pin);
            pin.FormClosed += (_, _) => pins.Remove(pin);
            ((Forms.Timer)Field(pin, "_hoverTimer")).Stop();
            pin.Show();
            Rectangle window = pin.Bounds;
            var capture = Rectangle.Inflate(window, 48, 48);
            Bitmap ReadSurface(bool shadow)
            {
                var backdrop = Rectangle.Inflate(capture, 6, 6);
                Assert(SetShadowTestWindowPos(background.Handle, pin.Handle, backdrop.X, backdrop.Y, backdrop.Width, backdrop.Height, 0x0010 | 0x0040),
                    "the synthetic white backdrop occupies the physical test area directly beneath Pin");
                background.Refresh();
                pin.ApplyAppearancePreferences(shadow, false);
                pin.BringToFront(); pin.Activate(); Forms.Application.DoEvents(); Pump();
                SetField(pin, "_hover", false); pin.Refresh(); Thread.Sleep(80);
                Marshal.ThrowExceptionForHR(FlushZoomTransitionComposition());
                var bitmap = ScreenCapture.CaptureRegion(capture);
                if (renders is not null) bitmap.Save(Path.Combine(renders, $"pin-shadow-{GetDpiForWindow(pin.Handle)}-{shadow}.png"));
                Assert(bitmap.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "the shadow inspection reads its own white backdrop");
                var center = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                var expected = Color.FromArgb((int)Math.Round(255 + (Color.SlateBlue.R - 255) * pin.Opacity),
                    (int)Math.Round(255 + (Color.SlateBlue.G - 255) * pin.Opacity), (int)Math.Round(255 + (Color.SlateBlue.B - 255) * pin.Opacity));
                Assert(Math.Abs(center.R - expected.R) <= 2 && Math.Abs(center.G - expected.G) <= 2 && Math.Abs(center.B - expected.B) <= 2,
                    $"the image remains visible with shadow={shadow}, monitor={screen.DeviceName}, window={pin.Bounds}, capture={capture}, origin={pin.PointToScreen(Point.Empty)}");
                return bitmap;
            }
            using var off = ReadSurface(false);
            using var on = ReadSurface(true);
            int shaded = 0;
            var shades = new HashSet<int>();
            for (int y = 0; y < on.Height; y++)
            for (int x = 0; x < on.Width; x++)
            {
                if (new Rectangle(48, 48, window.Width, window.Height).Contains(x, y)) continue;
                var before = off.GetPixel(x, y); var after = on.GetPixel(x, y);
                if (before.R - after.R > 2 && before.G - after.G > 2 && before.B - after.B > 2)
                {
                    shaded++;
                    shades.Add(after.R);
                }
            }
            Assert(pin.Bounds == window && pin.ImageScale == 1,
                "the shadow does not change physical image scale or window geometry");
            Assert(shaded > 50 && shades.Count >= 5,
                $"the shadow toggle produces a soft graduated shadow outside the image on {screen.DeviceName}: shaded={shaded}, shades={shades.Count}");
            var shadowForm = (Forms.Form)Field(pin, "_shadow");
            Assert(IsWidgetToolTipAbove(pin.Handle, shadowForm.Handle), "the shadow stays below its image window");
            object[] hitTest = { Forms.Message.Create(shadowForm.Handle, 0x0084, IntPtr.Zero, IntPtr.Zero) };
            Call(shadowForm, "WndProc", hitTest);
            Assert(((Forms.Message)hitTest[0]).Result == new IntPtr(-1), "the shadow passes mouse input through to the underlying window");
            Call(pin, "SetImageOpacity", .5);
            Assert((byte)Field(shadowForm, "_alpha") == 128, "opacity controls immediately fade the shadow along with Pin");
            using (var faded = ReadSurface(true)) { }
            var surface = (Bitmap)Field(shadowForm, "_surface");
            Assert(surface.GetPixel(surface.Width / 2, surface.Height / 2).A == 0,
                "the shadow has a transparent center so semitransparent images have no black backing");
            Call(pin, "SetImageOpacity", 1d);
            pin.ApplyCapturePreferences(false, true);
            GetWindowDisplayAffinity(pin.Handle, out uint pinAffinity);
            GetWindowDisplayAffinity(shadowForm.Handle, out uint shadowAffinity);
            Assert(PinChooserExcluded(pin.Handle) && PinChooserExcluded(shadowForm.Handle),
                $"both windows participate in capture exclusion, with a registered fallback for unsupported layered affinity: pin={pinAffinity}, shadow={shadowAffinity}");
            using (var excluded = ScreenCapture.CaptureRegion(capture))
            {
                Assert(excluded.GetPixel(excluded.Width / 2, excluded.Height / 2).ToArgb() == Color.White.ToArgb()
                    && excluded.GetPixel(excluded.Width / 2, 48 + window.Height + 4).ToArgb() == Color.White.ToArgb(),
                    "the actual capture pipeline excludes both the pinned image and its shadow");
            }
            SetZoomAppField(app, "_isCapturing", 1);
            CallZoomApp(app, "RefreshPinnedImageShadows");
            try
            {
                Assert(!shadowForm.Visible, "excluded shadows stay hidden throughout the live capture session");
                Thread.Sleep(80); Marshal.ThrowExceptionForHR(FlushZoomTransitionComposition());
                using (var recording = ScreenCapture.CaptureRegionForRecording(capture))
                    Assert(recording.GetPixel(recording.Width / 2, recording.Height / 2).ToArgb() == Color.White.ToArgb()
                        && recording.GetPixel(recording.Width / 2, 48 + window.Height + 4).ToArgb() == Color.White.ToArgb(),
                        "the recording capture path also leaves neither the image nor a ghost shadow in the frame");
                pin.ApplyCapturePreferences(true, true);
                CallZoomApp(app, "RefreshPinnedImageShadows");
                Assert(shadowForm.Visible, "explicitly including Pin keeps its shadow visible during recording");
                Thread.Sleep(80); Marshal.ThrowExceptionForHR(FlushZoomTransitionComposition());
                using (var recording = ScreenCapture.CaptureRegionForRecording(capture))
                    Assert(recording.GetPixel(recording.Width / 2, recording.Height / 2).ToArgb() == Color.SlateBlue.ToArgb()
                        && recording.GetPixel(recording.Width / 2, 48 + window.Height + 4).R < 255,
                        "the recording capture path includes both image and shadow when requested");
                pin.ApplyCapturePreferences(false, true);
            }
            finally { CallZoomApp(app, "ResetCapturing"); }
            Assert(shadowForm.Visible, "the shadow returns when recording releases its capture session");
            CallZoomApp(app, "BeginStandaloneToolSession");
            Assert(!shadowForm.Visible, "live standalone sampling tools also suppress excluded shadows");
            CallZoomApp(app, "EndStandaloneToolSession");
            Assert(shadowForm.Visible, "closing the sampling tool restores the shadow");
            pin.ApplyCapturePreferences(true, true);
            pin.Hide();
            Assert(!shadowForm.Visible, "hiding Pin also hides its shadow immediately");
            pin.Show(); Forms.Application.DoEvents();
            Assert(shadowForm.Visible && IsWidgetToolTipAbove(pin.Handle, shadowForm.Handle), "showing Pin restores the shadow below it");
            using var restored = ReadSurface(false);
            Assert(EqualZoomBitmaps(off, restored), "disabling the shadow removes it without changing the image or surrounding surface");
            Assert(shadowForm.IsDisposed, "turning the shadow off releases its native window and cached surface");
            pin.ApplyAppearancePreferences(true, false);
            var closingShadow = (Forms.Form)Field(pin, "_shadow");
            pin.Close();
            Assert(closingShadow.IsDisposed && !closingShadow.IsHandleCreated, "closing Pin releases its shadow without leaving a floating window behind");
        }
    }
}
