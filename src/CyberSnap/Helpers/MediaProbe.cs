using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CyberSnap.Capture;

namespace CyberSnap.Helpers;

/// <summary>
/// Single place for ffprobe-based media inspection (duration, audio presence).
/// Replaces ad-hoc ffmpeg-stderr parsing, which breaks with locales and codecs.
/// All methods are best-effort and return fallback values on any failure.
/// </summary>
internal static class MediaProbe
{
    /// <summary>Locates ffprobe next to the bundled ffmpeg, or null when missing.</summary>
    public static string? FindFfprobe()
    {
        string? ffmpeg = VideoRecorder.FindFfmpeg();
        if (ffmpeg == null)
            return null;

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

        return File.Exists(ffprobePath) ? ffprobePath : null;
    }

    /// <summary>Container duration in seconds via ffprobe, or 0 when unreadable.</summary>
    public static double TryGetDurationSeconds(string mediaPath)
    {
        string? ffprobe = FindFfprobe();
        if (ffprobe == null || !File.Exists(mediaPath))
            return 0;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{mediaPath}\"",
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

    /// <summary>Duration from frame count × frame rate (more reliable for GIFs), or 0.</summary>
    public static double TryGetFrameCountDuration(string mediaPath)
    {
        string? ffprobe = FindFfprobe();
        if (ffprobe == null || !File.Exists(mediaPath))
            return 0;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=nb_frames,r_frame_rate -of csv=p=0 \"{mediaPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });

            if (process == null)
                return 0;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            if (string.IsNullOrWhiteSpace(output))
                return 0;

            string[] parts = output.Split(',');
            if (parts.Length < 2)
                return 0;

            if (!int.TryParse(parts[0].Trim(), out int frames) || frames <= 0)
                return 0;

            double fps = ParseFrameRate(parts[1].Trim());
            return fps > 0 ? frames / fps : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>True when the file has at least one audio stream.</summary>
    public static bool HasAudioTrack(string mediaPath)
    {
        string? ffprobe = FindFfprobe();
        if (ffprobe == null || !File.Exists(mediaPath))
            return false;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{mediaPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });

            if (process == null)
                return false;

            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5000);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static double ParseFrameRate(string rate)
    {
        string[] segments = rate.Split('/');
        if (segments.Length == 2
            && double.TryParse(segments[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double numerator)
            && double.TryParse(segments[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double denominator)
            && denominator > 0)
        {
            return numerator / denominator;
        }

        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out double direct)
            ? direct
            : 0;
    }
}
