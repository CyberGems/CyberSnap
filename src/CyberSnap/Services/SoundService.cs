using System.IO;
using System.Media;
using System.Collections.Concurrent;
using System.Reflection;
using CyberSnap.Models;
using NAudio.Wave;

namespace CyberSnap.Services;

public static class SoundService
{
    private const int MaxQueuedSounds = 8;

    // Cached WAV bytes per sound event (decoded from MP3 at load time)
    private static readonly Dictionary<SoundEvent, byte[]> _defaultWavs = new();
    private static readonly Dictionary<SoundEvent, byte[]> _customWavs = new();
    private static readonly object _cacheLock = new();

    // Per-sound mute flags (SoundEvent → muted)
    private static Dictionary<SoundEvent, bool> _mutedSounds = new();

    private static readonly object PlaybackGate = new();
    private static BlockingCollection<byte[]>? _playbackQueue;
    private static Thread? _playbackThread;
    private static int _suppressionDepth;

    public static bool Muted { get; set; }
    internal static bool IsPlaybackSuppressed => Muted || Volatile.Read(ref _suppressionDepth) > 0;

    /// <summary>Initialize the sound service with user preferences.</summary>
    public static void Initialize(Dictionary<SoundEvent, string?> customSounds, Dictionary<SoundEvent, bool> mutedSounds)
    {
        lock (_cacheLock)
        {
            _mutedSounds = new Dictionary<SoundEvent, bool>(mutedSounds);
            _defaultWavs.Clear();
            _customWavs.Clear();

            // Load default MP3s from embedded resources
            foreach (SoundEvent evt in Enum.GetValues<SoundEvent>())
            {
                try
                {
                    var mp3Bytes = LoadEmbeddedMp3(evt);
                    if (mp3Bytes is not null)
                        _defaultWavs[evt] = DecodeMp3ToWav(mp3Bytes);
                }
                catch
                {
                    // Sound missing — no crash, just silent for that event
                }
            }

            // Load custom sounds (MP3 or WAV) from user paths
            if (customSounds is not null)
            {
                foreach (var kvp in customSounds)
                {
                    if (!string.IsNullOrWhiteSpace(kvp.Value) && File.Exists(kvp.Value))
                    {
                        var decoded = TryDecodeCustomFile(kvp.Value);
                        if (decoded is not null)
                            _customWavs[kvp.Key] = decoded;
                    }
                }
            }
        }
    }

    /// <summary>Set a custom sound (MP3 or WAV) for an event. Pass null to revert to default.</summary>
    public static void SetCustomSound(SoundEvent evt, string? filePath)
    {
        lock (_cacheLock)
        {
            _customWavs.Remove(evt);

            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                var decoded = TryDecodeCustomFile(filePath);
                if (decoded is not null)
                    _customWavs[evt] = decoded;
            }
        }
    }

    /// <summary>Set per-sound mute flag.</summary>
    public static void SetSoundMuted(SoundEvent evt, bool muted)
    {
        lock (_cacheLock)
        {
            _mutedSounds[evt] = muted;
        }
    }

    public static bool IsSoundMuted(SoundEvent evt)
    {
        lock (_cacheLock)
        {
            // Default (no entry) = not muted, i.e. the sound plays.
            return _mutedSounds.TryGetValue(evt, out var m) && m;
        }
    }

    public static void PlayCaptureSound() => Play(SoundEvent.Capture);
    public static void PlayColorSound() => Play(SoundEvent.Color);
    public static void PlayTextSound() => Play(SoundEvent.Text);
    public static void PlayScanSound() => Play(SoundEvent.Scan);
    public static void PlayRecordStartSound() => Play(SoundEvent.RecordStart);
    public static void PlayRecordStopSound() => Play(SoundEvent.RecordStop);
    public static void PlayErrorSound() => Play(SoundEvent.Error);
    public static void PlayStartupSound() => Play(SoundEvent.Startup);
    public static void PlayAchievementSound() => Play(SoundEvent.Achievement);
    public static void PlayUploadSound() => Play(SoundEvent.Upload);
    /// <summary>Brief system-status toasts (Sent to the editor, encoding wait, etc.).</summary>
    public static void PlaySystemSound() => Play(SoundEvent.System);

    /// <summary>Play a sound by event type. Respects global mute, per-sound mute, and suppression.</summary>
    public static void Play(SoundEvent evt)
    {
        if (IsPlaybackSuppressed) return;
        if (IsSoundMuted(evt)) return;

        byte[]? wav = null;
        lock (_cacheLock)
        {
            _customWavs.TryGetValue(evt, out wav);
            if (wav is null)
                _defaultWavs.TryGetValue(evt, out wav);
        }

        if (wav is null) return;
        PlayAsync(wav);
    }

    public static IDisposable SuppressPlayback()
    {
        Interlocked.Increment(ref _suppressionDepth);
        return new PlaybackSuppressionHandle();
    }

    // ── Internal ───────────────────────────────────────────────────────────

    private static void PlayAsync(byte[] wav)
    {
        EnsurePlaybackWorker();
        _playbackQueue?.TryAdd(wav);
    }

    private static void EnsurePlaybackWorker()
    {
        lock (PlaybackGate)
        {
            if (_playbackQueue is not null && _playbackThread?.IsAlive == true)
                return;

            _playbackQueue?.Dispose();
            _playbackQueue = new BlockingCollection<byte[]>(MaxQueuedSounds);
            _playbackThread = new Thread(() =>
            {
                foreach (var queuedWav in _playbackQueue.GetConsumingEnumerable())
                {
                    try
                    {
                        using var ms = new MemoryStream(queuedWav);
                        using var player = new SoundPlayer(ms);
                        player.PlaySync();
                    }
                    catch
                    {
                    }
                }
            })
            {
                IsBackground = true,
                Name = "CyberSnapSoundPlayback"
            };
            _playbackThread.Start();
        }
    }

    private static byte[]? LoadEmbeddedMp3(SoundEvent evt)
    {
        var name = evt switch
        {
            SoundEvent.Capture => "capture",
            SoundEvent.Color => "color",
            SoundEvent.Text => "text",
            SoundEvent.Scan => "scan",
            SoundEvent.RecordStart => "record-start",
            SoundEvent.RecordStop => "record-stop",
            SoundEvent.Error => "error",
            SoundEvent.Startup => "startup",
            SoundEvent.Achievement => "achievement",
            SoundEvent.Upload => "upload",
            SoundEvent.System => "system",
            _ => null
        };
        if (name is null) return null;

        var resourceName = $"CyberSnap.Assets.Sounds.{name}.mp3";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Decode MP3 bytes to a WAV byte array (PCM 16-bit).</summary>
    private static byte[] DecodeMp3ToWav(byte[] mp3Bytes)
    {
        using var mp3Stream = new MemoryStream(mp3Bytes);
        using var reader = new Mp3FileReader(mp3Stream);

        // Read all PCM samples
        using var pcmStream = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            pcmStream.Write(buffer, 0, read);

        var waveFormat = reader.WaveFormat;
        return WriteWavHeader(pcmStream.ToArray(), waveFormat.SampleRate,
            (short)waveFormat.Channels, (short)waveFormat.BitsPerSample);
    }

    /// <summary>
    /// Decode a user-supplied audio file (MP3 or WAV, PCM or float) to PCM 16-bit WAV bytes.
    /// Returns null on failure so the caller can fall back to the default sound.
    /// </summary>
    private static byte[]? TryDecodeCustomFile(string path)
    {
        try
        {
            // AudioFileReader handles MP3, WAV (PCM/float), and AIFF uniformly,
            // exposing samples as normalized 32-bit float which we quantize to PCM16.
            using var reader = new AudioFileReader(path);
            var samples = reader.ToSampleProvider();
            int sampleRate = reader.WaveFormat.SampleRate;
            short channels = (short)reader.WaveFormat.Channels;

            using var pcmStream = new MemoryStream();
            var buffer = new float[4096];
            int read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    var clamped = Math.Clamp(buffer[i], -1f, 1f);
                    short s = (short)(clamped * short.MaxValue);
                    pcmStream.WriteByte((byte)(s & 0xFF));
                    pcmStream.WriteByte((byte)((s >> 8) & 0xFF));
                }
            }

            return WriteWavHeader(pcmStream.ToArray(), sampleRate, channels, 16);
        }
        catch
        {
            // Unsupported/corrupt file — fall back to default
            return null;
        }
    }

    /// <summary>Wrap raw PCM bytes in a canonical RIFF/WAVE header.</summary>
    private static byte[] WriteWavHeader(byte[] pcmBytes, int sampleRate, short channels, short bitsPerSample)
    {
        using var wavStream = new MemoryStream();
        using var bw = new BinaryWriter(wavStream);

        int dataSize = pcmBytes.Length;

        bw.Write("RIFF"u8);
        bw.Write(36 + dataSize);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * bitsPerSample / 8);
        bw.Write((short)(channels * bitsPerSample / 8));
        bw.Write(bitsPerSample);
        bw.Write("data"u8);
        bw.Write(dataSize);
        bw.Write(pcmBytes);

        return wavStream.ToArray();
    }

    private sealed class PlaybackSuppressionHandle : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Interlocked.Decrement(ref _suppressionDepth);
        }
    }
}
