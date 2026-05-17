using System;
using System.Windows.Media.Animation;

namespace CyberSnap.UI;

internal static class Motion
{
    /// <summary>When true, all WPF animations use zero duration (instant).</summary>
    internal static bool Disabled { get; set; }

    private static readonly IEasingFunction _smoothInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction _smoothOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction _smoothIn = Freeze(new QuarticEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction _softOut = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });

    internal static IEasingFunction SmoothInOut => _smoothInOut;
    internal static IEasingFunction SmoothOut => _smoothOut;
    internal static IEasingFunction SmoothIn => _smoothIn;
    internal static IEasingFunction SoftOut => _softOut;

    private static IEasingFunction Freeze(EasingFunctionBase easing)
    {
        if (easing.CanFreeze) easing.Freeze();
        return easing;
    }

    /// <summary>Returns TimeSpan.Zero when animations are disabled, otherwise the given duration.</summary>
    internal static TimeSpan Ms(double milliseconds) => Disabled ? TimeSpan.Zero : TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>Returns TimeSpan.Zero when animations are disabled, otherwise the given duration.</summary>
    internal static TimeSpan Sec(double seconds) => Disabled ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);

    /// <summary>Returns null when animations are disabled, otherwise the given easing.</summary>
    internal static IEasingFunction? Ease(IEasingFunction? easing = null) => Disabled ? null : easing;

    internal static DoubleAnimation To(double to, int milliseconds, IEasingFunction? easing = null) => new()
    {
        To = to,
        Duration = Ms(milliseconds),
        EasingFunction = Ease(easing ?? SmoothInOut)
    };

    internal static DoubleAnimation FromTo(double from, double to, int milliseconds, IEasingFunction? easing = null) => new(from, to, Ms(milliseconds))
    {
        EasingFunction = Ease(easing ?? SmoothInOut)
    };
}
