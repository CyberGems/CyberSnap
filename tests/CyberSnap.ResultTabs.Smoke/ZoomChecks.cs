using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using Forms = System.Windows.Forms;
using DrawingPoint = System.Drawing.Point;
using DrawingSize = System.Drawing.Size;

internal static partial class Program
{
    private static void CheckZoom(string temp, string? renderDirectory)
    {
        var size = new DrawingSize(800, 600);
        Assert(ZoomViewport.NormalizeZoom(double.NaN) == 2 && ZoomViewport.NormalizeZoom(double.PositiveInfinity) == 2
            && ZoomViewport.NormalizeZoom(0) == 1.25 && ZoomViewport.NormalizeZoom(10) == 4,
            "invalid and out-of-range zoom settings are normalized");
        foreach (var point in new[] { DrawingPoint.Empty, new DrawingPoint(799, 599), new DrawingPoint(400, 300), new DrawingPoint(-200, 900) })
        foreach (double zoom in new[] { 1.25, 2, 4 })
        {
            var viewport = new ZoomViewport(size, point, zoom, false);
            var bounds = viewport.SourceBounds;
            Assert(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= size.Width + 0.001
                && bounds.Bottom <= size.Height + 0.001 && Math.Abs(bounds.Width * zoom - size.Width) < 0.001,
                $"source geometry stays within the monitor at {point}, {zoom}x", false);
        }
        var bottomRight = new ZoomViewport(size, new DrawingPoint(799, 599), 4, false);
        Assert(bottomRight.SourceBounds == new RectangleF(600, 450, 200, 150), "all monitor edges remain reachable at maximum zoom");
        var animated = new ZoomViewport(size, DrawingPoint.Empty, 2, true);
        Assert(animated.Zoom == 1, "animated entry starts with the unscaled frozen frame");
        animated.SetPointer(new DrawingPoint(799, 599));
        animated.Advance(0.016, true);
        Assert(animated.Zoom > 1 && animated.Zoom < 2 && animated.SourceBounds.Left > 0,
            "zoom and pointer panning ease towards their targets");
        var frozen = animated.SourceBounds;
        animated.BeginSelection();
        animated.SetPointer(DrawingPoint.Empty);
        animated.ChangeZoom(5, false);
        animated.Advance(1, true);
        Assert(animated.SourceBounds == frozen, "region mode locks the exact visible transform during pointer and wheel input");
        Assert(animated.Selection(new DrawingPoint(700, 500), new DrawingPoint(-20, -30)) == new Rectangle(0, 0, 700, 500),
            "reverse region drags clip to the monitor bounds");
        animated.EndSelection();
        animated.ChangeZoom(20, false);
        Assert(animated.Zoom == 4, "wheel and keyboard zoom stop at 4x");
        animated.ChangeZoom(-20, false);
        Assert(animated.Zoom == 1.25, "wheel and keyboard zoom stop at 1.25x");

        using var source = new Bitmap(800, 600, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.Clear(Color.FromArgb(19, 23, 36));
            using var accent = new SolidBrush(Color.FromArgb(0, 220, 230));
            using var foreground = new SolidBrush(Color.White);
            using var font = new Font("Segoe UI", 12);
            for (int y = 0; y < 600; y += 40)
            for (int x = 0; x < 800; x += 40)
            {
                using var fill = new SolidBrush(Color.FromArgb(40 + x / 5, 40 + y / 4, (x + y) % 190 + 30));
                graphics.FillRectangle(fill, x + 1, y + 1, 38, 38);
            }
            graphics.FillRectangle(accent, 300, 210, 200, 110);
            graphics.DrawString("CyberSnap Zoom", font, foreground, 310, 245);
        }

        foreach (bool smooth in new[] { false, true })
        {
            using var form = NewZoomForm(source, new AppSettings { ZoomAnimate = false, ZoomSmoothImage = smooth });
            _ = form.Handle;
            var viewport = (ZoomViewport)Field(form, "_viewport");
            using var expected = form.RenderView();
            using var displayed = form.RenderView(includeChrome: true);
            Assert(!(bool)Field(form, "_rendererUnavailable"), "the Direct2D rendering and readback path is available");
            if (!smooth)
            {
                Assert(ZoomNearestMatchesGeometry(source, expected, viewport.SourceBounds),
                    "GPU nearest-neighbor sampling matches physical pixel centers, including hardware subpixel ties");
            }
            Assert(displayed.GetPixel(400, 260).ToArgb() == expected.GetPixel(400, 260).ToArgb(),
                "the display samples the same original frame as capture output, smooth=" + smooth);
            Bitmap? captured = null;
            form.FormClosed += (_, _) => captured = form.TakeResult();
            ZoomKey(form, Forms.Keys.Enter);
            using (captured)
            {
                Assert(captured is { Width: 800, Height: 600 }, "Enter transfers ownership of a full-resolution magnified viewport");
                Assert(EqualZoomBitmaps(expected, captured!), "full capture matches the view exactly and excludes the HUD");
            }

            using var regionForm = NewZoomForm(source, new AppSettings { ZoomAnimate = false, ZoomSmoothImage = smooth });
            _ = regionForm.Handle;
            ZoomKey(regionForm, Forms.Keys.C);
            ZoomMouse(regionForm, "OnMouseDown", Forms.MouseButtons.Left, 570, 410);
            ZoomMouse(regionForm, "OnMouseMove", Forms.MouseButtons.Left, 220, 130);
            Bitmap? regionCapture = null;
            regionForm.FormClosed += (_, _) => regionCapture = regionForm.TakeResult();
            ZoomMouse(regionForm, "OnMouseUp", Forms.MouseButtons.Left, 220, 130);
            using (regionCapture)
            using (var crop = expected.Clone(new Rectangle(220, 130, 350, 280), PixelFormat.Format32bppArgb))
                Assert(regionCapture is not null && EqualZoomBitmaps(crop, regionCapture),
                    "a reverse drag captures precisely the locked viewport region without selection chrome, smooth=" + smooth);
        }

        foreach (var action in new[] { Forms.Keys.Escape, Forms.Keys.Control | Forms.Keys.C, Forms.Keys.Control | Forms.Keys.S })
        {
            using var form = NewZoomForm(source, new AppSettings { ZoomAnimate = false });
            _ = form.Handle;
            Bitmap? result = null;
            form.FormClosed += (_, _) => result = form.TakeResult();
            ZoomKey(form, action);
            using (result)
                Assert(action == Forms.Keys.Escape ? result is null : result is not null && form.CommitAction ==
                    ((action & Forms.Keys.KeyCode) == Forms.Keys.C ? RegionOverlayForm.ConfirmCommitAction.Copy : RegionOverlayForm.ConfirmCommitAction.Save),
                    "exit and explicit copy/save retain their output contracts: " + action);
        }
        using (var form = NewZoomForm(source, new AppSettings { ZoomAnimate = false }))
        {
            _ = form.Handle;
            ZoomKey(form, Forms.Keys.C);
            ZoomMouse(form, "OnMouseDown", Forms.MouseButtons.Left, 50, 50);
            ZoomMouse(form, "OnMouseUp", Forms.MouseButtons.Left, 50, 50);
            Assert(form.Result is null && !form.IsDisposed, "an empty region keeps selection open without producing a fake capture");
            ZoomKey(form, Forms.Keys.C);
            Assert(!((ZoomViewport)Field(form, "_viewport")).IsSelecting, "C returns from region selection to panning");
            ZoomMouse(form, "OnMouseDown", Forms.MouseButtons.Right, 50, 50);
            Assert(form.IsDisposed && form.Result is null, "right-click exits without capturing");
        }

        using (var form = NewZoomForm(source, new AppSettings { ZoomAnimate = false }))
        {
            _ = form.Handle;
            SetField(form, "_rendererUnavailable", true);
            var viewport = (ZoomViewport)Field(form, "_viewport");
            using var reference = viewport.Render(source, true);
            ZoomKey(form, Forms.Keys.C);
            using var frozenView = form.RenderView();
            Assert(EqualZoomBitmaps(reference, frozenView), "systems without Direct2D preserve the locked capture using the CPU renderer");
        }

        using (var largeSource = new Bitmap(3840, 2160))
        {
            using (var graphics = Graphics.FromImage(largeSource)) graphics.Clear(Color.CornflowerBlue);
            using var form = NewZoomForm(largeSource, new AppSettings { ZoomAnimate = false });
            using var warmup = form.RenderView();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int index = 0; index < 5; index++)
            {
                using var frame = form.RenderView();
                Assert(frame.Size == largeSource.Size && frame.GetPixel(2000, 1000).ToArgb() == Color.CornflowerBlue.ToArgb(),
                    "4K rendering and readback retain opaque source pixels", false);
            }
            Console.WriteLine($"4K Direct2D rendering including GPU readback: {clock.Elapsed.TotalMilliseconds / 5:0.0} ms/frame.");
            var renderer = Field(form, "_renderer");
            var viewport = (ZoomViewport)Field(form, "_viewport");
            clock.Restart();
            for (int index = 0; index < 30; index++)
            {
                viewport.SetPointer(new DrawingPoint(100 + index * 60, 500));
                viewport.Advance(0.016, false);
                Call(renderer, "Paint", viewport, true, null!);
            }
            Console.WriteLine($"4K Direct2D pan drawing without readback: {clock.Elapsed.TotalMilliseconds / 30:0.0} ms/frame.");
        }

        string settingsPath = Path.Combine(temp, "zoom-settings.json");
        using (var settings = new SettingsService(settingsPath))
        {
            settings.Settings.ZoomInitialLevel = 2.75;
            settings.Settings.ZoomSmoothImage = false;
            settings.Settings.ZoomAnimate = false;
            settings.Settings.SetToolHotkey("_standaloneZoom", 6, 0x5A);
            settings.Save(); settings.FlushPendingWrites();
        }
        using (var settings = new SettingsService(settingsPath))
        {
            settings.Load();
            Assert(settings.Settings.ZoomInitialLevel == 2.75 && !settings.Settings.ZoomSmoothImage && !settings.Settings.ZoomAnimate
                && settings.Settings.GetToolHotkey("_standaloneZoom") == (6u, 0x5Au), "Zoom preferences and its global hotkey survive a reload");
            settings.Settings.ResetToDefaultHotkeys();
            Assert(settings.Settings.GetToolHotkey("_standaloneZoom") == (0u, 0u), "resetting hotkeys clears Zoom without introducing a global conflict");
        }
        using (var hotkeys = new HotkeyService())
        {
            int calls = 0;
            hotkeys.StandaloneZoomHotkeyPressed += () => calls++;
            Assert(hotkeys.RegisterStandaloneZoom(0, 0), "an unassigned Zoom hotkey installs no global key binding");
            var message = new System.Windows.Interop.MSG { message = 0x0312, wParam = new IntPtr(9017) };
            object[] arguments = { message, false };
            typeof(HotkeyService).GetMethod("OnMsg", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(hotkeys, arguments);
            Assert(calls == 1 && (bool)arguments[1], "the registered native Zoom message dispatches exactly once");
            hotkeys.Unregister();
            Assert(!(bool)Field(hotkeys, "_registered") && !(bool)Field(hotkeys, "_standaloneZoomRegistered"),
                "unregistering hotkeys releases the Zoom message hook and binding");
        }
        if (renderDirectory is not null)
        {
            Directory.CreateDirectory(renderDirectory);
            foreach (string language in new[] { "en", "es" })
            {
                LocalizationService.ApplyCurrentCulture(language);
                using var form = NewZoomForm(source, new AppSettings { ZoomAnimate = false });
                _ = form.Handle;
                using var preview = form.RenderView(includeChrome: true);
                preview.Save(Path.Combine(renderDirectory, "zoom-view-" + language + ".png"));
                ZoomKey(form, Forms.Keys.C);
                ZoomMouse(form, "OnMouseDown", Forms.MouseButtons.Left, 240, 170);
                ZoomMouse(form, "OnMouseMove", Forms.MouseButtons.Left, 600, 360);
                using var selectionPreview = form.RenderView(includeChrome: true);
                selectionPreview.Save(Path.Combine(renderDirectory, "zoom-region-" + language + ".png"));
            }
        }
    }

    private static StandaloneZoomForm NewZoomForm(Bitmap source, AppSettings options) => new((Bitmap)source.Clone(),
        new Rectangle(-1000, 0, source.Width, source.Height), new DrawingPoint(-600, 300), options);

    private static void ZoomKey(StandaloneZoomForm form, Forms.Keys key) => Call(form, "OnKeyDown", new Forms.KeyEventArgs(key));
    private static void ZoomMouse(StandaloneZoomForm form, string method, Forms.MouseButtons buttons, int x, int y)
        => Call(form, method, new Forms.MouseEventArgs(buttons, 1, x, y, 0));

    private static bool EqualZoomBitmaps(Bitmap expected, Bitmap actual)
    {
        if (expected.Size != actual.Size) return false;
        for (int y = 0; y < actual.Height; y++)
        for (int x = 0; x < actual.Width; x++)
            if (expected.GetPixel(x, y).ToArgb() != actual.GetPixel(x, y).ToArgb()) return false;
        return true;
    }

    private static bool ZoomNearestMatchesGeometry(Bitmap source, Bitmap actual, RectangleF bounds)
    {
        // GPUs quantize texture coordinates to subpixel precision. Near an exact texel boundary,
        // either adjacent texel can win; verify the original texels, rather than a different GDI+ sampler.
        const double precision = 1d / 256;
        for (int y = 0; y < actual.Height; y++)
        for (int x = 0; x < actual.Width; x++)
        {
            double sx = bounds.X + (x + 0.5) * bounds.Width / actual.Width;
            double sy = bounds.Y + (y + 0.5) * bounds.Height / actual.Height;
            int x1 = Math.Clamp((int)(sx - precision), 0, source.Width - 1);
            int x2 = Math.Clamp((int)(sx + precision), 0, source.Width - 1);
            int y1 = Math.Clamp((int)(sy - precision), 0, source.Height - 1);
            int y2 = Math.Clamp((int)(sy + precision), 0, source.Height - 1);
            int color = actual.GetPixel(x, y).ToArgb();
            if (color != source.GetPixel(x1, y1).ToArgb() && color != source.GetPixel(x1, y2).ToArgb()
                && color != source.GetPixel(x2, y1).ToArgb() && color != source.GetPixel(x2, y2).ToArgb()) return false;
        }
        return true;
    }
}
