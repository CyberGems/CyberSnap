using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
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
    private readonly BitmapFrameCache _frameCache = new(capacity: 60);
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

    public static Task<Mp4FrameSequence> OpenAsync(string filePath, double requestedFps)
        => OpenAsync(filePath, requestedFps, CancellationToken.None);

    public static async Task<Mp4FrameSequence> OpenAsync(string filePath, double requestedFps, CancellationToken ct)
    {
        if (requestedFps <= 0)
            requestedFps = 30;

        string? ffmpeg = VideoRecorder.FindFfmpeg();
        if (ffmpeg == null)
            throw new InvalidOperationException("FFmpeg binary not found.");

        if (!File.Exists(filePath))
            throw new FileNotFoundException("Video file not found.", filePath);

        double duration = await Task.Run(() => MediaProbe.TryGetDurationSeconds(filePath), ct);
        if (duration <= 0.05)
            throw new InvalidOperationException("Could not read video duration.");

        // Cap total preview frames so very long videos don't exhaust disk/RAM.
        // For >15 min material the effective fps may drop below 1 fps, which is
        // fine for a trimmer preview (filmstrip + scrubbing, not full playback).
        double effectiveFps = Math.Min(requestedFps, MaxPreviewFrames / Math.Max(duration, 0.05));
        effectiveFps = Math.Clamp(effectiveFps, 0.25, requestedFps);

        string tempDir = Path.Combine(Path.GetTempPath(), $"cybersnap-mp4prev-{Guid.NewGuid():N}");
        SweepStalePreviewTemp();
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

            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync();
                throw;
            }

            string err = await stderrTask;
            if (process.ExitCode != 0)
            {
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
        if (_frameCache.TryGet(frameIndex, out BitmapSource? cached))
            return cached;

        using var bitmap = BitmapPerf.LoadDetached(_framePaths[frameIndex]);
        BitmapSource source = BitmapPerf.ToBitmapSource(bitmap);
        _frameCache.Add(frameIndex, source);
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

    /// <summary>
    /// Deletes preview/palette temp leftovers from crashed sessions (older than a day,
    /// so live sessions from this or another app instance are never touched).
    /// </summary>
    private static void SweepStalePreviewTemp()
    {
        string tempRoot;
        try
        {
            tempRoot = Path.GetTempPath();
        }
        catch
        {
            return;
        }

        DateTime cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(1);
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(tempRoot, "cybersnap-mp4prev-*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoffUtc)
                        TryDeleteDirectory(dir);
                }
                catch
                {
                    // Best effort per entry.
                }
            }

            foreach (string file in Directory.EnumerateFiles(tempRoot, "cybersnap-palette-*.png"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                        File.Delete(file);
                }
                catch
                {
                    // Best effort per entry.
                }
            }
        }
        catch
        {
            // Temp enumeration itself failed; skip the sweep.
        }
    }
}
