using System;
using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaPen = System.Windows.Media.Pen;
using MediaPoint = System.Windows.Point;

namespace CyberSnap.UI.Controls;

/// <summary>
/// Lightweight audio waveform strip for the video trimmer. Draws one vertical
/// bar per peak value; bars outside the current [SelectionStart, SelectionEnd]
/// range render dimmed so the kept segment stands out.
/// </summary>
internal sealed class WaveformControl : FrameworkElement
{
    public static readonly DependencyProperty PeaksProperty =
        DependencyProperty.Register(
            nameof(Peaks),
            typeof(float[]),
            typeof(WaveformControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(
            nameof(BarBrush),
            typeof(System.Windows.Media.Brush),
            typeof(WaveformControl),
            new FrameworkPropertyMetadata(MediaBrushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionStartProperty =
        DependencyProperty.Register(
            nameof(SelectionStart),
            typeof(double),
            typeof(WaveformControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SelectionEndProperty =
        DependencyProperty.Register(
            nameof(SelectionEnd),
            typeof(double),
            typeof(WaveformControl),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public float[]? Peaks
    {
        get => (float[]?)GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public MediaBrush BarBrush
    {
        get => (MediaBrush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    /// <summary>Kept-range start as a 0..1 fraction of the total duration.</summary>
    public double SelectionStart
    {
        get => (double)GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    /// <summary>Kept-range end as a 0..1 fraction of the total duration.</summary>
    public double SelectionEnd
    {
        get => (double)GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        float[]? peaks = Peaks;
        if (peaks == null || peaks.Length == 0)
            return;

        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        double selStart = Math.Clamp(SelectionStart, 0, 1);
        double selEnd = Math.Clamp(SelectionEnd, 0, 1);

        var barBrush = BarBrush ?? MediaBrushes.Gray;

        var dimBrush = barBrush.Clone();
        dimBrush.Opacity *= 0.25;
        dimBrush.Freeze();

        double midY = h / 2;
        double maxBarH = h - 2;
        double slot = w / peaks.Length;
        double barW = Math.Max(1, Math.Min(3, slot * 0.65));

        for (int i = 0; i < peaks.Length; i++)
        {
            double centerX = (i + 0.5) * slot;
            double fraction = centerX / w;
            bool selected = fraction >= selStart && fraction <= selEnd;

            double barH = Math.Max(1, Math.Min(maxBarH, peaks[i] * maxBarH));
            var pen = new MediaPen(selected ? barBrush : dimBrush, barW)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();

            dc.DrawLine(
                pen,
                new MediaPoint(centerX, midY - barH / 2),
                new MediaPoint(centerX, midY + barH / 2));
        }
    }
}
