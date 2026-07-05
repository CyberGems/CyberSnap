using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Services;
using UserControl = System.Windows.Controls.UserControl;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace CyberSnap.UI.Controls;

public partial class MediaVolumeControl : UserControl
{
    public static readonly DependencyProperty VolumeProperty =
        DependencyProperty.Register(
            nameof(Volume),
            typeof(double),
            typeof(MediaVolumeControl),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnVolumeChanged));

    public static readonly DependencyProperty IsExportMutedProperty =
        DependencyProperty.Register(
            nameof(IsExportMuted),
            typeof(bool),
            typeof(MediaVolumeControl),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnMuteChanged));

    public static readonly DependencyProperty HasAudioTrackProperty =
        DependencyProperty.Register(
            nameof(HasAudioTrack),
            typeof(bool),
            typeof(MediaVolumeControl),
            new PropertyMetadata(true, OnHasAudioTrackChanged));

    public static readonly DependencyProperty InterfaceLanguageProperty =
        DependencyProperty.Register(
            nameof(InterfaceLanguage),
            typeof(string),
            typeof(MediaVolumeControl),
            new PropertyMetadata("en", OnInterfaceLanguageChanged));

    public static readonly RoutedEvent VolumeChangedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(VolumeChanged),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(MediaVolumeControl));

    public static readonly RoutedEvent ExportMuteChangedEvent =
        EventManager.RegisterRoutedEvent(
            nameof(ExportMuteChanged),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(MediaVolumeControl));

    private bool _isSliderDragging;
    private bool _isTrackDrag;

    public MediaVolumeControl()
    {
        InitializeComponent();
        UpdateSpeakerIcon();
        UpdateTooltips();
    }

    public double Volume
    {
        get => (double)GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, Math.Clamp(value, 0.0, 1.0));
    }

    public bool IsExportMuted
    {
        get => (bool)GetValue(IsExportMutedProperty);
        set => SetValue(IsExportMutedProperty, value);
    }

    public bool HasAudioTrack
    {
        get => (bool)GetValue(HasAudioTrackProperty);
        set => SetValue(HasAudioTrackProperty, value);
    }

    public string InterfaceLanguage
    {
        get => (string)GetValue(InterfaceLanguageProperty);
        set => SetValue(InterfaceLanguageProperty, value);
    }

    public event RoutedEventHandler VolumeChanged
    {
        add => AddHandler(VolumeChangedEvent, value);
        remove => RemoveHandler(VolumeChangedEvent, value);
    }

    public event RoutedEventHandler ExportMuteChanged
    {
        add => AddHandler(ExportMuteChangedEvent, value);
        remove => RemoveHandler(ExportMuteChangedEvent, value);
    }

    private static void OnVolumeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaVolumeControl control)
        {
            control.UpdateSpeakerIcon();
            control.UpdateTooltips();
            control.RaiseEvent(new RoutedEventArgs(VolumeChangedEvent, control));
        }
    }

    private static void OnMuteChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaVolumeControl control)
        {
            control.UpdateSpeakerIcon();
            control.UpdateTooltips();
            control.RaiseEvent(new RoutedEventArgs(ExportMuteChangedEvent, control));
        }
    }

    private static void OnHasAudioTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaVolumeControl control)
            control.UpdateAvailability();
    }

    private static void OnInterfaceLanguageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MediaVolumeControl control)
            control.UpdateTooltips();
    }

    public void SetGifMode()
    {
        HasAudioTrack = false;
        ToolTip = LocalizationService.Translate(InterfaceLanguage, "GIF files do not contain audio");
    }

    public void SetNoAudioMode()
    {
        HasAudioTrack = false;
        ToolTip = LocalizationService.Translate(InterfaceLanguage, "No audio track in this file");
    }

    private void UpdateAvailability()
    {
        Visibility = HasAudioTrack ? Visibility.Visible : Visibility.Collapsed;
        IsEnabled = HasAudioTrack;
    }

    private void UpdateSpeakerIcon()
    {
        if (IsExportMuted || Volume <= 0.001)
            SpeakerIcon.Text = "\uE74F";
        else if (Volume <= 0.33)
            SpeakerIcon.Text = "\uE992";
        else if (Volume <= 0.66)
            SpeakerIcon.Text = "\uE993";
        else
            SpeakerIcon.Text = "\uE767";
    }

    public void UpdateTooltips()
    {
        if (!HasAudioTrack)
            return;

        string lang = InterfaceLanguage;
        string exportTip = IsExportMuted
            ? LocalizationService.Translate(lang, "Muted. Audio will be excluded from the exported file.")
            : LocalizationService.Translate(lang, "Adjust volume. Export will include audio at this level.");

        MuteBtn.ToolTip = IsExportMuted
            ? LocalizationService.Translate(lang, "Unmute audio")
            : LocalizationService.Translate(lang, "Mute audio");

        PillBorder.ToolTip = exportTip;
        VolumeSlider.ToolTip = exportTip;
    }

    public void RefreshThemeBrushes()
    {
        Resources["VolumeTrackFillBrush"] = Theme.Brush(Theme.Accent);
        Resources["VolumeTrackBgBrush"] = Theme.Brush(Theme.AccentSubtle);
    }

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        IsExportMuted = !IsExportMuted;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSliderDragging && IsExportMuted && Volume > 0.001)
            IsExportMuted = false;
    }

    private void VolumeSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (IsInsideThumb(e.OriginalSource as DependencyObject))
        {
            _isSliderDragging = true;
            _isTrackDrag = false;
            return;
        }

        if (TrySetVolumeFromMousePosition(e.GetPosition(VolumeSlider)))
        {
            e.Handled = true;
            _isSliderDragging = true;
            _isTrackDrag = true;
            VolumeSlider.CaptureMouse();
        }
    }

    private void VolumeSlider_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isTrackDrag || e.LeftButton != MouseButtonState.Pressed)
            return;

        TrySetVolumeFromMousePosition(e.GetPosition(VolumeSlider));
        e.Handled = true;
    }

    private void VolumeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isTrackDrag && VolumeSlider.IsMouseCaptured)
            VolumeSlider.ReleaseMouseCapture();

        _isSliderDragging = false;
        _isTrackDrag = false;
    }

    private bool TrySetVolumeFromMousePosition(Point position)
    {
        double width = VolumeSlider.ActualWidth;
        if (width <= 1)
            return false;

        const double thumbRadius = 6;
        double trackStart = thumbRadius;
        double trackEnd = width - thumbRadius;
        double trackWidth = trackEnd - trackStart;
        if (trackWidth <= 0)
            return false;

        double ratio = Math.Clamp((position.X - trackStart) / trackWidth, 0.0, 1.0);
        VolumeSlider.Value = VolumeSlider.Minimum + ratio * (VolumeSlider.Maximum - VolumeSlider.Minimum);
        return true;
    }

    private static bool IsInsideThumb(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Thumb)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
