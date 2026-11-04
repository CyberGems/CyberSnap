using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CyberSnap.Capture;

namespace CyberSnap.Helpers;

/// <summary>
/// Single place for ffprobe-based media inspection (duration, audio presence).
/// Replaces ad-hoc ffmpeg-stderr parsing, which breaks with locales and codecs.
/// All methods are best-effort and return fallback values on any failure.
/// </summary>
internal static class MediaProbe
{
    /// <summary>Locates ffprobe next to the bundled ffmpeg, or on PATH, or null when missing.
    /// Older installs bundled only ffmpeg.exe, so the PATH fallback matters there.</summary>
    public static string? FindFfprobe()
    {
        string? ffmpeg = VideoRecorder.FindFfmpeg();
        if (ffmpeg != null)
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

            if (File.Exists(ffprobePath))
                return ffprobePath;
        }

        return FindOnPath("ffprobe.exe");
    }

    /// <summary>First hit for <paramref name="executable"/> on PATH, or null.</summary>
    private static string? FindOnPath(string executable)
    {
        try
        {
            var psi = new ProcessStartInfo("where", executable)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd().Trim();
            proc?.WaitForExit(3000);
            if (string.IsNullOrEmpty(output))
                return null;

            var first = output.Split('\n')[0].Trim();
            return File.Exists(first) ? first : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Container duration in seconds via ffprobe, with an ffmpeg-metadata fallback
    /// for installs where ffprobe is unavailable. Returns 0 when unreadable.</summary>
    public static double TryGetDurationSeconds(string mediaPath)
    {
        if (!File.Exists(mediaPath))
            return 0;

        string? ffprobe = FindFfprobe();
        if (ffprobe != null)
        {
            double viaProbe = ReadDurationFromProcess(ffprobe,
                $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{mediaPath}\"",
                readsError: false);
            if (viaProbe > 0.05)
                return viaProbe;
        }

        // Older installs bundled only ffmpeg.exe. Its own metadata line carries the same
        // container duration ("Duration: 00:00:02.80, ..."), written to stderr.
        return TryGetDurationViaFfmpeg(mediaPath);
    }

    private static double ReadDurationFromProcess(string fileName, string arguments, bool readsError)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = !readsError,
                RedirectStandardError = readsError
            });

            if (process == null)
                return 0;

            string output = readsError
                ? process.StandardError.ReadToEnd().Trim()
                : process.StandardOutput.ReadToEnd().Trim();
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

    private static double TryGetDurationViaFfmpeg(string mediaPath)
    {
        string? ffmpeg = VideoRecorder.FindFfmpeg();
        if (ffmpeg == null)
            return 0;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-hide_banner -i \"{mediaPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });

            if (process == null)
                return 0;

            // ffmpeg exits with code 1 (no output file requested) after printing the metadata.
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);

            var match = System.Text.RegularExpressions.Regex.Match(
                stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (!match.Success)
                return 0;

            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600
                + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
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

    public sealed record VideoStreamInfo(
        string Codec,
        int Width,
        int Height,
        double Fps,
        string PixelFormat,
        long BitRate);

    public sealed record AudioStreamInfo(
        string Codec,
        int Channels,
        int SampleRate,
        long BitRate);

    public sealed record MediaInfo(
        double DurationSeconds,
        long SizeBytes,
        string FormatName,
        long FormatBitRate,
        VideoStreamInfo? Video,
        AudioStreamInfo? Audio);

    /// <summary>
    /// Full container + first video/audio stream inspection in a single ffprobe
    /// call. Best-effort: null when ffprobe is missing or the file is unreadable,
    /// individual fields fall back to 0/empty when a tag is absent.
    /// </summary>
    public static MediaInfo? TryGetMediaInfo(string mediaPath)
    {
        string? ffprobe = FindFfprobe();
        if (ffprobe == null || !File.Exists(mediaPath))
            return null;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -show_format -show_streams -of json \"{mediaPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });

            if (process == null)
                return null;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);
            if (string.IsNullOrWhiteSpace(output))
                return null;

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;

            double duration = 0;
            long size = 0;
            string formatName = "";
            long formatBitRate = 0;
            if (root.TryGetProperty("format", out var format))
            {
                duration = GetDouble(format, "duration");
                size = GetInt64(format, "size");
                formatName = GetString(format, "format_name");
                formatBitRate = GetInt64(format, "bit_rate");
            }

            VideoStreamInfo? video = null;
            AudioStreamInfo? audio = null;
            if (root.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    string codecType = GetString(stream, "codec_type");
                    if (video is null && string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        double fps = ParseFrameRate(GetString(stream, "r_frame_rate"));
                        if (fps <= 0)
                            fps = ParseFrameRate(GetString(stream, "avg_frame_rate"));
                        video = new VideoStreamInfo(
                            GetString(stream, "codec_name"),
                            GetInt32(stream, "width"),
                            GetInt32(stream, "height"),
                            fps,
                            GetString(stream, "pix_fmt"),
                            GetInt64(stream, "bit_rate"));
                    }
                    else if (audio is null && string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
                    {
                        audio = new AudioStreamInfo(
                            GetString(stream, "codec_name"),
                            GetInt32(stream, "channels"),
                            GetInt32(stream, "sample_rate"),
                            GetInt64(stream, "bit_rate"));
                    }
                }
            }

            return new MediaInfo(duration, size, formatName, formatBitRate, video, audio);
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double GetDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return 0;
    }

    private static long GetInt64(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;
        if (value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            return parsed;
        return 0;
    }

    private static int GetInt32(JsonElement element, string name)
    {
        long value = GetInt64(element, name);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : 0;
    }
}
