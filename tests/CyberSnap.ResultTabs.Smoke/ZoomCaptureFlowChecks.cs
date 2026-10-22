using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    // WPF queues OnStartup on the first dispatcher pump, even without Application.Run.
    // Keep real capture handlers, but isolate startup/IPC, installation, timers and the user profile.
    private sealed class ZoomFlowTestApp : CyberSnap.App
    {
        protected override void OnStartup(System.Windows.StartupEventArgs e) { }
        protected override void OnExit(System.Windows.ExitEventArgs e) { }
    }

    private static object? ZoomAppField(object app, string name) => typeof(CyberSnap.App)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(app);
    private static void SetZoomAppField(object app, string name, object? value) => typeof(CyberSnap.App)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, value);
    private static object? CallZoomApp(object app, string name, params object[] args) => typeof(CyberSnap.App)
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app, args);

    // Exercise the same borrowed-frame modal entry point used by App.LaunchOverlayNow.
    // The fixture collects output in memory, without clipboard, files, Gallery, or Preview side effects.
    private static Bitmap? ExerciseZoomCapture(StandaloneZoomForm zoom, bool fullView,
        Action<RegionOverlayForm> interact, CaptureMode selectionMode = CaptureMode.Rectangle)
    {
        Bitmap? output = null;
        Exception? failure = null;
        void Capture(ZoomCaptureRequest request)
        {
            Assert(request.FullView == fullView && request.Frame.Size == new Size(800, 600)
                && request.Bounds == zoom.Bounds && zoom.CapturePending && !zoom.Visible,
                "capture borrows the full clean viewport while Zoom is paused and hidden", false);
            using var overlay = new RegionOverlayForm(request.Frame, request.Bounds, request.SelectionMode,
                WindowDetectionMode.Off) { DetectWindows = false, ConfirmRegionBeforeCapture = true };
            overlay.SetEnabledTools(null);
            overlay.RegionSelected += rect =>
            {
                using var annotated = overlay.RenderAnnotatedBitmap();
                output = ScreenCapture.CropRegion(annotated, rect);
                request.Commit();
                overlay.Close();
            };
            overlay.Shown += (_, _) => overlay.BeginInvoke((Action)(() => overlay.BeginInvoke((Action)(() =>
            {
                try { interact(overlay); }
                catch (Exception ex) { failure = ex; Call(overlay, "CancelFromShortcut"); }
            }))));
            using var timeout = new Forms.Timer { Interval = 10000 };
            timeout.Tick += (_, _) =>
            {
                failure = new TimeoutException("Zoom modal capture did not finish.");
                Call(overlay, "CancelFromShortcut");
            };
            timeout.Start();
            Call(request, "ShowDialog", overlay);
            Assert(request.IsCommitted == (output is not null), "cancel and commit have distinct output ownership", false);
        }
        zoom.CaptureRequested += Capture;
        try
        {
            if (selectionMode == CaptureMode.Rectangle) ZoomKey(zoom, fullView ? Forms.Keys.Enter : Forms.Keys.C);
            else zoom.BeginCapture(fullView, selectionMode);
        }
        finally { zoom.CaptureRequested -= Capture; }
        if (failure is not null) { output?.Dispose(); throw failure; }
        return output;
    }

    private static void CommitZoomOverlay(RegionOverlayForm overlay) => overlay.GetType()
        .GetMethod("CommitConfirmedSelection", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)!
        .Invoke(overlay, null);

    private static void OverlayZoomMouse(RegionOverlayForm overlay, string method, int x, int y)
        => Call(overlay, method, new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, x, y, 0));

    private static void CheckZoomConfirmationFlow(Bitmap source, CyberSnap.App app, string? renderDirectory)
    {
        using (var zoom = NewZoomForm(source, new AppSettings { ZoomAnimate = false }))
        {
            using var original = zoom.RenderView();
            var viewport = (ZoomViewport)Field(zoom, "_viewport");
            var bounds = viewport.SourceBounds;
            foreach (bool full in new[] { false, true, false })
            {
                using var output = ExerciseZoomCapture(zoom, full, overlay =>
                {
                    if (!full)
                    {
                        OverlayZoomMouse(overlay, "OnMouseDown", 200, 180);
                        OverlayZoomMouse(overlay, "OnMouseUp", 200, 180);
                        Assert((bool)Field(overlay, "_isConfirmingSelection") && overlay.ConfirmRect == new Rectangle(0, 0, 800, 600),
                            "a click selects the full frame for confirmation without bypassing it");
                    }
                    Call(overlay, "HandleEscapeKey");
                });
                Assert(output is null && !zoom.IsDisposed && zoom.Visible && !zoom.CaptureOutputDelegated
                    && !zoom.CapturePending, "canceling selection or full confirmation resumes the same Zoom session: output=" + (output is not null) + ", disposed=" + zoom.IsDisposed + ", visible=" + zoom.Visible + ", delegated=" + zoom.CaptureOutputDelegated + ", pending=" + zoom.CapturePending + ", closing=" + Field(zoom,"_closing"));
                viewport.Advance(0.1, true);
                using var resumed = zoom.RenderView();
                Assert(viewport.SourceBounds == bounds && EqualZoomBitmaps(original, resumed),
                    "cancel preserves both the exact magnification and framing");
            }
            ZoomMouse(zoom, "OnMouseDown", Forms.MouseButtons.Right, 50, 50);
            Assert(zoom.IsDisposed && zoom.Result is null, "right-click still exits Zoom without output");
        }

        using (var zoom = NewZoomForm(source, new AppSettings { ZoomAnimate = false }))
        using (var output = ExerciseZoomCapture(zoom, false, overlay =>
        {
            OverlayZoomMouse(overlay, "OnMouseDown", 180, 100);
            OverlayZoomMouse(overlay, "OnMouseMove", 650, 450);
            OverlayZoomMouse(overlay, "OnMouseUp", 650, 450);
            // Use the standard bottom-right resize handle, then draw with the normal annotation tool.
            OverlayZoomMouse(overlay, "OnMouseDown", 650, 450);
            OverlayZoomMouse(overlay, "OnMouseMove", 620, 420);
            OverlayZoomMouse(overlay, "OnMouseUp", 620, 420);
            Assert(overlay.ConfirmRect == new Rectangle(180, 100, 440, 320), "the usual confirmation handles refine a Zoom crop");
            Call(overlay, "SetMode", CaptureMode.Draw, null!, false);
            overlay.SetToolColor(Color.Magenta);
            overlay.StrokeWidth = 8;
            OverlayZoomMouse(overlay, "OnMouseDown", 300, 220);
            OverlayZoomMouse(overlay, "OnMouseMove", 450, 220);
            OverlayZoomMouse(overlay, "OnMouseUp", 450, 220);
            Assert(((List<Annotation>)Field(overlay, "_undoStack")).Count == 1,
                "the standard annotation tool edits the magnified capture before confirmation");
            if (renderDirectory is not null)
            {
                using var image = new Bitmap(800, 600);
                overlay.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                using (var graphics = Graphics.FromImage(image)) overlay.PaintToolbarTo(graphics);
                image.Save(System.IO.Path.Combine(renderDirectory, "zoom-confirm-annotated.png"));
            }
            CommitZoomOverlay(overlay);
        }))
        {
            Assert(output is { Width: 440, Height: 320 } && output.GetPixel(160, 120).ToArgb() == Color.Magenta.ToArgb(),
                "confirmed output includes the refined crop and annotations, without selection chrome");
        }

        using (var zoom = NewZoomForm(source, new AppSettings { ZoomAnimate = false }))
        {
            using var output = ExerciseZoomCapture(zoom, true, overlay =>
            {
                ((List<Annotation>)Field(overlay, "_undoStack")).Add(new LineAnnotation(new Point(300, 200), new Point(450, 200), Color.Cyan));
                RespondToConfirmation(false);
                Call(overlay, "HandleEscapeKey");
                Assert(overlay.Visible && zoom.CapturePending && (bool)Field(overlay, "_isConfirmingSelection"),
                    "declining to discard annotations keeps confirmation open");
                RespondToConfirmation(true);
                Call(overlay, "HandleEscapeKey");
            });
            Assert(output is null && zoom.Visible && !zoom.IsDisposed,
                "accepting annotation discard returns to Zoom without dispatching output");
            zoom.Close();
        }

        foreach (var mode in new[] { CaptureMode.Rectangle, CaptureMode.Center })
            CheckZoomCaptureHotkey(source, app, mode);
    }

    private static void CheckZoomCaptureHotkey(Bitmap source, CyberSnap.App app, CaptureMode mode)
    {
        var settings = (SettingsService)ZoomAppField(app, "_settingsService")!;
        settings.Settings.ShowCapturePreview = false;
        settings.Settings.DefaultCaptureMode = mode;
        using var zoom = NewZoomForm(source, new AppSettings { ZoomAnimate = false });
        SetZoomAppField(app, "_activeZoomForm", zoom);
        SetZoomAppField(app, "_isZooming", 1);
        SetZoomAppField(app, "_isCapturing", 1);
        int requests = 0;
        Exception? failure = null;
        zoom.CaptureRequested += request =>
        {
            requests++;
            CallZoomApp(app, "LaunchOverlayNow", request.SelectionMode, request);
        };
        zoom.Shown += (_, _) => zoom.BeginInvoke((Action)(() => CallZoomApp(app, "OnHotkeyPressed")));
        using var tick = new Forms.Timer { Interval = 30 };
        var deadline = DateTime.UtcNow.AddSeconds(10);
        tick.Tick += (_, _) =>
        {
            try
            {
                var overlay = typeof(RegionOverlayForm).GetField("_currentOverlay", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as RegionOverlayForm;
                if (overlay is { Visible: true } && zoom.CapturePending)
                {
                    Assert(!overlay.DetectWindows && !zoom.Visible && overlay.CurrentMode == mode,
                        "the actual App hotkey opens standard selection on the borrowed frame with live detection disabled: " + mode);
                    Call(overlay, "HandleEscapeKey");
                }
                else if (requests == 1 && !zoom.CapturePending)
                {
                    Assert(zoom.Visible && (int)ZoomAppField(app, "_isCapturing")! == 1 && (int)ZoomAppField(app, "_isZooming")! == 1
                        && ZoomAppField(app, "_activePreviewDialog") is null,
                        "App cancellation keeps the Zoom capture slot and outer message loop alive with Preview disabled");
                    tick.Stop(); zoom.Close();
                }
                else if (DateTime.UtcNow > deadline) throw new TimeoutException("The capture hotkey did not return to Zoom.");
            }
            catch (Exception ex) { failure = ex; tick.Stop(); zoom.Close(); }
        };
        tick.Start();
        try { Forms.Application.Run(zoom); }
        finally
        {
            SetZoomAppField(app, "_activeZoomForm", null);
            SetZoomAppField(app, "_isZooming", 0);
            SetZoomAppField(app, "_isCapturing", 0);
        }
        if (failure is not null) throw failure;
        Assert(requests == 1, "one normal capture hotkey creates exactly one Zoom capture request");
    }
}
