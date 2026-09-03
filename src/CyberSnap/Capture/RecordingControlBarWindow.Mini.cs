using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Native;
using CyberSnap.Services;
using CyberSnap.UI;
using Color = System.Windows.Media.Color;

namespace CyberSnap.Capture;

public sealed partial class RecordingControlBarWindow
{
    private const double MiniBarHeight = 36;
    private const double MiniButtonSize = 28;
    private const double MiniCornerRadius = 18;
    private const double MiniModeButtonWidth = 32;
    private const double FullModeButtonWidth = 40;
    private const double FullPrimaryWidth = 128;
    private const double FullPrimaryHeight = 40;
    private const int MiniHoverExpandMs = 140;
    private const int MiniHoverCollapseMs = 110;
    private const int MiniHoverExpandDelayMs = 70;
    private const int MiniHoverCollapseDelayMs = 380;
    private const int LongPressCancelMs = 700;

    private bool _isMini;
    private bool _miniHoverExpanded;
    private bool _suppressMiniHover;
    private DispatcherTimer? _miniHoverDelayTimer;
    private DispatcherTimer? _stopHoldTimer;
    private bool _stopHoldArmed;
    private bool _stopHoldFired;

    private void SetupMini()
    {
        _miniHoverDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MiniHoverExpandDelayMs) };
        _miniHoverDelayTimer.Tick += MiniHoverDelayTick;

        _stopHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressCancelMs) };
        _stopHoldTimer.Tick += StopHoldTimer_Tick;

        TintGrip(80);
        GripBtn.MouseEnter += (_, _) => TintGrip(170);
        GripBtn.MouseLeave += (_, _) => TintGrip(80);

        ModeBtn.MouseEnter += (_, _) =>
        {
            if (!ModeBtn.IsEnabled) return;
            ModeBtn.Background = Theme.Brush(Theme.AccentSubtle);
            ModeChevron.Stroke = Theme.Brush(_accentHover);
        };
        ModeBtn.MouseLeave += (_, _) =>
        {
            ModeBtn.Background = System.Windows.Media.Brushes.Transparent;
            ModeChevron.Stroke = Theme.Brush(Theme.TextPrimary);
        };
        ModeBtn.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_isEncoding) return;
            SetMiniMode(!_isMini);
        };

        StopBtn.MouseLeftButtonDown += StopBtn_MouseLeftButtonDown;
        StopBtn.MouseLeftButtonUp += StopBtn_MouseLeftButtonUp;
        StopBtn.MouseLeave += StopBtn_MouseLeave;

        ModeChevron.Stroke = Theme.Brush(Theme.TextPrimary);
        StopHoldFill.Background = Theme.Brush(Color.FromArgb(140, StopRed.R, StopRed.G, StopRed.B));
        UpdateModeChevronVisual();
    }

    private void StopBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!StopBtn.IsEnabled) return;

        if (_isMini)
        {
            StopBtn.CaptureMouse();
            BeginStopHold();
            return;
        }

        StopClicked?.Invoke();
    }

    private void StopBtn_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_stopHoldArmed)
            return;
        e.Handled = true;
        bool fired = _stopHoldFired;
        if (StopBtn.IsMouseCaptured)
            StopBtn.ReleaseMouseCapture();
        EndStopHold();
        if (!fired && StopBtn.IsEnabled)
            StopClicked?.Invoke();
    }

    private void StopBtn_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Keep the click/hold alive while we own capture; overlay z-order
        // flashes used to fire Leave and swallow the Stop click.
        if (_stopHoldArmed && !StopBtn.IsMouseCaptured)
            EndStopHold();
    }

    private void BeginStopHold()
    {
        _stopHoldArmed = true;
        _stopHoldFired = false;
        StopHoldFill.Height = 0;
        if (UI.Motion.Disabled)
        {
            _stopHoldTimer!.Interval = TimeSpan.FromMilliseconds(LongPressCancelMs);
            _stopHoldTimer.Start();
            return;
        }

        var fillAnim = Motion.FromTo(0, MiniButtonSize, LongPressCancelMs, Motion.SmoothIn);
        fillAnim.FillBehavior = FillBehavior.Stop;
        fillAnim.Completed += (_, _) => StopHoldFill.Height = MiniButtonSize;
        StopHoldFill.BeginAnimation(FrameworkElement.HeightProperty, fillAnim);
        _stopHoldTimer!.Start();
    }

    private void StopHoldTimer_Tick(object? sender, EventArgs e)
    {
        _stopHoldTimer?.Stop();
        if (!_stopHoldArmed || _stopHoldFired)
            return;
        _stopHoldFired = true;
        StopHoldFill.BeginAnimation(FrameworkElement.HeightProperty, null);
        StopHoldFill.Height = MiniButtonSize;
        CancelClicked?.Invoke();
        EndStopHold();
    }

    private void EndStopHold()
    {
        _stopHoldTimer?.Stop();
        _stopHoldArmed = false;
        StopHoldFill.BeginAnimation(FrameworkElement.HeightProperty, null);
        StopHoldFill.Height = 0;
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isMini || _isEncoding || _suppressMiniHover)
            return;
        ArmMiniHover(expand: true, MiniHoverExpandDelayMs);
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            e.Handled = true;
            ShowRecordingContextMenu(e);
            return;
        }

        // Keep the bar on top, but do not steal this click by activating the
        // overlay mid-down (that dropped Stop/Pause MouseUp).
        AssertBarTopmost();
        Dispatcher.BeginInvoke(() =>
        {
            if (OwnerWinFormsForm is RecordingForm form && !form.IsDisposed)
                form.ReclaimTransportHotkeys();
            AssertBarTopmost();
        }, DispatcherPriority.Background);
    }

    private void ShowRecordingContextMenu(MouseButtonEventArgs e)
    {
        if (_isEncoding)
            return;
        if (OwnerWinFormsForm is not RecordingForm form || form.IsDisposed)
            return;

        var wpf = PointToScreen(e.GetPosition(this));
        form.ShowEmptyAreaContextMenuAtScreen(new System.Drawing.Point(
            (int)Math.Round(wpf.X),
            (int)Math.Round(wpf.Y)));
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_suppressMiniHover)
            _suppressMiniHover = false;
        if (!_isMini || _isBarDragging || _stopHoldArmed)
            return;
        ArmMiniHover(expand: false, MiniHoverCollapseDelayMs);
    }

    private void ArmMiniHover(bool expand, int delayMs)
    {
        _miniHoverDelayTimer!.Stop();
        _miniHoverDelayTimer.Tag = expand;
        _miniHoverDelayTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        _miniHoverDelayTimer.Start();
    }

    private void MiniHoverDelayTick(object? sender, EventArgs e)
    {
        _miniHoverDelayTimer?.Stop();
        bool expand = _miniHoverDelayTimer?.Tag as bool? ?? false;
        if (expand == _miniHoverExpanded) return;
        if (!expand && (_isBarDragging || _stopHoldArmed)) return;
        SetMiniHoverExpanded(expand, animate: true);
    }

    private void SetMiniMode(bool mini)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetMiniMode(mini));
            return;
        }

        if (_isEncoding)
            mini = false;
        if (_isMini == mini)
            return;

        var fromRect = GetPhysicalRect();

        _isMini = mini;
        _miniHoverExpanded = false;
        _suppressMiniHover = mini;
        _miniHoverDelayTimer?.Stop();
        EndStopHold();

        ApplyModeChrome(setSlideSizes: true);
        UpdateLayout();

        // Keep the chevron side planted: shrink/grow to the right so << / >> match
        // the motion. Do not re-center on the capture region.
        var toSize = GetPhysicalRect();
        int x = fromRect.Left;
        int y = fromRect.Top + (fromRect.Height - toSize.Height) / 2;
        ClampToVirtualScreen(ref x, ref y, toSize.Width, toSize.Height);
        MoveBarToPhysical(x, y);
        _userPositioned = true;

        if (_isMini && _isRecording)
            StartMiniShine();
        else
            StopMiniShine();

        if (_isMini)
            StopShineAnimation();
        else if (!_isRecording && !_isEncoding)
            StartShineAnimation();

        UpdateTooltips();
        UpdateModeChevronVisual();
    }

    private void SetMiniHoverExpanded(bool expanded, bool animate)
    {
        if (!_isMini || _miniHoverExpanded == expanded)
            return;

        _miniHoverExpanded = expanded;
        ApplyModeChrome(setSlideSizes: !animate || UI.Motion.Disabled);
        if (animate && !UI.Motion.Disabled)
            AnimateMiniSlide();
        UpdateLayout();
        ClampPhysicalPosition();
        UpdateModeChevronVisual();
        UpdateTooltips();
    }

    private void ApplyModeChrome(bool setSlideSizes = true)
    {
        bool mini = _isMini && !_isEncoding;
        bool slide = mini && _miniHoverExpanded;
        bool full = !mini;

        FormatBadge.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        StorageText.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        FpsDivider.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        FpsCombo.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        TrimmerBtn.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        CancelBtn.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        GripBtn.Visibility = full ? Visibility.Visible : Visibility.Collapsed;

        TimerCluster.Margin = mini
            ? (slide ? new Thickness(12, 0, 4, 0) : new Thickness(14, 0, 14, 0))
            : new Thickness(10, 0, 8, 0);
        ActionsPanel.Margin = mini ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, 10, 0);

        double shellH = mini ? MiniBarHeight : BarHeight;
        double radius = mini ? MiniCornerRadius : UiChrome.ToolbarCornerRadius;
        Height = shellH;
        OuterShell.Height = shellH;
        OuterShell.CornerRadius = new CornerRadius(radius);
        ShadowPlate.CornerRadius = new CornerRadius(radius);
        EdgeRing.CornerRadius = new CornerRadius(radius);
        Root.CornerRadius = new CornerRadius(Math.Max(0, radius - 1));
        MiniShineRing.RadiusX = radius;
        MiniShineRing.RadiusY = radius;
        MiniShineRing.Visibility = mini && _isRecording && !UI.Motion.Disabled ? Visibility.Visible : Visibility.Collapsed;
        OuterShell.Cursor = mini ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Arrow;
        LeadingCluster.Margin = mini
            ? (slide ? new Thickness(6, 0, 0, 0) : new Thickness(0))
            : new Thickness(6, 0, 0, 0);

        ApplyPrimaryMetrics(mini, slide, setSlideSizes);
        ApplyStopMetrics(mini, slide, setSlideSizes);
        ApplyModeButtonMetrics(mini, slide, full, setSlideSizes);
        ApplyMiniSurface(mini);
        if (!_isEncoding)
            StatusText.FontSize = mini ? 15 : 18;
    }

    private void ApplyPrimaryMetrics(bool mini, bool slide, bool setSize)
    {
        bool show = !mini || (slide && (!_isRecording || _supportsPause));
        PrimaryBtn.Visibility = Visibility.Visible;
        PrimaryBtn.IsHitTestVisible = show;
        PrimaryText.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        ApplyPrimaryIconLayout(mini);

        double w = !show ? 0 : mini ? MiniButtonSize : FullPrimaryWidth;
        double h = mini ? MiniButtonSize : FullPrimaryHeight;
        double cr = mini ? MiniButtonSize / 2 : 20;
        PrimaryBtn.Height = h;
        PrimaryBtn.CornerRadius = new CornerRadius(cr);
        PrimaryBtn.Margin = w <= 0 ? new Thickness(0) : new Thickness(mini ? 2 : 4, 0, 0, 0);
        PrimaryClip.RadiusX = cr;
        PrimaryClip.RadiusY = cr;
        PrimaryClip.Rect = new Rect(0, 0, Math.Max(mini ? MiniButtonSize : FullPrimaryWidth, 1), h);
        if (setSize)
        {
            PrimaryBtn.Width = w;
            PrimaryBtn.Opacity = show ? 1 : 0;
        }
    }

    private void ApplyStopMetrics(bool mini, bool slide, bool setSize)
    {
        bool show = !mini || (slide && _isRecording);
        double w = show ? (mini ? MiniButtonSize : 40) : 0;
        double h = mini ? MiniButtonSize : 40;
        StopBtn.Visibility = Visibility.Visible;
        StopBtn.IsHitTestVisible = show;
        StopBtn.Height = h;
        StopBtn.CornerRadius = new CornerRadius(mini ? 6 : 10);
        StopBtn.Margin = w <= 0 ? new Thickness(0) : new Thickness(mini ? 4 : 8, 0, 0, 0);
        StopGlyph.Width = mini ? 10 : 18;
        StopGlyph.Height = mini ? 10 : 18;
        StopGlyph.CornerRadius = new CornerRadius(mini ? 2 : 3.5);
        if (setSize)
        {
            StopBtn.Width = w;
            StopBtn.Opacity = show ? 1 : 0;
        }
    }

    private void ApplyModeButtonMetrics(bool mini, bool slide, bool full, bool setSize)
    {
        bool show = full || slide;
        ModeBtn.Visibility = Visibility.Visible;
        ModeBtn.IsHitTestVisible = show;
        double w = show ? (mini ? MiniModeButtonWidth : FullModeButtonWidth) : 0;
        ModeBtn.Height = mini ? MiniButtonSize : 36;
        ModeBtn.Margin = w <= 0 ? new Thickness(0) : new Thickness(0, 0, 2, 0);
        ApplyModeChevronMetrics(mini);
        if (setSize)
        {
            ModeBtn.Width = w;
            ModeBtn.Opacity = show ? 1 : 0;
        }
    }

    private void ApplyModeChevronMetrics(bool mini)
    {
        if (mini)
        {
            // Match the timer cap-height (~15px) instead of filling the 28px well.
            ModeChevron.Width = 14;
            ModeChevron.Height = 11;
            ModeChevron.StrokeThickness = 1.7;
            ModeChevron.Margin = new Thickness(0, 1.5, 0, 0);
        }
        else
        {
            ModeChevron.Width = 28;
            ModeChevron.Height = 24;
            ModeChevron.StrokeThickness = 2.2;
            ModeChevron.Margin = new Thickness(0);
        }
    }

    private void ApplyPrimaryIconLayout(bool mini)
    {
        bool playGlyph = !_isRecording || _isPaused;
        double icon = mini
            ? (playGlyph ? 22 : 16)
            : 20;
        PrimaryIcon.Width = icon;
        PrimaryIcon.Height = icon;
        if (mini)
        {
            // Translate, not Margin: a left margin is absorbed by the centered
            // StackPanel and only shifts the glyph by half. Play triangles also
            // need a true optical nudge toward the tip.
            PrimaryIcon.Margin = new Thickness(0);
            PrimaryIcon.RenderTransform = playGlyph
                ? new TranslateTransform(2.0, -0.5)
                : Transform.Identity;
        }
        else
        {
            PrimaryIcon.RenderTransform = playGlyph
                ? new TranslateTransform(1.2, 0)
                : Transform.Identity;
            PrimaryIcon.Margin = new Thickness(0, 0, 8, 0);
        }
    }

    private void ApplyMiniSurface(bool mini)
    {
        if (!mini)
        {
            ConfigureShell();
            return;
        }

        Color wash;
        if (_isPaused)
        {
            var muted = Theme.TextMuted;
            wash = Color.FromArgb(28, muted.R, muted.G, muted.B);
        }
        else
        {
            wash = Color.FromArgb((byte)(Theme.IsDark ? 48 : 36), _accent.R, _accent.G, _accent.B);
        }

        Root.Background = Theme.Brush(ToMediaColor(UiChrome.SurfaceTier1));
        OuterShell.Background = Theme.Brush(wash);
        EdgeRing.Background = Theme.Brush(Color.FromArgb(
            _isPaused ? (byte)70 : (byte)160, _accent.R, _accent.G, _accent.B));
        MiniShineRing.Stroke = Theme.Brush(MiniShineStrokeColor());
    }

    private Color MiniShineStrokeColor()
    {
        if (_isPaused)
            return Color.FromArgb(190, 210, 215, 222);
        return Color.FromArgb(150, _accent.R, _accent.G, _accent.B);
    }

    private void AnimateMiniSlide()
    {
        bool slide = _miniHoverExpanded;
        bool showPrimary = slide && (!_isRecording || _supportsPause);
        int ms = slide ? MiniHoverExpandMs : MiniHoverCollapseMs;

        AnimateWidth(PrimaryBtn, showPrimary ? MiniButtonSize : 0, ms);
        AnimateWidth(StopBtn, slide && _isRecording ? MiniButtonSize : 0, ms);
        AnimateWidth(ModeBtn, slide ? MiniModeButtonWidth : 0, ms);
        AnimateOpacity(PrimaryBtn, showPrimary ? 1 : 0, ms);
        AnimateOpacity(StopBtn, slide && _isRecording ? 1 : 0, ms);
        AnimateOpacity(ModeBtn, slide ? 1 : 0, ms);
    }

    private static void AnimateWidth(FrameworkElement element, double to, int ms)
    {
        element.BeginAnimation(FrameworkElement.WidthProperty, null);
        var from = element.Width;
        if (double.IsNaN(from)) from = element.ActualWidth;
        if (Math.Abs(from - to) < 0.25)
        {
            element.Width = to;
            return;
        }

        var anim = Motion.FromTo(from, to, ms, Motion.SoftOut);
        anim.FillBehavior = FillBehavior.Stop;
        anim.Completed += (_, _) => element.Width = to;
        element.BeginAnimation(FrameworkElement.WidthProperty, anim);
    }

    private static void AnimateOpacity(UIElement element, double to, int ms)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        var anim = Motion.FromTo(element.Opacity, to, ms, Motion.SoftOut);
        anim.FillBehavior = FillBehavior.Stop;
        anim.Completed += (_, _) => element.Opacity = to;
        element.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void ClampPhysicalPosition()
    {
        var r = GetPhysicalRect();
        int x = r.Left;
        int y = r.Top;
        ClampToVirtualScreen(ref x, ref y, r.Width, r.Height);
        if (x != r.Left || y != r.Top)
            MoveBarToPhysical(x, y);
    }

    private Native.User32.RECT GetPhysicalRect()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && User32.GetWindowRect(hwnd, out var wr))
            return wr;
        return new Native.User32.RECT { Left = 0, Top = 0, Right = 1, Bottom = 1 };
    }

    private void StartMiniShine()
    {
        StopMiniShine();
        if (UI.Motion.Disabled || !_isMini || !_isRecording) return;

        MiniShineRing.Visibility = Visibility.Visible;
        MiniShineRing.Stroke = Theme.Brush(MiniShineStrokeColor());
        var travel = new DoubleAnimation
        {
            From = 0,
            To = 158,
            Duration = TimeSpan.FromMilliseconds(2600),
            RepeatBehavior = RepeatBehavior.Forever
        };
        MiniShineRing.BeginAnimation(Shape.StrokeDashOffsetProperty, travel);
    }

    private void StopMiniShine()
    {
        MiniShineRing.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        MiniShineRing.Visibility = Visibility.Collapsed;
    }

    private void UpdateModeChevronVisual()
    {
        // Full → mini: chevrons point left (compress). Mini → full: point right (expand).
        ModeChevron.Data = System.Windows.Media.Geometry.Parse(
            _isMini
                ? "M2,1 L8,6 L2,11 M7,1 L13,6 L7,11"
                : "M8,1 L2,6 L8,11 M13,1 L7,6 L13,11");
        ModeChevron.Stroke = Theme.Brush(Theme.TextPrimary);
    }

    private void TintGrip(byte alpha)
    {
        var brush = Theme.Brush(Color.FromArgb(
            alpha, Theme.TextPrimary.R, Theme.TextPrimary.G, Theme.TextPrimary.B));
        foreach (var child in GripDots.Children)
        {
            if (child is Ellipse dot)
                dot.Fill = brush;
        }
    }

    private void UpdateMiniTooltips()
    {
        GripBtn.ToolTip = LocalizationService.Translate("Recording drag grip tooltip");
        ModeBtn.ToolTip = LocalizationService.Translate(
            _isMini ? "Recording expand tooltip" : "Recording collapse tooltip");

        if (_isMini)
        {
            StopBtn.ToolTip = LocalizationService.Translate("Recording mini stop tooltip");
        }
    }

    private void TeardownMini()
    {
        _miniHoverDelayTimer?.Stop();
        _stopHoldTimer?.Stop();
        StopMiniShine();
        EndStopHold();
    }
}
