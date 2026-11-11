using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows.Media;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckRecordingBarPlacement(string temp)
    {
        var opacity = DependencyPropertyDescriptor.FromProperty(System.Windows.UIElement.OpacityProperty,
            typeof(RecordingControlBarWindow));
        var initialPlacementErrors = new List<string>();
        var observedDpis = new HashSet<uint>();
        foreach (var screen in WidgetScreens())
        foreach (double scale in new[] { UiScale.Default, UiScale.Max })
        foreach (var format in new[] { RecordingFormat.MP4, RecordingFormat.GIF })
        foreach (bool parkAbove in new[] { false, true })
        {
            UiScale.Set(scale);
            var work = WidgetStatic<Rectangle>("GetPhysicalWorkingArea", screen);
            var region = new Rectangle(work.Left + (work.Width - 520) / 2,
                parkAbove ? work.Bottom - 290 : work.Top + 260, 520, 260);
            var localRegion = new Rectangle(region.X - work.X, region.Y - work.Y, region.Width, region.Height);
            string outputPath = Path.Combine(temp, format == RecordingFormat.MP4 ? "placement-test.mp4" : "placement-test.gif");
            // Production keeps the WinForms overlay on its own message loop.
            var ownerReady = new TaskCompletionSource<RecordingForm>(TaskCreationOptions.RunContinuationsAsynchronously);
            var ownerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ownerThread = new Thread(() =>
            {
                try
                {
                    using (var overlay = new RecordingForm(null, work, 30, outputPath, format, initialSelection: localRegion))
                    {
                        SetField(overlay, "_recordRegion", localRegion);
                        _ = overlay.Handle;
                        ownerReady.SetResult(overlay);
                        Forms.Application.Run();
                    }
                    ownerExited.SetResult();
                }
                catch (Exception error)
                {
                    ownerReady.TrySetException(error);
                    ownerExited.SetException(error);
                }
            }) { IsBackground = true };
            ownerThread.SetApartmentState(ApartmentState.STA);
            ownerThread.Start();
            AwaitCondition(() => ownerReady.Task.IsCompleted, "The recording overlay did not initialize", 5000);
            var owner = ownerReady.Task.GetAwaiter().GetResult();
            var bar = RecordingControlBarWindow.Create(region, format, 30, false, owner, outputPath);
            Rectangle? firstVisible = null;
            var renderedBounds = new List<Rectangle>();
            EventHandler captureFirstVisible = (_, _) =>
            {
                if (bar.Opacity > 0 && firstVisible is null)
                    firstVisible = bar.GetScreenBounds();
            };
            EventHandler captureRenderedBounds = (_, _) =>
            {
                if (bar.IsVisible && bar.Opacity > 0)
                    renderedBounds.Add(bar.GetScreenBounds());
            };
            opacity.AddValueChanged(bar, captureFirstVisible);
            CompositionTarget.Rendering += captureRenderedBounds;
            try
            {
                bar.SetDragInProgress(false);
                Assert(bar.Opacity == 0 && !bar.IsHitTestVisible, "restoring drag visibility waits for initial placement", false);
                owner.BeginInvoke(() =>
                {
                    SetField(owner, "_controlBarWpf", bar);
                    bar.ShowSafely();
                });
                AwaitCondition(() => firstVisible is not null && renderedBounds.Count > 0, "The recording bar did not appear", 5000);
                Rectangle settled = bar.GetScreenBounds();
                uint dpi = GetDpiForWindow(bar.Hwnd);
                observedDpis.Add(dpi);
                int gap = (int)Math.Round(14 * dpi / 96.0);
                int expectedLeft = region.Left + (region.Width - settled.Width) / 2;
                int expectedTop = parkAbove ? region.Top - settled.Height - gap : region.Bottom + gap;
                string context = $"{screen.DeviceName}, DPI={dpi}, UI={scale}, {format}, above={parkAbove}";
                Console.WriteLine($"{context}: first={firstVisible}, settled={settled}, expected=({expectedLeft},{expectedTop}).");
                Assert(Forms.Screen.FromHandle(bar.Hwnd).DeviceName == screen.DeviceName,
                    "the recording bar appears on the selection monitor: " + context, false);
                if (firstVisible != settled || renderedBounds.Any(bounds => bounds != settled)
                    || Math.Abs(settled.Left - expectedLeft) > 1 || Math.Abs(settled.Top - expectedTop) > 1)
                    initialPlacementErrors.Add(context);
            }
            finally
            {
                opacity.RemoveValueChanged(bar, captureFirstVisible);
                CompositionTarget.Rendering -= captureRenderedBounds;
                bar.CloseSafely();
                if (!ownerExited.Task.IsCompleted)
                    owner.BeginInvoke(() =>
                    {
                        SetField(owner, "_controlBarWpf", null);
                        Forms.Application.ExitThread();
                    });
                AwaitCondition(() => ownerExited.Task.IsCompleted, "The recording overlay did not exit", 5000);
                ownerExited.Task.GetAwaiter().GetResult();
            }
        }

        Assert(observedDpis.Contains(144), "the tests exercised a physical monitor at 150% DPI");
        Assert(initialPlacementErrors.Count == 0, "the first visible frame is already centered and docked correctly: "
            + string.Join("; ", initialPlacementErrors));
    }
}
