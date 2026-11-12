using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
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
        var revealTimes = new List<(uint Dpi, bool Prepared, double Milliseconds)>();
        foreach (var screen in WidgetScreens())
        foreach (double scale in new[] { UiScale.Default, UiScale.Max })
        foreach (var format in new[] { RecordingFormat.MP4, RecordingFormat.GIF })
        foreach (bool parkAbove in new[] { false, true })
        foreach (bool waitWhileDragging in new[] { true, false })
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
                    using (var overlay = new RecordingForm(new Bitmap(1, 1), work, 30, outputPath, format))
                    {
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
            var dispatcher = Dispatcher.CurrentDispatcher;
            RecordingControlBarWindow? bar = null;
            Rectangle? firstVisible = null;
            long releasedAt = 0;
            TimeSpan revealTime = default;
            TimeSpan firstFrameTime = default;
            var renderedBounds = new List<Rectangle>();
            EventHandler captureFirstVisible = (_, _) =>
            {
                if (bar is { Opacity: > 0 } && firstVisible is null)
                {
                    firstVisible = bar.GetScreenBounds();
                    revealTime = Stopwatch.GetElapsedTime(releasedAt);
                }
            };
            DispatcherHookEventHandler observeBar = (_, _) =>
            {
                if (bar is not null) return;
                bar = System.Windows.Application.Current.Windows.OfType<RecordingControlBarWindow>()
                    .SingleOrDefault(window => ReferenceEquals(window.OwnerWinFormsForm, owner));
                if (bar is not null)
                {
                    opacity.AddValueChanged(bar, captureFirstVisible);
                    captureFirstVisible(bar, EventArgs.Empty);
                }
            };
            EventHandler captureRenderedBounds = (_, _) =>
            {
                if (bar is { IsVisible: true, Opacity: > 0 })
                {
                    if (renderedBounds.Count == 0)
                        firstFrameTime = Stopwatch.GetElapsedTime(releasedAt);
                    renderedBounds.Add(bar.GetScreenBounds());
                }
            };
            dispatcher.Hooks.OperationCompleted += observeBar;
            CompositionTarget.Rendering += captureRenderedBounds;
            try
            {
                // Start at the opposite corner from the release point to exercise
                // the difference between the warm-up anchor and the finished area.
                var start = new Point(localRegion.Right, localRegion.Bottom);
                var partial = new Point(localRegion.X + 180, localRegion.Y + 120);
                var end = localRegion.Location;
                if (waitWhileDragging && parkAbove && scale == UiScale.Default && format == RecordingFormat.MP4)
                {
                    RunOnOverlay(() => Mouse("OnMouseDown", start, 1));
                    WaitForPreparedBar();
                    var abandoned = bar!;
                    RunOnOverlay(() => Mouse("OnMouseUp", start, 1));
                    AwaitCondition(() => !abandoned.IsVisible, "The bar for an empty selection was not closed", 5000);
                    opacity.RemoveValueChanged(abandoned, captureFirstVisible);
                    bar = null;
                    Assert(firstVisible is null, "an empty selection never reveals its prepared bar", false);
                }

                RunOnOverlay(() =>
                {
                    Mouse("OnMouseDown", start, 1);
                    Mouse("OnMouseMove", partial, 0);
                    if (!waitWhileDragging) ReleaseSelection();
                });
                if (waitWhileDragging)
                {
                    WaitForPreparedBar();
                    RunOnOverlay(ReleaseSelection);
                }

                AwaitCondition(() => firstVisible is not null && renderedBounds.Count >= 3, "The recording bar did not appear", 7000);
                Rectangle settled = bar!.GetScreenBounds();
                uint dpi = GetDpiForWindow(bar.Hwnd);
                observedDpis.Add(dpi);
                revealTimes.Add((dpi, waitWhileDragging, firstFrameTime.TotalMilliseconds));
                int gap = (int)Math.Round(14 * dpi / 96.0);
                int expectedLeft = region.Left + (region.Width - settled.Width) / 2;
                int expectedTop = parkAbove ? region.Top - settled.Height - gap : region.Bottom + gap;
                string context = $"{screen.DeviceName}, DPI={dpi}, UI={scale}, {format}, above={parkAbove}, prepared={waitWhileDragging}";
                Console.WriteLine($"{context}: first={firstVisible}, settled={settled}, expected=({expectedLeft},{expectedTop}), "
                    + $"release to reveal={revealTime.TotalMilliseconds:F1} ms, first frame={firstFrameTime.TotalMilliseconds:F1} ms.");
                Assert(Forms.Screen.FromHandle(bar.Hwnd).DeviceName == screen.DeviceName,
                    "the recording bar appears on the selection monitor: " + context, false);
                Assert(bar.IsHitTestVisible && (GetWidgetToolTipExStyle(bar.Hwnd, -20) & 0x20) == 0,
                    "the revealed bar receives mouse input", false);
                if (firstVisible != settled || renderedBounds.Any(bounds => bounds != settled)
                    || Math.Abs(settled.Left - expectedLeft) > 1 || Math.Abs(settled.Top - expectedTop) > 1)
                    initialPlacementErrors.Add(context);

                void ReleaseSelection()
                {
                    releasedAt = Stopwatch.GetTimestamp();
                    Mouse("OnMouseUp", end, 1);
                }

                void Mouse(string handler, Point point, int clicks) =>
                    Call(owner, handler, new Forms.MouseEventArgs(Forms.MouseButtons.Left, clicks, point.X, point.Y, 0));

                void WaitForPreparedBar()
                {
                    AwaitCondition(() => bar is { IsLoaded: true } && !bar.GetScreenBounds().IsEmpty,
                        "The recording bar did not prepare during the drag", 7000);
                    Assert(bar!.Opacity == 0 && !bar.IsHitTestVisible && firstVisible is null
                        && (GetWidgetToolTipExStyle(bar.Hwnd, -20) & 0x20) != 0,
                        "the prepared recording bar remains invisible and ignores native input during selection", false);
                }
            }
            finally
            {
                dispatcher.Hooks.OperationCompleted -= observeBar;
                if (bar is not null) opacity.RemoveValueChanged(bar, captureFirstVisible);
                CompositionTarget.Rendering -= captureRenderedBounds;
                if (!ownerExited.Task.IsCompleted)
                    owner.BeginInvoke(() =>
                    {
                        Call(owner, "CloseControlBar");
                        Forms.Application.ExitThread();
                    });
                AwaitCondition(() => ownerExited.Task.IsCompleted, "The recording overlay did not exit", 5000);
                ownerExited.Task.GetAwaiter().GetResult();
                AwaitCondition(() => bar is null || !bar.IsVisible, "The recording bar outlived its overlay", 5000);
            }

            void RunOnOverlay(Action action)
            {
                var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                owner.BeginInvoke(() =>
                {
                    try { action(); done.SetResult(); }
                    catch (Exception error) { done.SetException(error); }
                });
                AwaitCondition(() => done.Task.IsCompleted, "The overlay did not handle the selection input", 5000);
                done.Task.GetAwaiter().GetResult();
            }
        }

        Assert(observedDpis.Contains(144), "the tests exercised a physical monitor at 150% DPI");
        Assert(initialPlacementErrors.Count == 0, "the first visible frame is already centered and docked correctly: "
            + string.Join("; ", initialPlacementErrors));
        foreach (var group in revealTimes.GroupBy(sample => (sample.Dpi, sample.Prepared)))
        {
            var ordered = group.Select(sample => sample.Milliseconds).Order().ToArray();
            double median = (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
            Console.WriteLine($"DPI={group.Key.Dpi}, prepared={group.Key.Prepared}: median={median:F1} ms, max={ordered[^1]:F1} ms.");
        }
    }
}
