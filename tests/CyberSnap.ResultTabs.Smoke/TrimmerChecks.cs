using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.UI.Controls;

internal static partial class Program
{
    private static Action? _onNextTrimmerConfirmation;
    private static T GetField<T>(object target, string name) => (T)Field(target, name);
    private static IList TrimmerSessions(Window window) => GetField<IList>(window, "_sessions");
    private static void AwaitCondition(Func<bool> condition, string description, int timeoutMs = 30000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(description);
            Pump();
            Thread.Sleep(10);
        }
        Pump();
    }
    private static void AwaitTrimmer(Window window)
    {
        try { AwaitCondition(() => GetField<bool>(window, "_previewReady"), "Trimmer preview did not load"); }
        catch
        {
            Console.Error.WriteLine($"Preview: {Field(window, "_mediaFilePath")}, GIF={Field(window, "_isGif")}, versions={Field(window, "_gifLoadVersion")}/{Field(window, "_mp4LoadVersion")}, gif={Field(window, "_gifSequence")}, mp4={Field(window, "_mp4Sequence")}");
            Console.Error.WriteLine("Diagnostics: " + AppDiagnostics.CurrentLogPath);
            throw;
        }
    }

    private static void RunFixtureFfmpeg(string ffmpeg, params string[] arguments)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("Fixture FFmpeg timed out"); }
        if (process.ExitCode != 0) throw new InvalidOperationException(error.GetAwaiter().GetResult());
    }

    private static (double Duration, bool Audio) ProbeFixture(string ffmpeg, string path)
    {
        var info = new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-v", "error", "-show_entries", "format=duration:stream=codec_type", "-of", "json", path }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new InvalidOperationException(error);
        using var document = JsonDocument.Parse(output);
        return (double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            document.RootElement.GetProperty("streams").EnumerateArray().Any(s => s.GetProperty("codec_type").GetString() == "audio"));
    }

    private static void RespondToConfirmations(params bool[] answers)
    {
        var queue = new Queue<bool>(answers);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        int ticks = 0;
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "ThemedConfirmDialog");
            if (dialog is not null)
            {
                var callback = _onNextTrimmerConfirmation;
                _onNextTrimmerConfirmation = null;
                callback?.Invoke();
                bool answer = queue.Dequeue();
                if (queue.Count == 0) timer.Stop();
                dialog.GetType().GetField("_confirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, answer);
                dialog.DialogResult = answer;
            }
            else if (++ticks > 1500) { timer.Stop(); throw new TimeoutException("Trimmer confirmation did not appear"); }
        };
        timer.Start();
    }

    private static void CheckTrimmer(SettingsService settings, string temp, string? renderDirectory)
    {
        string ffmpeg = VideoRecorder.FindFfmpeg() ?? throw new InvalidOperationException("FFmpeg required for Trimmer integration tests");
        string red = Path.Combine(temp, "red-with-audio.mp4"), blue = Path.Combine(temp, "blue-with-audio.mp4"), gif = Path.Combine(temp, "green.gif");
        foreach (var (path, color, tone) in new[] { (red, "red", "440"), (blue, "blue", "880") })
            RunFixtureFfmpeg(ffmpeg, "-f", "lavfi", "-i", $"color=c={color}:size=320x180:rate=10", "-f", "lavfi", "-i", $"sine=frequency={tone}:sample_rate=44100",
                "-t", "2.4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "10", "-c:a", "aac", "-shortest", path);
        RunFixtureFfmpeg(ffmpeg, "-f", "lavfi", "-i", "color=c=green:size=160x120:rate=10", "-t", "2.4", "-loop", "0", gif);
        var redHash = SHA256.HashData(File.ReadAllBytes(red));
        var blueHash = SHA256.HashData(File.ReadAllBytes(blue));
        settings.Settings.InterfaceLanguage = "en";
        settings.Settings.UiScale = 1;
        settings.Settings.RecordingFps = settings.Settings.GifFps = 10;
        settings.Save();
        var watch = Stopwatch.StartNew();
        var window = VideoTrimmerWindow.ShowFile(red, settings);
        var ownedFixtures = new List<string>();
        try
        {
            AwaitTrimmer(window);
            Assert(Element<PreviewTabStrip>(window, "TrimmerTabStrip").Visibility == Visibility.Collapsed, "Trimmer hides tabs for one file");
            var redSession = Field(window, "_active");
            SetField(window, "_startTimeSeconds", 0.4);
            SetField(window, "_endTimeSeconds", 1.8);
            Element<ToggleButton>(window, "SetStartBtn").IsChecked = true;
            Element<ToggleButton>(window, "SetEndBtn").IsChecked = true;
            Call(window, "SeekToSeconds", 0.7);
            SetField(window, "_loopEnabled", false);
            SetField(window, "_preciseCut", true);
            SetField(window, "_detailedTimeDisplay", false);
            SetField(window, "_currentZoom", 4.0);
            SetField(window, "_didInitialContain", true);
            Call(window, "ApplyZoom");
            Pump();
            var viewport = Element<ScrollViewer>(window, "ZoomViewport");
            viewport.ScrollToHorizontalOffset(100);
            viewport.ScrollToVerticalOffset(35);
            Pump();
            double panX = viewport.HorizontalOffset, panY = viewport.VerticalOffset;
            var volume = Element<MediaVolumeControl>(window, "VolumeControl");
            volume.Volume = 0.25;
            volume.IsExportMuted = true;
            Call(window, "EvaluateCropState");
            var sequence = Field(window, "_mp4Sequence");
            string cacheDirectory = GetField<string>(sequence, "_framesDirectory");

            Assert(ReferenceEquals(window, VideoTrimmerWindow.ShowFile(gif, settings)), "MP4 and GIF reuse one Trimmer window");
            AwaitTrimmer(window);
            Assert(!Directory.Exists(cacheDirectory) && Field(window, "_mp4Sequence") is null, "switching releases the outgoing MP4 frames and cache");
            Assert(Element<PreviewTabStrip>(window, "TrimmerTabStrip").Visibility == Visibility.Visible, "Trimmer shows its shared tab strip for multiple files");
            Assert(GetField<double>(window, "_startTimeSeconds") == 0 && volume.Volume != 0.25, "new GIF starts with isolated trim and audio preferences");
            Call(window, "SeekToSeconds", 1.2);
            var gifSession = Field(window, "_active");
            VideoTrimmerWindow.ShowFile(red.ToUpperInvariant(), settings);
            AwaitTrimmer(window);
            Assert(TrimmerSessions(window).Count == 2 && ReferenceEquals(Field(window, "_active"), redSession), "duplicate full paths focus the existing tab without adding a tab");
            Assert(Math.Abs(GetField<double>(window, "_startTimeSeconds") - 0.4) < 0.001 && Math.Abs(GetField<double>(window, "_endTimeSeconds") - 1.8) < 0.001,
                "MP4 restores its trim range");
            Assert(Math.Abs((double)Call(window, "GetCurrentTimelineSeconds")! - 0.7) < 0.03 && !(bool)Call(window, "IsPlaying")!, "MP4 restores its playhead and pause state");
            Assert(volume.Volume == 0.25 && volume.IsExportMuted && !GetField<bool>(window, "_loopEnabled") && GetField<bool>(window, "_preciseCut"), "MP4 restores its audio, loop and precise-cut choices");
            Assert(Equals(volume.ToolTip, LocalizationService.Translate("en", "Drag to adjust volume. The speaker button mutes; muted audio is excluded from exports.")),
                "returning from a GIF restores the MP4 audio tooltip");
            Assert(GetField<double>(window, "_currentZoom") == 4 && Element<ToggleButton>(window, "SetStartBtn").IsChecked == true,
                "MP4 restores zoom and marker state");
            Assert(Math.Abs(viewport.HorizontalOffset - panX) < 1 && Math.Abs(viewport.VerticalOffset - panY) < 1
                && !GetField<bool>(window, "_detailedTimeDisplay"), "MP4 restores pan and time-display preferences");

            string saved = Path.Combine(temp, "red-trimmed.mp4");
            var export = (Task<bool>)Call(window, "ExportToFileAsync", saved)!;
            Assert(GetField<bool>(window, "_isExporting") && !Element<PreviewTabStrip>(window, "TrimmerTabStrip").IsEnabled, "export freezes the active document through replacement and reload");
            VideoTrimmerWindow.ShowFile(blue, settings);
            Call(window, "ActivateSessionAt", 1);
            Assert(TrimmerSessions(window).Count == 3 && ReferenceEquals(Field(window, "_active"), redSession), "incoming files queue as tabs without replacing the exporting session");
            // Rejected close must not cancel the in-flight export.
            RespondToConfirmations(false);
            window.Close();
            Assert(window.IsVisible && !GetField<bool>(window, "_closeAfterExport"), "canceling close-all keeps the export and all tabs usable");
            AwaitCondition(() => export.IsCompleted, "MP4 export timed out");
            Assert(export.GetAwaiter().GetResult(), "MP4 export completes against its originating session");
            AwaitTrimmer(window);
            Assert(GetField<string>(window, "_mediaFilePath") == saved && TrimmerSessions(window).Count == 3, "Save As New reloads the copy in the originating tab");
            var probe = ProbeFixture(ffmpeg, saved);
            Assert(Math.Abs(probe.Duration - 1.4) < 0.15 && !probe.Audio, "real MP4 export preserves selected duration and mute");
            Assert(SHA256.HashData(File.ReadAllBytes(red)).SequenceEqual(redHash) && SHA256.HashData(File.ReadAllBytes(blue)).SequenceEqual(blueHash), "Save As New and queued tabs preserve both original files");
            string still = Path.Combine(temp, "export-frame.png");
            RunFixtureFfmpeg(ffmpeg, "-i", saved, "-frames:v", "1", still);
            using (var bitmap = new System.Drawing.Bitmap(still))
            {
                var color = bitmap.GetPixel(80, 60);
                Assert(color.R > color.B + 100, "export contains frames from the originating red video");
            }

            Call(window, "ActivateSessionAt", 1);
            AwaitTrimmer(window);
            Assert(ReferenceEquals(Field(window, "_active"), gifSession) && Math.Abs((double)Call(window, "GetCurrentTimelineSeconds")! - 1.2) < 0.03,
                "GIF restores its independent playhead");
            SetField(window, "_startTimeSeconds", 0.2);
            SetField(window, "_endTimeSeconds", 1.5);
            Call(window, "EvaluateCropState");
            string cancelledPath = Path.Combine(temp, "cancelled.gif");
            var cancelled = (Task<bool>)Call(window, "ExportToFileAsync", cancelledPath)!;
            Call(window, "CancelExportBtn_Click", window, new RoutedEventArgs());
            AwaitCondition(() => cancelled.IsCompleted, "GIF cancellation timed out");
            Assert(!cancelled.GetAwaiter().GetResult() && !File.Exists(cancelledPath) && !Directory.GetFiles(temp, "cybersnap-trim-*").Any(),
                "canceling GIF export settles the process and removes only its staging file");
            string gifSaved = Path.Combine(temp, "green-trimmed.gif");
            var gifExport = (Task<bool>)Call(window, "ExportToFileAsync", gifSaved)!;
            AwaitCondition(() => gifExport.IsCompleted, "GIF export timed out");
            Assert(gifExport.GetAwaiter().GetResult() && File.Exists(gifSaved), "real two-pass GIF export succeeds");
            AwaitTrimmer(window);
            Assert(Math.Abs(GetField<double>(window, "_videoDurationSeconds") - 1.3) < 0.2, "GIF copy loads with the exported segment duration");

            // A destination belonging to another tab must never be overwritten.
            var conflict = (Task<bool>)Call(window, "ExportToFileAsync", blue)!;
            Assert(!conflict.GetAwaiter().GetResult() && SHA256.HashData(File.ReadAllBytes(blue)).SequenceEqual(blueHash), "export rejects destinations open in another tab");
            SetField(window, "_startTimeSeconds", 0.2);
            Call(window, "EvaluateCropState");
            RespondToConfirmations(false);
            Call(window, "CloseSessionAt", 1);
            Assert(TrimmerSessions(window).Count == 3 && ReferenceEquals(Field(window, "_active"), gifSession), "canceling a tab discard preserves its session");
            RespondToConfirmations(true);
            Call(window, "CloseSessionAt", 1);
            AwaitTrimmer(window);
            Assert(TrimmerSessions(window).Count == 2, "closing a tab affects only that file");
            Call(window, "ReopenLastSession");
            AwaitTrimmer(window);
            Assert(TrimmerSessions(window).Count == 3 && GetField<double>(window, "_startTimeSeconds") == 0.2, "reopening restores the last closed session and its trim state");

            // Rapid switches must invalidate both GIF and MP4 continuations.
            for (int index = 0; index < 12; index++) Call(window, "ActivateSessionAt", index % 3);
            Call(window, "ActivateSessionAt", 0);
            AwaitTrimmer(window);
            Assert(GetField<string>(window, "_mediaFilePath") == saved && Field(window, "_gifSequence") is null,
                "rapid mixed-format switches reject stale preview callbacks");

            Call(window, "PausePlayback");
            SetField(window, "_startTimeSeconds", 0.2);
            SetField(window, "_endTimeSeconds", 0.8);
            Call(window, "EvaluateCropState");
            string replacementArrival = Path.Combine(temp, "arrived-during-replacement.mp4");
            File.Copy(blue, replacementArrival);
            bool queuedDuringReplacement = false;
            var arrival = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            arrival.Tick += (_, _) =>
            {
                if (Field(window, "_mp4Sequence") is not null || !GetField<bool>(window, "_isExporting")) return;
                arrival.Stop();
                queuedDuringReplacement = true;
                VideoTrimmerWindow.ShowFile(replacementArrival, settings);
                Assert(ReferenceEquals(Field(window, "_active"), redSession), "arrival during player-release delay cannot change the exporting session");
            };
            arrival.Start();
            var overwrite = (Task<bool>)Call(window, "ExportToFileAsync", saved)!;
            AwaitCondition(() => overwrite.IsCompleted, "Overwrite timed out");
            arrival.Stop();
            Assert(overwrite.GetAwaiter().GetResult() && queuedDuringReplacement, "atomic overwrite keeps the tab locked after encoding completes");
            AwaitTrimmer(window);
            Assert(Math.Abs(ProbeFixture(ffmpeg, saved).Duration - 0.6) < 0.15 && !(bool)Call(window, "IsCropModified")!, "overwrite reloads only its own file with a clean trim state");

            string lateDestination = Path.Combine(temp, "destination-opened-during-replacement.mp4");
            File.Copy(blue, lateDestination);
            SetField(window, "_startTimeSeconds", 0.1);
            SetField(window, "_endTimeSeconds", 0.4);
            Call(window, "SeekToSeconds", 0.3);
            Call(window, "EvaluateCropState");
            var destinationArrival = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            destinationArrival.Tick += (_, _) =>
            {
                if (Field(window, "_mp4Sequence") is not null || !GetField<bool>(window, "_isExporting")) return;
                destinationArrival.Stop();
                RespondToConfirmations(false);
                window.Close();
                VideoTrimmerWindow.ShowFile(lateDestination, settings);
            };
            destinationArrival.Start();
            var lateConflict = (Task<bool>)Call(window, "ExportToFileAsync", lateDestination)!;
            AwaitCondition(() => lateConflict.IsCompleted, "Late destination conflict timed out");
            destinationArrival.Stop();
            Assert(!lateConflict.GetAwaiter().GetResult() && SHA256.HashData(File.ReadAllBytes(lateDestination)).SequenceEqual(blueHash),
                "a destination opened during the release delay is rechecked before file replacement");
            AwaitTrimmer(window);
            Assert(GetField<string>(window, "_mediaFilePath") == saved && GetField<double>(window, "_startTimeSeconds") == 0.1,
                "an aborted destination conflict restores the originating session and its pending trim");
            Assert(Math.Abs((double)Call(window, "GetCurrentTimelineSeconds")! - 0.3) < 0.03,
                "rejecting close during player release preserves the source playhead when export aborts");

            foreach (string tag in new[] { "first", "second" })
            {
                string owned = (string)typeof(VideoTrimmerWindow).Assembly.GetType("CyberSnap.Helpers.CaptureSavePath")!
                    .GetMethod("BuildTempRecordingPath")!.Invoke(null, new object[] { ".mp4" })!;
                ownedFixtures.Add(owned);
                File.Copy(blue, owned);
                VideoTrimmerWindow.ShowFile(owned, settings, ephemeral: true);
                AwaitTrimmer(window);
                int index = TrimmerSessions(window).Count - 1;
                Call(window, "CloseSessionAt", index);
                AwaitTrimmer(window);
                Assert(File.Exists(owned), "one closed temporary recording remains available for reopening");
                if (tag == "first")
                {
                    VideoTrimmerWindow.ShowFile(owned, settings);
                    AwaitTrimmer(window);
                    Assert(GetField<string>(window, "_mediaFilePath") == owned, "opening a recently closed temporary file transfers its existing ownership");
                }
            }
            // Retiring the last-closed temporary tab must preserve the other open recording.
            int firstOwnedIndex = TrimmerSessions(window).Cast<object>().ToList().FindIndex(s => (string)Property(s, "FilePath") == ownedFixtures[0]);
            Call(window, "CloseSessionAt", firstOwnedIndex);
            AwaitTrimmer(window);
            AwaitCondition(() => !File.Exists(ownedFixtures[1]), "Closed recording was not released");
            Assert(File.Exists(ownedFixtures[0]) && !File.Exists(ownedFixtures[1]), "temporary cleanup releases only the retired session's file");

            var dropped = Enumerable.Range(0, 7).Select(i => Path.Combine(temp, $"overflow-recording-with-a-long-file-name-{i}.gif")).ToArray();
            foreach (string path in dropped) File.Copy(gif, path);
            int beforeDrop = TrimmerSessions(window).Count;
            Call(window, "AddDroppedFiles", (object)dropped.Append(gif).ToArray());
            AwaitTrimmer(window);
            Assert(TrimmerSessions(window).Count == beforeDrop + 8, "dropping multiple files appends tabs and does not replace current documents");
            Call(window, "ActivateSessionAt", 0);
            AwaitTrimmer(window);

            foreach (string language in new[] { "en", "es" })
            foreach (double scale in new[] { 1.0, 1.4 })
            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            {
                settings.Settings.InterfaceLanguage = language;
                LocalizationService.ApplyTo(window, language);
                Theme.SetMode(mode);
                Theme.ApplyTo(Application.Current.Resources);
                window.ApplyTheme();
                UiScale.Set(scale);
                UiScale.ApplyToWindow(window, (FrameworkElement)window.Content, true);
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                Pump();
                var strip = Element<PreviewTabStrip>(window, "TrimmerTabStrip");
                var preview = Element<FrameworkElement>(window, "PreviewFrame");
                Assert(strip.TransformToAncestor(window).TransformBounds(new Rect(strip.RenderSize)).Bottom
                    <= preview.TransformToAncestor(window).TransformBounds(new Rect(preview.RenderSize)).Top + 1, "Trimmer tab strip leaves room for the preview", false);
                Assert(GetField<ScrollViewer>(strip, "_scroller").ExtentWidth > GetField<ScrollViewer>(strip, "_scroller").ViewportWidth,
                    "Trimmer tab overflow uses the shared scrolling strip", false);
                if (scale == 1 && mode == AppThemeMode.Dark) Render(window, renderDirectory, "trimmer-tabs-" + language + ".png");
            }

            int tabsBeforeClose = TrimmerSessions(window).Count;
            object previewBeforeClose = Field(window, "_mp4Sequence");
            RespondToConfirmations(true, false);
            window.Close();
            Assert(window.IsVisible && TrimmerSessions(window).Count == tabsBeforeClose && ReferenceEquals(Field(window, "_mp4Sequence"), previewBeforeClose),
                "canceling a later discard prompt preserves every tab and the active decoder");

            string promptArrival = Path.Combine(temp, "arrived-during-confirmation.mp4");
            File.Copy(blue, promptArrival);
            _onNextTrimmerConfirmation = () => VideoTrimmerWindow.ShowFile(promptArrival, settings);
            RespondToConfirmations(Enumerable.Repeat(true, 1 + TrimmerSessions(window).Cast<object>().Count(s => (bool)Property(s, "Modified"))).ToArray());
            window.Close();
            Assert(window.IsVisible && TrimmerSessions(window).Count == tabsBeforeClose + 1 && ReferenceEquals(Field(window, "_active"), redSession),
                "a file arriving during close confirmation is queued and prevents unreviewed close-all");

            // Approve closing while FFmpeg is finished but the player-release delay
            // is still pending: cancellation must prevent the atomic replacement.
            var beforeCancelledOverwrite = SHA256.HashData(File.ReadAllBytes(saved));
            SetField(window, "_startTimeSeconds", 0.1);
            SetField(window, "_endTimeSeconds", 0.4);
            Call(window, "EvaluateCropState");
            bool closeDuringRelease = false;
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            closeTimer.Tick += (_, _) =>
            {
                if (Field(window, "_mp4Sequence") is not null || !GetField<bool>(window, "_isExporting")) return;
                closeTimer.Stop();
                closeDuringRelease = true;
                int confirmations = 2 + TrimmerSessions(window).Cast<object>().Count(s => (bool)Property(s, "Modified"));
                RespondToConfirmations(Enumerable.Repeat(true, confirmations).ToArray());
                window.Close();
            };
            var closingExport = (Task<bool>)Call(window, "ExportToFileAsync", saved)!;
            closeTimer.Start();
            AwaitCondition(() => closingExport.IsCompleted && !window.IsVisible, "Approved export close did not settle");
            closeTimer.Stop();
            Assert(closeDuringRelease && !closingExport.GetAwaiter().GetResult(), "approved close waits for export cancellation before disposing the window");
            Assert(SHA256.HashData(File.ReadAllBytes(saved)).SequenceEqual(beforeCancelledOverwrite) && !Directory.GetFiles(temp, "cybersnap-trim-*").Any(),
                "canceling close during replacement preserves the original and removes the job's staging file");
            Console.WriteLine($"PASS: Trimmer mixed-format sessions, real MP4/GIF exports, cancellation, isolation, close/reopen and layout ({watch.Elapsed.TotalSeconds:0.0}s).");
        }
        finally
        {
            SetField(window, "_closeApproved", true);
            if (window.IsVisible) window.Close();
            AwaitCondition(() => !window.IsVisible, "Trimmer did not close");
            AwaitCondition(() => ownedFixtures.All(path => !File.Exists(path)), "Owned recordings were not cleaned after closing");
            foreach (string path in ownedFixtures) if (File.Exists(path)) File.Delete(path);
        }
        CheckTrimmerIsolation(settings, temp);
    }
}
