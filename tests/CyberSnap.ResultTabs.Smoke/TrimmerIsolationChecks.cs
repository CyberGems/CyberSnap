using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyberSnap.Capture;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.UI.Controls;

internal static partial class Program
{
    private static void SetTrimMarkers(Window window, double start, double end)
    {
        Call(window, "SeekToSeconds", start);
        var startButton = Element<ToggleButton>(window, "SetStartBtn");
        startButton.IsChecked = true;
        Call(window, "SetStartBtn_Click", startButton, new RoutedEventArgs());
        Call(window, "SeekToSeconds", end);
        var endButton = Element<ToggleButton>(window, "SetEndBtn");
        endButton.IsChecked = true;
        Call(window, "SetEndBtn_Click", endButton, new RoutedEventArgs());
    }

    private static void EditTrimTime(Window window, bool start, string text)
    {
        var input = Element<TextBox>(window, start ? "StartTimeInput" : "EndTimeInput");
        var display = Element<TextBlock>(window, start ? "StartPointText" : "EndPointText");
        Call(window, "ActivateTimeInput", input, display, display.Text);
        input.Text = text;
    }

    private static void AssertTrimRange(Window window, double start, double end, string description)
    {
        Assert(Math.Abs(GetField<double>(window, "_startTimeSeconds") - start) < 0.001
            && Math.Abs(GetField<double>(window, "_endTimeSeconds") - end) < 0.001
            && Element<TextBlock>(window, "StartPointText").Text == (string)Call(window, "FormatTime", start)!
            && Element<TextBlock>(window, "EndPointText").Text == (string)Call(window, "FormatTime", end)!
            && Element<ToggleButton>(window, "SetStartBtn").IsChecked == true
            && Element<ToggleButton>(window, "SetEndBtn").IsChecked == true
            && Math.Abs(Element<ColumnDefinition>(window, "ColKeep").Width.Value - (end - start)) < 0.001,
            description);
    }

    private static void AssertPreviewColor(Window window, int width, Func<System.Drawing.Color, bool> matches, string description)
    {
        var source = (BitmapSource)Element<System.Windows.Controls.Image>(window, "GifPreviewImage").Source;
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(source.PixelWidth / 2, source.PixelHeight / 2, 1, 1), pixel, 4, 0);
        Assert(source.PixelWidth == width && matches(System.Drawing.Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0])), description);
    }

    private static void AssertFixtureColor(string ffmpeg, string path, string still, Func<System.Drawing.Color, bool> matches, string description)
    {
        RunFixtureFfmpeg(ffmpeg, "-ss", "0.2", "-i", path, "-frames:v", "1", still);
        using var bitmap = new System.Drawing.Bitmap(still);
        Assert(matches(bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2)), description);
    }

    private static double FixtureMeanVolume(string ffmpeg, string path)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (string arg in new[] { "-hide_banner", "-i", path, "-map", "0:a:0", "-af", "volumedetect", "-f", "null", "-" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new TimeoutException("Audio volume probe timed out"); }
        string log = error.GetAwaiter().GetResult();
        output.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException(log);
        var match = Regex.Match(log, @"mean_volume:\s*(-?[\d.]+) dB");
        if (!match.Success) throw new InvalidOperationException("FFmpeg did not report the audio volume: " + log);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static void CheckTrimmerIsolation(SettingsService settings, string temp)
    {
        string ffmpeg = VideoRecorder.FindFfmpeg()!;
        string first = Path.Combine(temp, "timeline-first.mp4"), second = Path.Combine(temp, "timeline-second.mp4"), silent = Path.Combine(temp, "silent.mp4");
        // Each timeline has three distinct time regions. Checking the exported
        // color detects both the wrong source and a lost start offset.
        foreach (var (path, colors, size, rate, segment, tone) in new[]
        {
            (first, new[] { "red", "green", "blue" }, "320x180", 10, 1.0, 440),
            (second, new[] { "magenta", "yellow", "cyan" }, "640x360", 20, 1.5, 880)
        })
        {
            string video = string.Join(";", colors.Select((color, i) => $"color=c={color}:size={size}:rate={rate}:d={segment.ToString(CultureInfo.InvariantCulture)}[v{i}]"))
                + ";[v0][v1][v2]concat=n=3:v=1:a=0";
            RunFixtureFfmpeg(ffmpeg, "-f", "lavfi", "-i", video, "-f", "lavfi", "-i", $"sine=frequency={tone}:sample_rate=44100",
                "-t", (segment * 3).ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-pix_fmt", "yuv420p", "-g", "10", "-c:a", "aac", "-shortest", path);
        }
        RunFixtureFfmpeg(ffmpeg, "-f", "lavfi", "-i", "color=c=white:size=160x120:rate=10", "-t", "1.6", "-c:v", "libx264", "-pix_fmt", "yuv420p", silent);
        var hashes = new[] { first, second, silent }.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(path)));
        settings.Settings.InterfaceLanguage = "en";
        settings.Settings.UiScale = 1;
        settings.Settings.VideoTrimmerPreciseCut = false;
        settings.Settings.VideoTrimmerLoopEnabled = true;
        settings.Settings.VideoTrimmerDetailedTimeDisplay = true;
        settings.Settings.VideoTrimmerVolume = 1;
        settings.Settings.VideoTrimmerExportMuted = false;
        settings.Save();
        var window = VideoTrimmerWindow.ShowFile(first, settings);
        try
        {
            AwaitTrimmer(window);
            var firstSession = Field(window, "_active");
            var volume = Element<MediaVolumeControl>(window, "VolumeControl");
            var viewport = Element<ScrollViewer>(window, "ZoomViewport");
            SetTrimMarkers(window, 1.1, 1.9);
            volume.Volume = 0.25;
            volume.IsExportMuted = true;
            var precise = Element<ToggleButton>(window, "PreciseCutToggleBtn");
            precise.IsChecked = true;
            Call(window, "PreciseCutToggleBtn_Click", precise, new RoutedEventArgs());
            var loop = Element<ToggleButton>(window, "LoopToggleBtn");
            loop.IsChecked = false;
            Call(window, "LoopToggleBtn_Click", loop, new RoutedEventArgs());
            Call(window, "PlaybackTimeBtn_Click", window, new RoutedEventArgs());
            Call(window, "ZoomToFixedPos", new Point(100, 60), 4.0);
            Pump();
            viewport.ScrollToHorizontalOffset(80);
            viewport.ScrollToVerticalOffset(20);
            Pump();
            double panX = viewport.HorizontalOffset, panY = viewport.VerticalOffset;
            EditTrimTime(window, start: false, "1.8");

            VideoTrimmerWindow.ShowFile(second, settings);
            AwaitTrimmer(window);
            Assert((double)Property(firstSession, "End") == 1.8 && Element<TextBox>(window, "EndTimeInput").Visibility == Visibility.Collapsed,
                "switching commits an unfinished time edit to the outgoing video and hides its editor");
            SetTrimMarkers(window, 1.5, 3.0);
            Call(window, "SeekToSeconds", 2.0);
            volume.Volume = 0.6;
            Assert(!volume.IsExportMuted && !GetField<bool>(window, "_preciseCut") && GetField<bool>(window, "_loopEnabled"),
                "a second MP4 starts with its own export and playback choices");
            VideoTrimmerWindow.ShowFile(silent, settings);
            AwaitTrimmer(window);
            SetTrimMarkers(window, 0.2, 1.2);
            Call(window, "SeekToSeconds", 0.7);

            for (int pass = 0; pass < 4; pass++)
            {
                // The intermediate load is deliberately left unfinished.
                Call(window, "ActivateSessionAt", 1);
                Call(window, "ActivateSessionAt", 0);
                AwaitTrimmer(window);
                AssertTrimRange(window, 1.1, 1.8, "first MP4 retains its markers and displayed range through rapid switches");
                AssertPreviewColor(window, 320, c => c.G > c.R + 50 && c.G > c.B + 50,
                    "first tab displays its own green frame after rapid switching");
                Assert(Math.Abs((double)Call(window, "GetCurrentTimelineSeconds")! - 1.8) < 0.03
                    && !(bool)Call(window, "IsPlaying")! && volume.Volume == 0.25 && volume.IsExportMuted
                    && precise.IsChecked == true && loop.IsChecked == false && !GetField<bool>(window, "_detailedTimeDisplay"),
                    "first MP4 retains its paused cursor, audio, precise mode, loop and time display");
                Assert(GetField<double>(window, "_currentZoom") == 4
                    && Math.Abs(viewport.HorizontalOffset - panX) < 1 && Math.Abs(viewport.VerticalOffset - panY) < 1,
                    "first MP4 retains its zoom and pan after videos with different dimensions");
                Call(window, "ActivateSessionAt", 1);
                AwaitTrimmer(window);
                AssertTrimRange(window, 1.5, 3.0, "second MP4 retains a different selection and range display");
                AssertPreviewColor(window, 640, c => c.R > 180 && c.G > 180 && c.B < 60,
                    "second tab displays its own yellow frame and dimensions");
                Assert(Math.Abs((double)Call(window, "GetCurrentTimelineSeconds")! - 2) < 0.03
                    && volume.Volume == 0.6 && !volume.IsExportMuted && precise.IsChecked == false && loop.IsChecked == true,
                    "second MP4 retains its own cursor, volume and fast export mode");
                Call(window, "ActivateSessionAt", 2);
                AwaitTrimmer(window);
                AssertTrimRange(window, 0.2, 1.2, "silent MP4 retains its independent selection");
                AssertPreviewColor(window, 160, c => c.R > 220 && c.G > 220 && c.B > 220,
                    "silent tab displays its own white frame after audio-video loads");
                Assert(!volume.HasAudioTrack && volume.Visibility == Visibility.Collapsed
                    && Math.Abs((double)Call(window, "GetCurrentTimelineSeconds")! - 0.7) < 0.03,
                    "silent MP4 does not inherit an audio control or another video's cursor");
            }
            var loads = GetField<List<Task>>(window, "_previewLoads");
            AwaitCondition(() => { lock (loads) return loads.Count == 0; }, "Stale decoders did not settle after repeated switching");
            Assert(((float[])Property(Element<FrameworkElement>(window, "Waveform"), "Peaks")).All(p => p == 0),
                "late audio decoding cannot paint a different video's waveform over the silent tab");

            Call(window, "ActivateSessionAt", 1);
            AwaitTrimmer(window);
            Call(window, "PlayPauseBtn_Click", window, new RoutedEventArgs());
            Call(window, "ActivateSessionAt", 2);
            AwaitTrimmer(window);
            Call(window, "ActivateSessionAt", 1);
            AwaitTrimmer(window);
            Assert((bool)Call(window, "IsPlaying")!, "returning to a playing tab resumes only that video's playback");
            Call(window, "SeekToSeconds", 2.0);

            Call(window, "ActivateSessionAt", 0);
            AwaitTrimmer(window);
            EditTrimTime(window, start: true, "1.79");
            string invalidPath = Path.Combine(temp, "invalid-pending-range.mp4");
            var invalidExport = (Task<bool>)Call(window, "ExportToFileAsync", invalidPath)!;
            AwaitCondition(() => invalidExport.IsCompleted, "Invalid-range export did not settle");
            Assert(!invalidExport.GetAwaiter().GetResult() && !File.Exists(invalidPath),
                "export validates the committed time edit and rejects a segment below the minimum duration");
            EditTrimTime(window, start: true, "1.2");
            string firstCopy = Path.Combine(temp, "first-copy.mp4");
            var firstExport = (Task<bool>)Call(window, "ExportToFileAsync", firstCopy)!;
            Call(window, "ActivateSessionAt", 2);
            var duplicateExport = (Task<bool>)Call(window, "ExportToFileAsync", Path.Combine(temp, "concurrent-copy.mp4"))!;
            Assert(ReferenceEquals(Field(window, "_active"), firstSession) && !duplicateExport.GetAwaiter().GetResult(),
                "switch and export requests cannot replace a running job's source or parameters");
            AwaitCondition(() => firstExport.IsCompleted, "First timeline export timed out");
            Assert(firstExport.GetAwaiter().GetResult(), "first timeline exports its pending manual selection");
            AwaitTrimmer(window);
            var firstProbe = ProbeFixture(ffmpeg, firstCopy);
            Assert(Math.Abs(firstProbe.Duration - 0.6) < 0.12 && !firstProbe.Audio,
                "precise export uses the first video's committed interval and mute setting");
            AssertFixtureColor(ffmpeg, firstCopy, Path.Combine(temp, "first-copy-frame.png"), c => c.G > c.R + 50 && c.G > c.B + 50,
                "first export contains its green middle region, proving the source and start offset");

            Call(window, "ActivateSessionAt", 1);
            AwaitTrimmer(window);
            AssertTrimRange(window, 1.5, 3.0, "exporting the first video leaves the second video's pending trim untouched");
            Assert(volume.Volume == 0.6 && !volume.IsExportMuted && !GetField<bool>(window, "_preciseCut"),
                "exporting the first video leaves the second video's audio and export mode untouched");
            var firstCopyHash = SHA256.HashData(File.ReadAllBytes(firstCopy));
            var conflict = (Task<bool>)Call(window, "ExportToFileAsync", firstCopy)!;
            Assert(!conflict.GetAwaiter().GetResult() && SHA256.HashData(File.ReadAllBytes(firstCopy)).SequenceEqual(firstCopyHash),
                "the second video cannot overwrite the first tab's exported copy");
            string cancelledPath = Path.Combine(temp, "second-cancelled.mp4");
            var cancelled = (Task<bool>)Call(window, "ExportToFileAsync", cancelledPath)!;
            Call(window, "CancelExportBtn_Click", window, new RoutedEventArgs());
            AwaitCondition(() => cancelled.IsCompleted, "Second timeline cancellation timed out");
            Assert(!cancelled.GetAwaiter().GetResult() && !File.Exists(cancelledPath), "canceling a second-tab export removes its incomplete output");
            AssertTrimRange(window, 1.5, 3.0, "canceling export preserves the second video's selection for retry");
            string secondCopy = Path.Combine(temp, "second-copy.mp4");
            var secondExport = (Task<bool>)Call(window, "ExportToFileAsync", secondCopy)!;
            AwaitCondition(() => secondExport.IsCompleted, "Second timeline export timed out");
            Assert(secondExport.GetAwaiter().GetResult(), "second timeline exports independently after cancellation");
            AwaitTrimmer(window);
            var secondProbe = ProbeFixture(ffmpeg, secondCopy);
            Assert(Math.Abs(secondProbe.Duration - 1.5) < 0.15 && secondProbe.Audio,
                "fast export uses the second video's interval and retains its audio");
            AssertFixtureColor(ffmpeg, secondCopy, Path.Combine(temp, "second-copy-frame.png"), c => c.R > 180 && c.G > 180 && c.B < 60,
                "second export contains its yellow middle region, not frames from another tab");
            double attenuation = FixtureMeanVolume(ffmpeg, secondCopy) - FixtureMeanVolume(ffmpeg, second);
            Assert(Math.Abs(attenuation - 20 * Math.Log10(0.6)) < 0.8,
                "exported audio uses the second video's volume, without inheriting the first video's mute");
            Assert(hashes.All(pair => SHA256.HashData(File.ReadAllBytes(pair.Key)).SequenceEqual(pair.Value)),
                "independent Save As New exports preserve all three source files byte for byte");
            var secondCopyHash = SHA256.HashData(File.ReadAllBytes(secondCopy));

            Call(window, "ActivateSessionAt", 2);
            AwaitTrimmer(window);
            AssertTrimRange(window, 0.2, 1.2, "both exports and cancellation leave the third video's trim untouched");
            precise.IsChecked = true;
            Call(window, "PreciseCutToggleBtn_Click", precise, new RoutedEventArgs());
            var overwrite = (Task<bool>)Call(window, "ExportToFileAsync", silent)!;
            AwaitCondition(() => overwrite.IsCompleted, "Silent timeline overwrite timed out");
            Assert(overwrite.GetAwaiter().GetResult(), "third tab can overwrite only its own source");
            AwaitTrimmer(window);
            Assert(Math.Abs(ProbeFixture(ffmpeg, silent).Duration - 1) < 0.12
                && !SHA256.HashData(File.ReadAllBytes(silent)).SequenceEqual(hashes[silent])
                && SHA256.HashData(File.ReadAllBytes(firstCopy)).SequenceEqual(firstCopyHash)
                && SHA256.HashData(File.ReadAllBytes(secondCopy)).SequenceEqual(secondCopyHash)
                && SHA256.HashData(File.ReadAllBytes(first)).SequenceEqual(hashes[first])
                && SHA256.HashData(File.ReadAllBytes(second)).SequenceEqual(hashes[second]),
                "third-tab overwrite changes only its selected file and preserves both other sources and copies");
            Call(window, "ActivateSessionAt", 0);
            AwaitTrimmer(window);
            Assert(GetField<string>(window, "_mediaFilePath") == firstCopy && !(bool)Call(window, "IsCropModified")!
                && volume.Volume == 0.25 && volume.IsExportMuted && GetField<bool>(window, "_preciseCut"),
                "the first tab retains its own exported file and audio preferences after other exports");
            Console.WriteLine("PASS: multi-video marker edits, pending inputs, repeated switching, playback, real interval/color/audio isolation and independent overwrites.");
        }
        finally
        {
            SetField(window, "_closeApproved", true);
            if (window.IsVisible) window.Close();
            AwaitCondition(() => !window.IsVisible, "Isolation fixture did not close");
            var loads = GetField<List<Task>>(window, "_previewLoads");
            AwaitCondition(() => { lock (loads) return loads.Count == 0; }, "Isolation fixture decoders did not close");
        }
    }
}
