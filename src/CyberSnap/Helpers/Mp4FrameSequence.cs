using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CyberSnap.Capture;

namespace CyberSnap.Helpers;

/// <summary>
/// Decodes MP4 preview frames via FFmpeg for flash-free WPF trimmer playback.
/// WPF MediaElement/WMF shows a black frame on every seek and loop restart.
/// </summary>
internal sealed class Mp4FrameSequence : IDisposable
{
    private const int MaxPreviewFrames = 900;
    private const int MaxPreviewWidth = 1280;

    private readonly string _framesDirectory;
    private readonly string[] _framePaths;
    private readonly Dictionary<int, BitmapSource> _frameCache = new();
    private bool _disposed;

    public double TotalDurationSeconds { get; }
    public double PreviewFps { get; }
    public int FrameCount => _framePaths.Length;

    private Mp4FrameSequence(string framesDirectory, string[] framePaths, double totalDurationSeconds, double previewFps)
    {
        _framesDirectory = framesDirectory;
        _framePaths = framePaths;
        TotalDurationSeconds = totalDurationSeconds;
        PreviewFps = previewFps;
    }

    public static async Task<Mp4FrameSequence> OpenAsync(string filePath, double requestedFps)
    {
        if (requestedFps <= 0)
            requestedFps = 30;

        string? ffmpeg = VideoRecorder.FindFfmpeg();
        if (ffmpeg == null)
            throw new InvalidOperationException("FFmpeg binary not found.");

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Video file not found.", filePath);

        double duration = await Task.Run(() => ProbeDurationSeconds(ffmpeg, filePath));
        if (duration <= 0.05)
            throw new InvalidOperationException("Could not read video duration.");

        double effectiveFps = requestedFps;
        if (duration * effectiveFps > MaxPreviewFrames)
            effectiveFps = MaxPreviewFrames / duration;
        effectiveFps = Math.Clamp(effectiveFps, 1, requestedFps);

        string tempDir = Path.Combine(Path.GetTempPath(), $"cybersnap-mp4prev-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string pattern = Path.Combine(tempDir, "frame_%06d.jpg");
            string fps = effectiveFps.ToString("0.###", CultureInfo.InvariantCulture);
            string scaleFilter = $"scale='min({MaxPreviewWidth},iw)':-2";
            string args = $"-y -i \"{filePath}\" -vf \"fps={fps},{scaleFilter}\" -q:v 3 \"{pattern}\"";

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Failed to start FFmpeg.");

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                string err = await process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                    ? $"FFmpeg preview decode failed ({process.ExitCode})."
                    : err.Trim());
            }

            string[] framePaths = Directory.GetFiles(tempDir, "frame_*.jpg", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (framePaths.Length == 0)
                throw new InvalidOperationException("FFmpeg produced no preview frames.");

            double frameDuration = framePaths.Length / effectiveFps;
            double totalDuration = Math.Max(duration, frameDuration);
            return new Mp4FrameSequence(tempDir, framePaths, totalDuration, effectiveFps);
        }
        catch
        {
            TryDeleteDirectory(tempDir);
            throw;
        }
    }

    public int GetFrameIndexAt(double seconds)
    {
        if (_framePaths.Length == 0)
            return 0;

        if (seconds <= 0)
            return 0;

        if (seconds >= TotalDurationSeconds - 0.0001)
            return _framePaths.Length - 1;

        int index = (int)Math.Floor(seconds * PreviewFps);
        return Math.Clamp(index, 0, _framePaths.Length - 1);
    }

    public BitmapSource GetFrameSource(int frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        frameIndex = Math.Clamp(frameIndex, 0, _framePaths.Length - 1);
        if (_frameCache.TryGetValue(frameIndex, out BitmapSource? cached))
            return cached;

        using var bitmap = BitmapPerf.LoadDetached(_framePaths[frameIndex]);
        BitmapSource source = BitmapPerf.ToBitmapSource(bitmap);
        _frameCache[frameIndex] = source;
        return source;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _frameCache.Clear();
        TryDeleteDirectory(_framesDirectory);
    }

    private static double ProbeDurationSeconds(string ffmpeg, string filePath)
    {
        string? directory = Path.GetDirectoryName(ffmpeg);
        string ffprobePath = directory == null
            ? "ffprobe.exe"
            : Path.Combine(directory, "ffprobe.exe");

        if (!File.Exists(ffprobePath))
        {
            if (ffmpeg.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                ffprobePath = ffmpeg[..^"ffmpeg.exe".Length] + "ffprobe.exe";
            else if (ffmpeg.EndsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
                ffprobePath = ffmpeg[..^"ffmpeg".Length] + "ffprobe";
        }

        if (!File.Exists(ffprobePath))
            return 0;

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });

            if (process == null)
                return 0;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                ? seconds
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup for temp preview frames.
        }
    }
}
