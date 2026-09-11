using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CyberSnap.Capture;

namespace CyberSnap.Helpers;

/// <summary>
/// Extracts audio peak data for the trimmer waveform strip. Decodes to low-rate
/// mono PCM via ffmpeg and returns per-bucket peaks normalized to 0..1.
/// Returns null when there is no audio track, ffmpeg is missing, or the
/// extraction fails/cancelled — the caller simply hides the strip.
/// </summary>
internal static class AudioWaveform
{
    public static async Task<float[]?> GetPeaksAsync(string mediaPath, int bucketCount, CancellationToken ct)
    {
        if (bucketCount <= 0 || !File.Exists(mediaPath))
            return null;

        string? ffmpeg = VideoRecorder.FindFfmpeg();
        if (ffmpeg == null)
            return null;

        // Low-rate mono keeps the pipe small (~16 KB/s at 8 kHz, ~8 KB/s at 4 kHz).
        int sampleRate = 8000;
        try
        {
            double duration = await Task.Run(() => MediaProbe.TryGetDurationSeconds(mediaPath), ct);
            if (duration > 600)
                sampleRate = 4000;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // Best effort; keep the default rate.
        }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-v error -i \"{mediaPath}\" -map 0:a:0 -ac 1 -ar {sampleRate} -f s16le -acodec pcm_s16le pipe:1",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();

            // Drain stderr so a noisy file cannot block on a full pipe.
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var stdout = process.StandardOutput.BaseStream;
            using var buffer = new MemoryStream();
            try
            {
                await stdout.CopyToAsync(buffer, ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync();
                return null;
            }

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync();
                return null;
            }

            await stderrTask;
            if (process.ExitCode != 0 || buffer.Length < 2)
                return null;

            return ComputePeaks(buffer.GetBuffer(), (int)buffer.Length, bucketCount);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static float[] ComputePeaks(byte[] pcm, int length, int bucketCount)
    {
        int sampleCount = length / 2;
        var peaks = new float[bucketCount];
        if (sampleCount <= 0)
            return peaks;

        for (int i = 0; i < sampleCount; i++)
        {
            int bucket = (int)((long)i * bucketCount / sampleCount);
            if (bucket >= bucketCount)
                bucket = bucketCount - 1;

            short sample = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            float abs = Math.Abs(sample / 32768f);
            if (abs > peaks[bucket])
                peaks[bucket] = abs;
        }

        return peaks;
    }
}
