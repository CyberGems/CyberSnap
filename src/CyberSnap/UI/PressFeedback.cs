using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfPoint = System.Windows.Point;

namespace CyberSnap.UI;

/// <summary>
/// Animated press-scale for buttons (tactile visual-haptic feedback).
/// Animates the button's RenderTransform to 0.96 on IsPressed and back to 1.0
/// on release using <see cref="Motion"/>, so "Disable animations" is respected
/// (zero-duration => instant snap, same end state).
/// Attach via <c>ui:PressFeedback.Enabled="True"</c> in button styles.
/// </summary>
internal static class PressFeedback
{
    internal const double PressedScale = 0.96;
    private const int PressMs = 110;
    private const int ReleaseMs = 140;

    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(PressFeedback),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WpfButtonBase button)
            return;

        var descriptor = DependencyPropertyDescriptor.FromProperty(
            WpfButtonBase.IsPressedProperty, typeof(WpfButtonBase));
        if (descriptor is null)
            return;

        if ((bool)e.NewValue)
        {
            EnsureScaleTransform(button);
            descriptor.AddValueChanged(button, OnIsPressedChanged);
            button.Unloaded += OnButtonUnloaded;
        }
        else
        {
            descriptor.RemoveValueChanged(button, OnIsPressedChanged);
            button.Unloaded -= OnButtonUnloaded;
        }
    }

    private static void OnButtonUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButtonBase button)
            return;

        // Return to rest state so a recycled template never stays squashed.
        button.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        button.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (button.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
        }
    }

    private static void OnIsPressedChanged(object? sender, EventArgs e)
    {
        if (sender is not WpfButtonBase button || !GetEnabled(button))
            return;

        // Disabled buttons never squash; a mid-press disable releases instantly.
        if (!button.IsEnabled)
        {
            ClearAndSnap(button, 1.0);
            return;
        }

        EnsureScaleTransform(button);
        AnimateTo(button, button.IsPressed ? PressedScale : 1.0,
            button.IsPressed ? PressMs : ReleaseMs);
    }

    private static void EnsureScaleTransform(WpfButtonBase button)
    {
        if (button.RenderTransform is ScaleTransform)
        {
            if (button.RenderTransformOrigin == new WpfPoint(0, 0))
                button.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
            return;
        }

        button.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
        button.RenderTransform = new ScaleTransform(1.0, 1.0);
    }

    private static void AnimateTo(WpfButtonBase button, double scale, int milliseconds)
    {
        if (button.RenderTransform is not ScaleTransform)
            EnsureScaleTransform(button);

        if (Motion.Disabled)
        {
            ClearAndSnap(button, scale);
            return;
        }

        button.RenderTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty, Motion.To(scale, milliseconds, Motion.SmoothOut));
        button.RenderTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty, Motion.To(scale, milliseconds, Motion.SmoothOut));
    }

    private static void ClearAndSnap(WpfButtonBase button, double scale)
    {
        button.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        button.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (button.RenderTransform is ScaleTransform st)
        {
            st.ScaleX = scale;
            st.ScaleY = scale;
        }
        else
        {
            button.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
            button.RenderTransform = new ScaleTransform(scale, scale);
        }
    }
}
