using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using CyberSnap.Models;
using CyberSnap.Services;
using MediaColor = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

namespace CyberSnap.UI;

// Milestone rail: a neon progress track shown under the "Celebrations" row in the Notifications
// tab. It always shows progress over the capture-count milestones (lit nodes for reached ones, a
// CyberPaste-style grabber at the current position); when a milestone has been reached but not yet
// seen here, the rail comes alive once with the same flowing sweep + breathing glow as a
// celebration toast (see ApplyCelebrationVisual in ToastWindow.xaml.cs), then settles back to calm.
public partial class SettingsWindow
{
    // Neon palette shared with the celebration toast sweep.
    private static readonly MediaColor RailCyan = MediaColor.FromRgb(0x00, 0xF2, 0xFF);
    private static readonly MediaColor RailPurple = MediaColor.FromRgb(0x7A, 0x00, 0xFF);
    private static readonly MediaColor RailMagenta = MediaColor.FromRgb(0xFF, 0x00, 0xD0);

    private const double RailYCenter = 11;   // vertical center of the track within the canvas
    private const double RailPad = 9;         // horizontal inset so the end nodes aren't clipped

    private Canvas? _railCanvas;
    private Border? _railBaseLine;
    private Border? _railFillLine;
    private Border? _railGrabber;
    private TranslateTransform? _railSweep;
    private LinearGradientBrush? _railSweepBrush;
    private readonly List<Border> _railNodes = new();

    // Layout model recomputed on each refresh and consumed by LayoutRail (which needs the live
    // canvas width). Node X = RailPad + frac * usableWidth.
    private double[] _railNodeFracs = Array.Empty<double>();
    private bool[] _railNodeAchieved = Array.Empty<bool>();
    private double _railGrabberFrac;
    private int _railNewestNodeIndex = -1;

    private bool _railSizeHooked;

    // (Re)build the rail from the current settings. When reveal is true and a milestone is newly
    // reached, the rail animates once and stamps LastSeenMilestone so it stays calm afterwards.
    // reveal is false for the hidden initial load (so the one-shot animation isn't wasted unseen).
    private void RefreshMilestoneRail(bool reveal)
    {
        if (MilestoneRailHost is null || _settingsService is null)
            return;

        var s = _settingsService.Settings;
        int count = s.CelebrationCaptureCount;
        var values = CelebrationMilestones.Values;

        int highestAchieved = CelebrationMilestones.HighestAchieved(count);
        int? next = CelebrationMilestones.Next(count);
        bool isNew = s.CelebrationsEnabled && highestAchieved > s.LastSeenMilestone;

        // Dim and stand down when celebrations are off entirely.
        MilestoneRailHost.Opacity = s.CelebrationsEnabled ? 1.0 : 0.32;

        BuildRailElements(values, count, highestAchieved, isNew);
        UpdateRailCaption(count, next, s.CurrentStreak);

        // Position + (optionally) animate after layout, when the canvas has a real width.
        MilestoneRailHost.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            LayoutRail();

            if (reveal && isNew && s.CelebrationsEnabled)
            {
                if (!Motion.Disabled)
                    PlayRailNewState();

                // Stamp as seen so the next reveal is calm; persist (Save is debounced).
                s.LastSeenMilestone = highestAchieved;
                try { _settingsService.Save(); } catch { /* non-critical */ }
            }
        }));
    }

    private void BuildRailElements(int[] values, int count, int highestAchieved, bool isNew)
    {
        // The rail opens with a "first capture" stop (value 1) so a brand-new user sees immediate
        // progress after their very first capture instead of an empty bar until 50. This is a
        // rail-only node — NOT a celebration milestone (those stay in CelebrationMilestones.Values
        // and drive the in-the-moment flourish; adding 1 there would fire a stray "1 captures"
        // toast clashing with the first-capture greeting).
        int[] railValues = new int[values.Length + 1];
        railValues[0] = 1;
        Array.Copy(values, 0, railValues, 1, values.Length);
        int n = railValues.Length;

        MilestoneRailHost!.Children.Clear();
        _railNodes.Clear();
        _railSweep = null;
        _railSweepBrush = null;

        _railCanvas = new Canvas { Height = 22, VerticalAlignment = VerticalAlignment.Top };
        MilestoneRailHost.Children.Add(_railCanvas);

        var dimBrush = new SolidColorBrush(MediaColor.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        var cyanBrush = new SolidColorBrush(RailCyan);

        // Base line (full span, dim) + progress fill (cyan, up to the grabber).
        _railBaseLine = new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = dimBrush };
        _railFillLine = new Border { Height = 2, CornerRadius = new CornerRadius(1), Background = cyanBrush };
        _railCanvas.Children.Add(_railBaseLine);
        _railCanvas.Children.Add(_railFillLine);

        // Rail nodes, evenly spaced by index (index 0 = the first-capture stop).
        _railNodeFracs = new double[n];
        _railNodeAchieved = new bool[n];
        _railNewestNodeIndex = -1;
        for (int i = 0; i < n; i++)
        {
            _railNodeFracs[i] = n == 1 ? 0 : (double)i / (n - 1);
            bool achieved = railValues[i] <= count;
            _railNodeAchieved[i] = achieved;
            // Flare only the freshly-lit real milestone (highestAchieved is from Values, never 1).
            if (achieved && railValues[i] == highestAchieved)
                _railNewestNodeIndex = i;

            double d = achieved ? 10 : 7;
            var node = new Border
            {
                Width = d,
                Height = d,
                CornerRadius = new CornerRadius(d / 2),
                Background = achieved ? new SolidColorBrush(RailCyan) : dimBrush,
                ToolTip = i == 0 ? LocalizationService.Translate("First capture") : railValues[i].ToString("N0"),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            if (achieved)
                node.Effect = new DropShadowEffect { Color = RailCyan, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.85 };

            _railNodes.Add(node);
            _railCanvas.Children.Add(node);
        }

        // Grabber: a bright core with a cyan halo, parked at the fractional progress position.
        _railGrabberFrac = ComputeGrabberFrac(railValues, count);
        _railGrabber = new Border
        {
            Width = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(MediaColor.FromRgb(0xEA, 0xFD, 0xFF)),
            BorderBrush = new SolidColorBrush(RailCyan),
            BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { Color = RailCyan, BlurRadius = isNew ? 14 : 9, ShadowDepth = 0, Opacity = 0.9 }
        };
        _railCanvas.Children.Add(_railGrabber);

        if (!_railSizeHooked)
        {
            MilestoneRailHost.SizeChanged += (_, _) => LayoutRail();
            _railSizeHooked = true;
        }
    }

    // Fractional node-axis position of the grabber: interpolates between the last reached node and
    // the next one by how far the raw count sits between their values (railValues[0] = 1, the
    // first-capture stop). Parks at the far-left node before any capture, and on the last node
    // once everything is reached.
    private static double ComputeGrabberFrac(int[] values, int count)
    {
        int n = values.Length;
        int lastIdx = -1;
        for (int i = 0; i < n; i++)
            if (values[i] <= count) lastIdx = i;

        if (lastIdx >= n - 1)
            return 1.0; // all reached

        int startIdx = Math.Max(lastIdx, 0);
        int nextIdx = lastIdx + 1;
        int prevVal = lastIdx >= 0 ? values[lastIdx] : 0;
        int nextVal = values[nextIdx];
        double seg = nextVal > prevVal ? (double)(count - prevVal) / (nextVal - prevVal) : 0;
        seg = Math.Max(0, Math.Min(1, seg));

        double gi = startIdx + seg * (nextIdx - startIdx);
        return n == 1 ? 0 : gi / (n - 1);
    }

    // Positions every rail element from the live canvas width. Called after layout and on resize.
    private void LayoutRail()
    {
        if (_railCanvas is null || _railBaseLine is null || _railFillLine is null || _railGrabber is null)
            return;

        double w = _railCanvas.ActualWidth;
        if (w <= 0) return;

        double usable = Math.Max(0, w - RailPad * 2);
        double X(double frac) => RailPad + frac * usable;

        Canvas.SetLeft(_railBaseLine, RailPad);
        Canvas.SetTop(_railBaseLine, RailYCenter - 1);
        _railBaseLine.Width = usable;

        double grabberX = X(_railGrabberFrac);
        Canvas.SetLeft(_railFillLine, RailPad);
        Canvas.SetTop(_railFillLine, RailYCenter - 1);
        _railFillLine.Width = Math.Max(0, grabberX - RailPad);

        for (int i = 0; i < _railNodes.Count; i++)
        {
            var node = _railNodes[i];
            double nx = X(_railNodeFracs[i]);
            Canvas.SetLeft(node, nx - node.Width / 2);
            Canvas.SetTop(node, RailYCenter - node.Height / 2);
        }

        Canvas.SetLeft(_railGrabber, grabberX - _railGrabber.Width / 2);
        Canvas.SetTop(_railGrabber, RailYCenter - _railGrabber.Height / 2);
    }

    // The one-shot "new milestone" flourish, mirroring ApplyCelebrationVisual: a flowing
    // cyan->purple->magenta sweep on the progress fill + a breathing glow on the grabber + a pulse
    // on the freshly lit node.
    private void PlayRailNewState()
    {
        if (_railFillLine is null || _railGrabber is null) return;

        // Flowing sweep on the fill line (RelativeTransform so the translate is a fraction of width).
        _railSweep = new TranslateTransform();
        _railSweepBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            SpreadMethod = GradientSpreadMethod.Repeat,
            GradientStops = new GradientStopCollection
            {
                new GradientStop(RailCyan, 0.0),
                new GradientStop(RailPurple, 0.25),
                new GradientStop(RailMagenta, 0.5),
                new GradientStop(RailPurple, 0.75),
                new GradientStop(RailCyan, 1.0),
            },
            RelativeTransform = _railSweep
        };
        _railFillLine.Background = _railSweepBrush;
        _railSweep.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = Motion.Sec(1.4),
            RepeatBehavior = RepeatBehavior.Forever
        });

        // Breathing glow on the grabber.
        if (_railGrabber.Effect is DropShadowEffect glow)
        {
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation
            {
                From = 10,
                To = 26,
                Duration = Motion.Sec(0.8),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
            glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation
            {
                From = 0.6,
                To = 1.0,
                Duration = Motion.Sec(0.8),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        // A few celebratory pulses on the newest node.
        if (_railNewestNodeIndex >= 0 && _railNewestNodeIndex < _railNodes.Count &&
            _railNodes[_railNewestNodeIndex].RenderTransform is ScaleTransform scale)
        {
            var pulse = new DoubleAnimation
            {
                From = 1.0,
                To = 1.9,
                Duration = Motion.Sec(0.45),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3),
                EasingFunction = Motion.SmoothOut
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }
    }

    private void UpdateRailCaption(int count, int? next, int streak)
    {
        if (MilestoneRailHost is null) return;

        string countText = string.Format(LocalizationService.Translate("{0} captures and counting"), count);
        string tailText = next is int nv
            ? string.Format(LocalizationService.Translate("Next milestone: {0}"), nv)
            : LocalizationService.Translate("All milestones reached!");

        var caption = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Bottom,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush?)TryFindResource("ThemeTextPrimaryBrush") ?? Brushes.White
        };
        caption.Inlines.Add(new Run($"{countText}    ·    {tailText}"));

        // Passive streak awareness — a warm flame + day count once a streak is going (>= 2 days).
        if (streak >= 2)
        {
            caption.Inlines.Add(new Run("    ·    "));
            caption.Inlines.Add(new Run("🔥")
            {
                FontFamily = new FontFamily("Segoe UI Emoji"),
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0x9A, 0x3D))
            });
            caption.Inlines.Add(new Run(" " + string.Format(LocalizationService.Translate("{0}-day streak"), streak)));
        }

        MilestoneRailHost.Children.Add(caption);
    }
}
