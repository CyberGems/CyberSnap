using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace CyberSnap.UI;

/// <summary>
/// Gentle loading pulse for placeholder text (opacity breathe loop).
/// Motion-aware: with animations disabled the text just sits at full opacity.
/// </summary>
internal static class LoadingTextShimmer
{
    private const double PulseFrom = 0.45;
    private const double PulseTo = 1.0;
    private const double PulseSeconds = 1.0;

    public static void Start(TextBlock textBlock, MediaColor baseColor, double durationSeconds = 1.0, double opacity = 1.0)
    {
        textBlock.Foreground = new SolidColorBrush(baseColor);
        textBlock.OpacityMask = null;

        if (Motion.Disabled || durationSeconds <= 0)
        {
            textBlock.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
            textBlock.Opacity = opacity;
            return;
        }

        double seconds = durationSeconds > 0 ? durationSeconds : PulseSeconds;
        var pulse = new DoubleAnimation(PulseFrom * opacity, PulseTo * opacity,
            Motion.Sec(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Motion.Ease(Motion.SmoothInOut)
        };
        textBlock.BeginAnimation(System.Windows.UIElement.OpacityProperty, pulse);
    }

    public static void Stop(TextBlock textBlock, MediaBrush fallbackBrush, double opacity = 1.0)
    {
        textBlock.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
        textBlock.Foreground = fallbackBrush;
        textBlock.OpacityMask = null;
        textBlock.Opacity = opacity;
    }
}
