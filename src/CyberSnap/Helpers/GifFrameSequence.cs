using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace CyberSnap.Helpers;

/// <summary>
/// Decodes an animated GIF frame-by-frame for reliable WPF preview playback.
/// WPF MediaElement/WMF often freezes mid-GIF when Netscape loop metadata is present.
/// </summary>
internal sealed class GifFrameSequence : IDisposable
{
    private const int PropertyTagFrameDelay = 0x5100;

    private readonly MemoryStream _gifStream;
    private readonly Bitmap _bitmap;
    private readonly FrameDimension _dimension;
    private readonly int[] _frameDelayMs;
    private readonly double[] _frameStartSeconds;
    private readonly Dictionary<int, BitmapSource> _frameCache = new();
    private bool _disposed;

    public int FrameCount => _frameDelayMs.Length;
    public double TotalDurationSeconds { get; }

    private GifFrameSequence(MemoryStream gifStream, Bitmap bitmap, int defaultFrameDelayMs)
    {
        _gifStream = gifStream;
        _bitmap = bitmap;
        _dimension = new FrameDimension(bitmap.FrameDimensionsList[0]);
        int frameCount = bitmap.GetFrameCount(_dimension);
        if (frameCount <= 0)
            throw new InvalidOperationException("GIF contains no frames.");

        _frameDelayMs = ReadFrameDelays(bitmap, frameCount, defaultFrameDelayMs);
        _frameStartSeconds = new double[frameCount];

        double start = 0;
        for (int i = 0; i < frameCount; i++)
        {
            _frameStartSeconds[i] = start;
            start += _frameDelayMs[i] / 1000.0;
        }

        TotalDurationSeconds = start;
    }

    public static GifFrameSequence Open(string filePath, int defaultFrameDelayMs)
    {
        if (defaultFrameDelayMs <= 0)
            defaultFrameDelayMs = 100;

        // LoadDetached/new Bitmap(source) flatten animated GIFs to a single frame.
        // Keep the encoded bytes in memory so GDI+ retains every frame.
        var stream = new MemoryStream(File.ReadAllBytes(filePath), writable: false);
        var bitmap = new Bitmap(stream);
        return new GifFrameSequence(stream, bitmap, defaultFrameDelayMs);
    }

    public int GetFrameIndexAt(double seconds)
    {
        if (_frameStartSeconds.Length == 0)
            return 0;

        if (seconds <= 0)
            return 0;

        for (int i = _frameStartSeconds.Length - 1; i >= 0; i--)
        {
            if (seconds >= _frameStartSeconds[i] - 0.0001)
                return i;
        }

        return 0;
    }

    public BitmapSource GetFrameSource(int frameIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        frameIndex = Math.Clamp(frameIndex, 0, FrameCount - 1);
        if (_frameCache.TryGetValue(frameIndex, out BitmapSource? cached))
            return cached;

        _bitmap.SelectActiveFrame(_dimension, frameIndex);
        using var frame = BitmapPerf.Clone32bppArgb(_bitmap);
        BitmapSource source = BitmapPerf.ToBitmapSource(frame);
        _frameCache[frameIndex] = source;
        return source;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _frameCache.Clear();
        _bitmap.Dispose();
        _gifStream.Dispose();
    }

    private static int[] ReadFrameDelays(Bitmap bitmap, int frameCount, int defaultFrameDelayMs)
    {
        var delays = new int[frameCount];
        for (int i = 0; i < frameCount; i++)
            delays[i] = defaultFrameDelayMs;

        if (!TryGetFrameDelayProperty(bitmap, out byte[]? bytes) || bytes == null)
            return delays;

        for (int i = 0; i < frameCount; i++)
        {
            if (i * 4 + 3 >= bytes.Length)
                break;

            int centiseconds = BitConverter.ToInt32(bytes, i * 4);
            delays[i] = centiseconds <= 0 ? 10 : centiseconds * 10;
        }

        return delays;
    }

    private static bool TryGetFrameDelayProperty(Bitmap bitmap, out byte[]? bytes)
    {
        bytes = null;
        foreach (int id in bitmap.PropertyIdList)
        {
            if (id != PropertyTagFrameDelay)
                continue;

            var propItem = bitmap.GetPropertyItem(PropertyTagFrameDelay);
            if (propItem?.Value != null)
            {
                bytes = propItem.Value;
                return true;
            }
        }

        return false;
    }
}
