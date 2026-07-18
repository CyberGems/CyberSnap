using Bitmap = System.Drawing.Bitmap;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Color = System.Windows.Media.Color;

namespace CyberSnap.UI;

public partial class ToastWindow : Window
{
    private const double RootCornerRadius = 10;
    private readonly DispatcherTimer _timer;
    private ToastSpec _spec;
    // Celebration timeline: cached default brush + the flowing celebration brush/sweep.
    private System.Windows.Media.Brush? _defaultProgressBrush;
    private System.Windows.Media.LinearGradientBrush? _celebrationBrush;
    private TranslateTransform? _celebrationSweep;
    private bool _isDismissing;
    private bool _isHovered;
    private bool _isFading;
    private bool _isSavingPreview;
    private bool _isDeletingSavedFile;
    private bool _isRunningShareAction;
    private bool _deleteFileOnDismiss;
    private int _toastStateVersion;
    private bool _closeAfterOpacityAnimation;
    private int _dismissAnimationToken;
    private bool _resumeDismissOnMouseLeave;

    private static ToastWindow? _current;
    private static CyberSnap.Models.ToastPosition _position = CyberSnap.Models.ToastPosition.TopCenter;
    private static double _durationSeconds = 2.5;
    private static double _systemDurationSeconds = 4.0;
    private static bool _notificationsEnabled = true;
    private static bool _systemNotificationsEnabled = true;
    private static double _fadeOutSeconds = 1.0;
    private static int _monitorIndex = -1;
    private static Models.AppSettings.ToastButtonLayoutSettings _buttonLayout = new();

    private bool _isPinned;
    private double _activeDurationSeconds = 2.5;
    private string? _savedFilePath;
    private Bitmap? _previewBitmap;
    private bool _isDragging;
    private System.Windows.Point _mouseDownPos;
    private System.Windows.Media.Brush? _dragBorderBrush;
    private Thickness _dragBorderThickness;

    private static bool IsDefaultDarkTheme()
        => Theme.IsDark && !Theme.IsGray;

    // Inner fill radius when EdgeRing uses a 1px padding ring (outer radius - stroke width).
    private const double EdgeRingStrokeWidth = 1.0;
    private static double InnerCornerRadius => Math.Max(0, RootCornerRadius - EdgeRingStrokeWidth);

    private static System.Windows.Media.Effects.DropShadowEffect CreateToastShadow()
    {
        // Default Dark: soft lift only — keep opacity modest so the halo doesn't muddy the edge ring.
        bool defaultDark = IsDefaultDarkTheme();
        return new()
        {
            BlurRadius = defaultDark ? 20 : 18,
            ShadowDepth = defaultDark ? 4 : 3,
            Opacity = defaultDark ? 0.42 : (Theme.IsDark ? 0.32 : 0.20),
            Direction = 270,
            Color = Colors.Black,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality
        };
    }

    /// <summary>
    /// Solid edge paint for the padding-ring technique. Opaque (clean composite) but muted —
    /// Theme.ToastBorder is a teal washed into ToastBg so the 1px ring feels light, not neon.
    /// </summary>
    private static Color DefaultDarkToastStroke()
        => Theme.ToastBorder;

    internal static (int Width, int Height, bool Framed) ComputeImageOnlyPreviewLayout(int sourceWidth, int sourceHeight)
    {
        int safeWidth = Math.Max(1, sourceWidth);
        int safeHeight = Math.Max(1, sourceHeight);
        double aspect = safeWidth / (double)safeHeight;
        bool framed = Math.Min(safeWidth, safeHeight) < 72 || aspect > 2.5 || aspect < 0.85;

        if (framed)
        {
            if (aspect < 0.85)
                return (188, 220, true);

            const int frameWidth = 280;
            const int maxFrameHeight = 176;
            const int minFrameHeight = 44;
            int frameHeight = (int)Math.Round(Math.Clamp(frameWidth / aspect, minFrameHeight, maxFrameHeight));
            return (frameWidth, frameHeight, true);
        }

        const int targetHeight = 188;
        double width = targetHeight * aspect;
        double height = targetHeight;

        if (width > 332)
        {
            width = 332;
            height = width / aspect;
        }
        else if (width < 188)
        {
            width = 188;
            height = Math.Min(targetHeight, width / aspect);
        }

        return ((int)Math.Round(width), (int)Math.Round(height), false);
    }

    private ToastWindow(ToastSpec spec)
    {
        _spec = spec;
        InitializeComponent();
        // Match content CornerRadius exactly — a larger HWND radius left mismatched arcs at the tips.
        CyberSnapWindowChrome.ApplyRoundedCorners(this, RootCornerRadius);
        Opacity = 0;
        Theme.Refresh();
        LoadOverlayIcons();
        UiScale.ApplyToWindow(this, OuterShell, scaleWindowBounds: false);

        double baseDuration = spec.PreviewBitmap is not null ? _durationSeconds : _systemDurationSeconds;
        _activeDurationSeconds = spec.DurationSeconds ?? baseDuration;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_activeDurationSeconds) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (ToastPinPolicy.CanAutoDismiss(_isPinned, _isHovered))
                DismissAnimated();
        };


        ConfigureShell();
        ApplySpec(spec);

        MouseEnter += (_, _) =>
        {
            _isHovered = true;
            CancelDismissForHover();
            _timer.Stop();
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressScale.ScaleX = ProgressScale.ScaleX;
            if (_spec.ShowOverlayButtons)
                AnimateOverlayButtons(1, _isPinned ? 1 : 1);
        };
        MouseLeave += (_, _) =>
        {
            _isHovered = false;
            if (_spec.ShowOverlayButtons)
                AnimateOverlayButtons(0, _isPinned ? 0.7 : 0);
            if (_isPinned)
            {
                _timer.Stop();
                return;
            }
            if (_resumeDismissOnMouseLeave)
            {
                _resumeDismissOnMouseLeave = false;
                DismissAnimated();
                return;
            }
            RestartVisibleTimer(Math.Max(0.1, ProgressScale.ScaleX * _activeDurationSeconds));
        };
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonUp += OnMouseRightButtonUp;
        Cursor = System.Windows.Input.Cursors.Hand;
        SourceInitialized += (_, _) => PopupWindowHelper.ApplyNoActivateChrome(this);
        SizeChanged += (_, _) => UpdateRootClip();
        Loaded += OnLoaded;
    }

    private void ConfigureShell()
    {
        Root.Background = Theme.Brush(Theme.ToastBg);
        Root.BorderBrush = System.Windows.Media.Brushes.Transparent;
        Root.BorderThickness = new Thickness(0);
        Root.Effect = null;
        TitleText.Foreground = Theme.Brush(Theme.TextPrimary);
        BodyText.Foreground = Theme.Brush(Theme.TextSecondary);

        if (IsDefaultDarkTheme())
        {
            // Padding-ring edge: EdgeRing is a solid stroke-colored plate with Padding=1;
            // Root sits inside as ToastBg with radius-1. No BorderThickness stroke → clean arcs.
            OuterShell.Background = System.Windows.Media.Brushes.Transparent;
            OuterShell.BorderBrush = System.Windows.Media.Brushes.Transparent;
            OuterShell.BorderThickness = new Thickness(0);
            OuterShell.CornerRadius = new CornerRadius(RootCornerRadius);
            OuterShell.Effect = null;

            ShadowPlate.Visibility = Visibility.Visible;
            ShadowPlate.Background = Theme.Brush(Theme.ToastBg);
            ShadowPlate.CornerRadius = new CornerRadius(RootCornerRadius);
            ShadowPlate.Effect = CreateToastShadow();

            EdgeRing.Background = Theme.Brush(DefaultDarkToastStroke());
            EdgeRing.Padding = new Thickness(EdgeRingStrokeWidth);
            EdgeRing.CornerRadius = new CornerRadius(RootCornerRadius);
            EdgeRing.BorderThickness = new Thickness(0);
            EdgeRing.SnapsToDevicePixels = true;
            EdgeRing.UseLayoutRounding = true;

            Root.CornerRadius = new CornerRadius(InnerCornerRadius);
            Root.SnapsToDevicePixels = true;
            Root.UseLayoutRounding = true;

            // Soft glow on the progress rail bleeds past the bottom arcs.
            ProgressGlow.BlurRadius = 0;
            ProgressGlow.Opacity = 0;

            ImageFrame.BorderBrush = Theme.Brush(Theme.BorderSubtle);
            InlinePreviewHost.Background = Theme.Brush(Theme.BgSecondary);
            InlinePreviewHost.BorderBrush = Theme.Brush(Theme.BorderSubtle);
            PreviewActionsDivider.Background = Theme.Brush(Theme.Separator);
        }
        else
        {
            // Grayscale / Light: prior OuterShell border + shadow (unchanged).
            OuterShell.Background = System.Windows.Media.Brushes.Transparent;
            OuterShell.BorderBrush = Theme.Brush(Theme.IsGray
                ? Color.FromArgb(160, 184, 190, 198)
                : Color.FromArgb(160, 0, 110, 205));
            OuterShell.BorderThickness = new Thickness(1.0);
            OuterShell.CornerRadius = new CornerRadius(12);
            OuterShell.Effect = CreateToastShadow();

            ShadowPlate.Visibility = Visibility.Collapsed;
            ShadowPlate.Effect = null;

            EdgeRing.Background = System.Windows.Media.Brushes.Transparent;
            EdgeRing.Padding = new Thickness(0);
            EdgeRing.CornerRadius = new CornerRadius(RootCornerRadius);
            EdgeRing.BorderThickness = new Thickness(0);

            Root.CornerRadius = new CornerRadius(RootCornerRadius);

            ProgressGlow.BlurRadius = 8;
            ProgressGlow.Opacity = 0.8;

            ImageFrame.BorderBrush = Theme.Brush(Theme.IsDark
                ? Color.FromArgb(28, 255, 255, 255)
                : Color.FromArgb(18, 0, 0, 0));
            InlinePreviewHost.Background = Theme.Brush(Theme.IsDark
                ? Color.FromArgb(22, 255, 255, 255)
                : Color.FromArgb(12, 0, 0, 0));
            InlinePreviewHost.BorderBrush = Theme.Brush(Theme.IsDark
                ? Color.FromArgb(34, 255, 255, 255)
                : Color.FromArgb(20, 0, 0, 0));
            PreviewActionsDivider.Background = Theme.Brush(Theme.IsDark
                ? Color.FromArgb(36, 255, 255, 255)
                : Color.FromArgb(28, 0, 0, 0));
        }
        ImageFrame.BorderThickness = new Thickness(1);
    }

    internal bool TryUpdateInPlace(ToastSpec spec)
    {
        if (!IsLoaded || _isDragging)
            return false;

        CancelActiveToastState();
        _spec = spec;
        ApplySpec(spec);
        Opacity = 1;
        Root.Opacity = 1;
        OuterShell.Opacity = 1;
        SlideTransform.X = 0;
        SlideTransform.Y = 0;
        DragScale.ScaleX = 1;
        DragScale.ScaleY = 1;
        UpdateLayout();
        UpdateRootClip();
        ApplyPlacement(animateEntry: true, subtleEntry: false);

        double baseDuration = spec.PreviewBitmap is not null ? _durationSeconds : _systemDurationSeconds;
        _activeDurationSeconds = spec.DurationSeconds ?? baseDuration;

        _isHovered = IsMouseOver;
        if (_spec.ShowOverlayButtons && _isHovered)
            AnimateOverlayButtons(1, _isPinned ? 1 : 1);

        if (!_isPinned && !_isHovered)
            RestartVisibleTimer(_durationSeconds);

        return true;
    }

    private void ApplySpec(ToastSpec spec)
    {
        _isPinned = false;
        ConfigureShell();
        ProgressBar.Visibility = Visibility.Visible;
        // Capture the XAML gradient once, before any error/celebration swap, so we can restore it.
        // In the grayscale theme, swap the cyberpunk cyan/purple/magenta for a sober silver
        // sheen (and a white glow) so the timeline bar keeps its motion without leaking colour.
        if (_defaultProgressBrush is null)
        {
            if (Theme.IsGray)
            {
                _defaultProgressBrush = CreateSilverProgressBrush(loopable: false);
                ProgressBar.Background = _defaultProgressBrush;
                ProgressGlow.Color = Color.FromRgb(0xE8, 0xEC, 0xF0); // soft white-silver halo
            }
            else
            {
                _defaultProgressBrush = ProgressBar.Background;
            }
        }
        ApplyToastOverlayButtonVisual(PinBtn, PinIcon, "pin", active: false);

        _savedFilePath = spec.FilePath;
        _deleteFileOnDismiss = spec.DeleteFileOnDismiss
            && !string.IsNullOrWhiteSpace(spec.FilePath);

        var celebrating = spec.Celebrate && !spec.IsError;
        TitleText.Text = LocalizationService.Translate(spec.Title);
        SetBodyContent(LocalizationService.Translate(spec.Body), celebrating, spec.CelebrationBodyIconId);

        // Always center-align toast text notifications as requested by the user.
        TitleText.TextAlignment = TextAlignment.Center;
        BodyText.TextAlignment = TextAlignment.Center;


        TitleText.Visibility = string.IsNullOrWhiteSpace(spec.Title) ? Visibility.Collapsed : Visibility.Visible;
        BodyText.Visibility = string.IsNullOrWhiteSpace(spec.Body) ? Visibility.Collapsed : Visibility.Visible;
        TextContentPanel.Visibility = (TitleText.Visibility == Visibility.Collapsed && BodyText.Visibility == Visibility.Collapsed)
            ? Visibility.Collapsed
            : Visibility.Visible;
        RefreshToastContentAccessibility(spec);

        if (spec.SwatchColor.HasValue)
        {
            ColorSwatch.Background = Theme.Brush(spec.SwatchColor.Value);
            ColorSwatch.Visibility = Visibility.Visible;
        }
        else
        {
            ColorSwatch.Visibility = Visibility.Collapsed;
        }

        if (spec.InlinePreviewBitmap is not null)
        {
            _previewBitmap = spec.InlinePreviewBitmap;
            InlinePreviewHost.Visibility = Visibility.Visible;
            ConfigureInlinePreviewLayout(spec.InlinePreviewBitmap);
            InlinePreviewImage.Source = ToBitmapSource(spec.InlinePreviewBitmap);
        }
        else
        {
            InlinePreviewHost.Visibility = Visibility.Collapsed;
            InlinePreviewImage.Source = null;
        }

        if (spec.PreviewBitmap is not null)
        {
            _previewBitmap = spec.PreviewBitmap;
            ImageArea.Visibility = Visibility.Visible;
            ConfigureImagePreview(spec);
            UpdateVideoPlayBadge(spec.FilePath);
        }
        else
        {
            ImageArea.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
            if (VideoPlayBadge is not null)
                VideoPlayBadge.Visibility = Visibility.Collapsed;
            CloseBtn.Visibility = Visibility.Collapsed;
            PinBtn.Visibility = Visibility.Collapsed;
            SaveBtn.Visibility = Visibility.Collapsed;
        }

        if (spec.TransparentShell)
        {
            OuterShell.Background = System.Windows.Media.Brushes.Transparent;
            ShadowPlate.Visibility = Visibility.Collapsed;
            ShadowPlate.Effect = null;
            EdgeRing.Background = System.Windows.Media.Brushes.Transparent;
            EdgeRing.Padding = new Thickness(0);
        }

        if (spec.IsError)
        {
            var red = Color.FromRgb(239, 68, 68);
            var errorFill = Theme.IsDark ? Color.FromRgb(60, 28, 28) : Color.FromRgb(255, 240, 240);
            Root.Background = Theme.Brush(errorFill);
            ProgressBar.Background = Theme.Brush(Color.FromArgb(180, red.R, red.G, red.B));
            TitleText.Foreground = Theme.Brush(red);

            if (IsDefaultDarkTheme())
            {
                ShadowPlate.Background = Theme.Brush(errorFill);
                EdgeRing.Background = Theme.Brush(Color.FromRgb(red.R, red.G, red.B));
                EdgeRing.Padding = new Thickness(EdgeRingStrokeWidth);
                OuterShell.BorderThickness = new Thickness(0);
            }
            else
            {
                OuterShell.BorderBrush = Theme.Brush(Color.FromArgb(160, red.R, red.G, red.B));
                OuterShell.BorderThickness = new Thickness(1.0);
            }
        }

        ApplyCelebrationVisual(spec.Celebrate && !spec.IsError);

        RefreshInteractiveTooltip(spec);

        if (spec.AutoPin)
            ApplyPinnedState(true);

        HookOverlayButtons();
        RefreshOverlayButtonLayout();
    }

    private void ConfigureImagePreview(ToastSpec spec)
    {
        var preview = spec.PreviewBitmap!;
        bool imageOnly = TitleText.Visibility == Visibility.Collapsed &&
                         BodyText.Visibility == Visibility.Collapsed &&
                         TextContentPanel.Visibility == Visibility.Collapsed;
        bool fallbackFramed = false;

        double aspect = preview.Height <= 0 ? 1d : preview.Width / (double)preview.Height;

        int toastW;
        int toastH;
        var previewStretch = spec.PreviewStretch;
        if (imageOnly)
        {
            var imageOnlyLayout = ComputeImageOnlyPreviewLayout(preview.Width, preview.Height);
            fallbackFramed = imageOnlyLayout.Framed;
            toastW = imageOnlyLayout.Width;
            toastH = imageOnlyLayout.Height;

            Root.MinWidth = toastW;
            Root.MaxWidth = toastW;
            ImageArea.Width = toastW;
            ImageArea.Height = toastH;
            ImageArea.MaxHeight = toastH;
            System.Windows.Controls.Grid.SetRowSpan(ImageArea, 1);
            Root.Background = Theme.Brush(Theme.ToastBg);
            ImageFrame.Background = Theme.Brush(Theme.ToastBg);
            double topR = IsDefaultDarkTheme() ? InnerCornerRadius : RootCornerRadius;
            ImageFrame.CornerRadius = new CornerRadius(topR, topR, 0, 0);
            ImageFrame.BorderThickness = new Thickness(0);
        }
        else
        {
            toastW = spec.MaxWidthOverride ?? (int)Math.Clamp(180 * aspect, 200, 340);
            toastH = spec.PreviewMaxHeight is double maxH
                ? (int)maxH
                : (int)Math.Clamp(toastW / Math.Max(0.35, aspect), 80, 200);
            Root.MaxWidth = toastW;
            Root.MinWidth = spec.MinWidthOverride ?? Math.Min(200, toastW);
            ImageArea.Width = double.NaN;
            ImageArea.Height = double.NaN;
            ImageArea.MaxHeight = toastH;
            System.Windows.Controls.Grid.SetRowSpan(ImageArea, 1);
            Root.Background = Theme.Brush(Theme.ToastBg);
            ImageFrame.Background = Theme.Brush(Theme.ToastBg);
            double topR = IsDefaultDarkTheme() ? InnerCornerRadius : RootCornerRadius;
            ImageFrame.CornerRadius = new CornerRadius(topR, topR, 0, 0);
            ImageFrame.BorderThickness = new Thickness(1);
        }

        PreviewImage.Stretch = previewStretch;
        PreviewImage.Margin = imageOnly
            ? (fallbackFramed ? new Thickness(0) : new Thickness(-1))
            : spec.PreviewMargin;
        PreviewImage.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        PreviewImage.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        PreviewImage.Source = ToBitmapSource(preview);

        RefreshOverlayButtonLayout();
    }

    private void ConfigureInlinePreviewLayout(Bitmap preview)
    {
        var aspect = preview.Height <= 0 ? 1d : preview.Width / (double)preview.Height;
        if (aspect >= 1.8)
        {
            var width = Math.Clamp(preview.Width / 3d, 72d, 112d);
            InlinePreviewHost.Width = width;
            InlinePreviewHost.Height = 40;
            InlinePreviewImage.Margin = new Thickness(6, 8, 6, 8);
        }
        else
        {
            InlinePreviewHost.Width = 44;
            InlinePreviewHost.Height = 44;
            InlinePreviewImage.Margin = new Thickness(4);
        }
    }

    private static readonly System.Drawing.Color IconWhite = System.Drawing.Color.FromArgb(230, 255, 255, 255);

    private void LoadOverlayIcons()
    {
        CloseIcon.Source = FluentIcons.RenderWpf("close", IconWhite, 20);
        PinIcon.Source = FluentIcons.RenderWpf("pin", IconWhite, 20);
        SaveIcon.Source = FluentIcons.RenderWpf("download", IconWhite, 20);
        CopyIcon.Source = FluentIcons.RenderWpf("copy", IconWhite, 20);
        ShareIcon.Source = FluentIcons.RenderWpf("share", IconWhite, 20);
        DeleteIcon.Source = FluentIcons.RenderWpf("trash", IconWhite, 20);
        EditIcon.Source = FluentIcons.RenderWpf("draw", IconWhite, 20);
        ApplyToastOverlayButtonVisual(CloseBtn, CloseIcon, "close", active: false);
        ApplyToastOverlayButtonVisual(PinBtn, PinIcon, "pin", active: false);
        ApplyToastOverlayButtonVisual(SaveBtn, SaveIcon, "download", active: false);
        ApplyToastOverlayButtonVisual(CopyBtn, CopyIcon, "copy", active: false);
        ApplyToastOverlayButtonVisual(ShareBtn, ShareIcon, "share", active: false);
        ApplyToastOverlayButtonVisual(DeleteBtn, DeleteIcon, "trash", active: false);
        ApplyToastOverlayButtonVisual(HistoryBtn, HistoryIcon, "history", active: false);
        ApplyToastOverlayButtonVisual(EditBtn, EditIcon, "draw", active: false);
        ApplyTextCloseVisual(active: false);

        HookOverlayHover(CloseBtn, CloseIcon, "close");
        HookOverlayHover(PinBtn, PinIcon, "pin");
        HookOverlayHover(SaveBtn, SaveIcon, "download");
        HookOverlayHover(CopyBtn, CopyIcon, "copy");
        HookOverlayHover(ShareBtn, ShareIcon, "share");
        HookOverlayHover(DeleteBtn, DeleteIcon, "trash");
        HookOverlayHover(HistoryBtn, HistoryIcon, "history");
        HookOverlayHover(EditBtn, EditIcon, "draw");
        TextCloseBtn.MouseEnter += (_, _) => ApplyTextCloseVisual(active: true);
        TextCloseBtn.MouseLeave += (_, _) => ApplyTextCloseVisual(active: false);
    }

    private void ApplyTextCloseVisual(bool active)
    {
        var iconColor = Theme.IsDark
            ? System.Drawing.Color.FromArgb(active ? 255 : 200, 255, 255, 255)
            : System.Drawing.Color.FromArgb(active ? 255 : 180, 24, 24, 24);
        TextCloseIcon.Source = FluentIcons.RenderWpf("close", iconColor, 14);
        TextCloseIcon.Opacity = active ? 1.0 : 0.78;
        TextCloseBtn.Background = active
            ? Theme.Brush(Theme.IsDark ? Color.FromArgb(48, 255, 255, 255) : Color.FromArgb(38, 0, 0, 0))
            : System.Windows.Media.Brushes.Transparent;
    }

    private void HookOverlayHover(System.Windows.Controls.Border btn, System.Windows.Controls.Image icon, string iconId)
    {
        btn.MouseEnter += (_, _) =>
        {
            if (iconId == "pin" && _isPinned) return;
            ApplyToastOverlayButtonVisual(btn, icon, iconId, active: true);
        };
        btn.MouseLeave += (_, _) =>
        {
            if (iconId == "pin" && _isPinned) return;
            ApplyToastOverlayButtonVisual(btn, icon, iconId, active: false);
        };
    }

    // Default Dark only: Settings chrome (BgSecondary/BorderSubtle, hover lift). Grayscale/Light keep prior fills.
    private static void ApplyToastOverlayButtonVisual(System.Windows.Controls.Border btn, System.Windows.Controls.Image icon, string iconId, bool active)
    {
        if (IsDefaultDarkTheme())
        {
            btn.Background = Theme.Brush(active ? Theme.BgHover : Theme.BgSecondary);
            btn.BorderBrush = Theme.Brush(active ? Theme.Border : Theme.BorderSubtle);
            var iconTone = active ? Theme.Accent : Theme.TextPrimary;
            var iconColor = System.Drawing.Color.FromArgb(iconTone.A, iconTone.R, iconTone.G, iconTone.B);
            btn.BorderThickness = new Thickness(1);
            icon.Source = FluentIcons.RenderWpf(iconId, iconColor, 22, active);
            return;
        }

        if (Theme.IsGray)
        {
            btn.Background = Theme.Brush(active
                ? Color.FromArgb(235, 46, 50, 55)
                : Color.FromArgb(215, 29, 32, 35));
            btn.BorderBrush = Theme.Brush(active
                ? Color.FromArgb(255, 184, 190, 198)
                : Color.FromArgb(130, 150, 156, 164));
        }
        else
        {
            btn.Background = Theme.Brush(active
                ? Color.FromArgb(235, 180, 225, 255)
                : Color.FromArgb(210, 235, 246, 253));
            btn.BorderBrush = Theme.Brush(active
                ? Color.FromArgb(255, 0, 120, 215)
                : Color.FromArgb(130, 0, 120, 215));
        }
        btn.BorderThickness = new Thickness(1);

        var legacyIcon = Theme.IsGray
            ? (active ? System.Drawing.Color.FromArgb(255, 184, 190, 198) : System.Drawing.Color.FromArgb(235, 255, 255, 255))
            : (active ? System.Drawing.Color.FromArgb(255, 0, 90, 180) : System.Drawing.Color.FromArgb(235, 24, 24, 24));
        icon.Source = FluentIcons.RenderWpf(iconId, legacyIcon, 22, active);
    }

    private void HookOverlayButtons()
    {
        CloseBtn.MouseLeftButtonDown -= CloseBtn_MouseLeftButtonDown;
        PinBtn.MouseLeftButtonDown -= PinBtn_MouseLeftButtonDown;
        SaveBtn.MouseLeftButtonDown -= SaveBtn_MouseLeftButtonDown;
        CopyBtn.MouseLeftButtonDown -= CopyBtn_MouseLeftButtonDown;
        ShareBtn.MouseLeftButtonDown -= ShareBtn_MouseLeftButtonDown;
        DeleteBtn.MouseLeftButtonDown -= DeleteBtn_MouseLeftButtonDown;
        HistoryBtn.MouseLeftButtonDown -= HistoryBtn_MouseLeftButtonDown;
        EditBtn.MouseLeftButtonDown -= EditBtn_MouseLeftButtonDown;
        TextCloseBtn.MouseLeftButtonDown -= CloseBtn_MouseLeftButtonDown;

        // Text-only toasts (no preview bitmap) always get an X — independent of ShowOverlayButtons.
        TextCloseBtn.MouseLeftButtonDown += CloseBtn_MouseLeftButtonDown;

        if (_previewBitmap is null || !_spec.ShowOverlayButtons)
            return;

        CloseBtn.MouseLeftButtonDown += CloseBtn_MouseLeftButtonDown;
        PinBtn.MouseLeftButtonDown += PinBtn_MouseLeftButtonDown;
        SaveBtn.MouseLeftButtonDown += SaveBtn_MouseLeftButtonDown;
        CopyBtn.MouseLeftButtonDown += CopyBtn_MouseLeftButtonDown;
        ShareBtn.MouseLeftButtonDown += ShareBtn_MouseLeftButtonDown;
        DeleteBtn.MouseLeftButtonDown += DeleteBtn_MouseLeftButtonDown;
        HistoryBtn.MouseLeftButtonDown += HistoryBtn_MouseLeftButtonDown;
        EditBtn.MouseLeftButtonDown += EditBtn_MouseLeftButtonDown;
    }

    internal void RefreshOverlayButtonLayout()
    {
        ApplyOverlayButton(CloseBtn, Helpers.ToastButtonKind.Close);
        ApplyOverlayButton(PinBtn, Helpers.ToastButtonKind.Pin);
        ApplyOverlayButton(SaveBtn, Helpers.ToastButtonKind.Save);
        ApplyOverlayButton(CopyBtn, Helpers.ToastButtonKind.Copy);
        ApplyOverlayButton(ShareBtn, Helpers.ToastButtonKind.Share);
        ApplyOverlayButton(DeleteBtn, Helpers.ToastButtonKind.Delete);
        ApplyOverlayButton(HistoryBtn, Helpers.ToastButtonKind.History);
        ApplyOverlayButton(EditBtn, Helpers.ToastButtonKind.Edit);

        bool anyActionBtnVisible = CloseBtn.Visibility == Visibility.Visible ||
                                   PinBtn.Visibility == Visibility.Visible ||
                                   SaveBtn.Visibility == Visibility.Visible ||
                                   CopyBtn.Visibility == Visibility.Visible ||
                                   ShareBtn.Visibility == Visibility.Visible ||
                                   DeleteBtn.Visibility == Visibility.Visible ||
                                   HistoryBtn.Visibility == Visibility.Visible ||
                                   EditBtn.Visibility == Visibility.Visible;
        ActionsHost.Visibility = anyActionBtnVisible ? Visibility.Visible : Visibility.Collapsed;
        RefreshActionsPanelMetrics();

        // Every text-only toast gets an X — Scan/Error/Color/Standard alike.
        bool textCloseVisible = _previewBitmap is null &&
                                Helpers.ToastButtonLayout.IsVisible(_buttonLayout, Helpers.ToastButtonKind.Close) &&
                                TextContentPanel.Visibility == Visibility.Visible;
        SetToastElementAccessibility(TextCloseBtn, LocalizationService.Translate("Close notification"), LocalizationService.Translate("Close this notification."));
        TextCloseBtn.Visibility = textCloseVisible ? Visibility.Visible : Visibility.Collapsed;
        if (textCloseVisible)
            ApplyTextCloseVisual(active: false);
    }

    private void ApplyOverlayButton(System.Windows.Controls.Border button, Helpers.ToastButtonKind kind)
    {
        RefreshOverlayButtonAccessibility(button, kind);
        // Keep Delete in the layout even when there is no file (notification-only captures)
        // so the action strip doesn't jump; disable it instead of hiding.
        bool visible = _previewBitmap is not null &&
                       _spec.ShowOverlayButtons &&
                       Helpers.ToastButtonLayout.IsVisible(_buttonLayout, kind);

        button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        // Hide the Edit button for non-image captures (e.g. MP4/GIF recordings)
        if (kind == Helpers.ToastButtonKind.Edit && _spec.HideEditButton)
            button.Visibility = Visibility.Collapsed;
        if (button.Visibility != Visibility.Visible)
            return;

        var (row, col) = Helpers.ToastButtonLayout.ToGridCell(Helpers.ToastButtonLayout.GetSlot(_buttonLayout, kind));
        System.Windows.Controls.Grid.SetRow(button, row);
        System.Windows.Controls.Grid.SetColumn(button, col);
        button.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        button.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        button.Margin = new Thickness(4);

        if (kind == Helpers.ToastButtonKind.Delete)
            ApplyDeleteButtonEnabledState();
    }

    /// <summary>
    /// Delete stays visible for layout consistency; enabled only when a real file exists on disk.
    /// </summary>
    private void ApplyDeleteButtonEnabledState()
    {
        bool canDelete = HasSavedFileOnDisk() && !_isDeletingSavedFile;
        DeleteBtn.IsEnabled = canDelete;
        DeleteBtn.Opacity = canDelete ? 1.0 : 0.38;
        DeleteBtn.Cursor = canDelete
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        RefreshOverlayButtonAccessibility(DeleteBtn, Helpers.ToastButtonKind.Delete);
    }

    private void RefreshActionsPanelMetrics()
    {
        int maxRow = 0;
        foreach (var button in new[] { CloseBtn, PinBtn, SaveBtn, CopyBtn, ShareBtn, DeleteBtn, HistoryBtn, EditBtn })
        {
            if (button.Visibility != Visibility.Visible)
                continue;

            maxRow = Math.Max(maxRow, System.Windows.Controls.Grid.GetRow(button) + 1);
        }

        const double rowHeight = 40;
        ActionsPanel.RowDefinitions[0].Height = maxRow >= 1 ? new GridLength(rowHeight) : new GridLength(0);
        ActionsPanel.RowDefinitions[1].Height = maxRow >= 2 ? new GridLength(rowHeight) : new GridLength(0);
        ActionsPanel.Height = maxRow * rowHeight;

        bool showDivider = maxRow > 0 &&
                           (ImageArea.Visibility == Visibility.Visible || TextContentPanel.Visibility == Visibility.Visible);
        PreviewActionsDivider.Visibility = showDivider ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshToastContentAccessibility(ToastSpec spec)
    {
        var title = TitleText.Text ?? "";
        TitleText.ToolTip = title;
        AutomationProperties.SetName(TitleText, "Toast title");
        AutomationProperties.SetHelpText(TitleText, title);

        var body = BodyText.Text ?? "";
        BodyText.ToolTip = body;
        AutomationProperties.SetName(BodyText, "Toast message");
        AutomationProperties.SetHelpText(BodyText, body);

        if (spec.SwatchColor.HasValue)
            SetToastElementAccessibility(ColorSwatch, "Toast color swatch", string.IsNullOrWhiteSpace(body) ? "Color preview." : body);

        if (spec.InlinePreviewBitmap is not null)
            SetToastElementAccessibility(InlinePreviewHost, "Inline toast preview", "Preview image shown inside this notification.");

        if (spec.PreviewBitmap is not null)
        {
            // Tooltip/help for the image surface is owned by RefreshInteractiveTooltip so the
            // configured body-click action and file name stay in one place (no competing tips).
            var previewHelp = BuildPreviewBodyTooltip(spec) ?? "Notification preview image";
            SetToastElementAccessibility(PreviewImage, "Notification preview image", previewHelp);
        }
    }

    private void RefreshOverlayButtonAccessibility(System.Windows.Controls.Border button, Helpers.ToastButtonKind kind)
    {
        var (name, helpText) = kind switch
        {
            Helpers.ToastButtonKind.Close => (T("Close preview"), T("Close this preview.")),
            Helpers.ToastButtonKind.Pin => _isPinned
                ? (T("Unpin preview"), T("Allow this preview to dismiss automatically."))
                : (T("Pin preview"), T("Keep this preview open.")),
            Helpers.ToastButtonKind.Save => _isSavingPreview
                ? (T("Saving preview"), T("Save is already running."))
                : GetSaveButtonAccessibility(),
            Helpers.ToastButtonKind.Copy => (T("Copy to clipboard"), T("Copy capture to the clipboard.")),
            Helpers.ToastButtonKind.Share => _isRunningShareAction
                ? (T("Sharing capture"), T("Share upload is already running."))
                : (T("Share"), T("Upload and copy a shareable link.")),
            Helpers.ToastButtonKind.Edit => (T("Edit preview"), T("Open this capture in the Annotations Editor.")),
            Helpers.ToastButtonKind.Delete => _isDeletingSavedFile
                ? (T("Deleting file"), T("Delete is already running."))
                : HasSavedFileOnDisk()
                    ? (T("Delete capture"), T("Delete capture"))
                    : (T("Delete capture"), T("No file to delete — capture was not saved to disk.")),
            Helpers.ToastButtonKind.History => (T("Open capture in Gallery"), T("Open capture in Gallery.")),
            _ => (T("Notification action"), T("Run this notification action."))
        };
        SetToastElementAccessibility(button, name, helpText);
    }

    private static string T(string text) => LocalizationService.Translate(text);

    private (string name, string helpText) GetSaveButtonAccessibility()
    {
        bool isGif = _savedFilePath != null &&
            _savedFilePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
        bool isMp4 = _savedFilePath != null &&
            _savedFilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

        var saveLabel = T("Save capture as...");
        if (isGif) return (saveLabel, T("Save a copy of this GIF"));
        if (isMp4) return (saveLabel, T("Save a copy of this MP4"));
        return (saveLabel, saveLabel);
    }

    private static void SetToastElementAccessibility(FrameworkElement element, string name, string helpText)
    {
        element.ToolTip = helpText;
        AutomationProperties.SetName(element, name);
        AutomationProperties.SetHelpText(element, helpText);
    }

    private void CloseBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanActivateMouseControl(sender) || InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        CloseToast();
    }

    private void CloseBtn_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!CanActivateKeyboardControl(sender, e) || InteractiveActionsBlocked)
            return;

        e.Handled = true;
        CloseToast();
    }

    private void PinBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanActivateMouseControl(sender) || InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        TogglePinned();
    }

    private void PinBtn_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!CanActivateKeyboardControl(sender, e) || InteractiveActionsBlocked)
            return;

        e.Handled = true;
        TogglePinned();
    }

    private void SaveBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanActivateMouseControl(sender) || InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        SavePreview();
    }

    private void CopyBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanActivateMouseControl(sender) || InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        CopyPreview();
    }

    private void CopyBtn_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!CanActivateKeyboardControl(sender, e) || InteractiveActionsBlocked)
            return;

        e.Handled = true;
        CopyPreview();
    }

    private void HistoryBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanActivateMouseControl(sender) || InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        OpenHistory();
    }

    private void HistoryBtn_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!CanActivateKeyboardControl(sender, e) || InteractiveActionsBlocked)
            return;

        e.Handled = true;
        OpenHistory();
    }

    private void EditBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanActivateMouseControl(sender) || InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        OpenEditor();
    }

    private void EditBtn_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!CanActivateKeyboardControl(sender, e) || InteractiveActionsBlocked)
            return;

        e.Handled = true;
        OpenEditor();
    }

    private void OpenEditor()
    {
        if (InteractiveActionsBlocked)
            return;

        if (_previewBitmap is null && _savedFilePath is null)
            return;

        try
        {
            if (IsVideoOrGifPath(_savedFilePath))
            {
                var trimmer = new VideoTrimmerWindow(_savedFilePath!, ((App)Application.Current).SettingsService);
                trimmer.Show();
            }
            else if (_savedFilePath is not null && _savedFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                OpenWithDefaultViewer();
            }
            else if (_previewBitmap is not null)
            {
                CyberSnap.UI.Editor.EditorForm.ShowEditor(new Bitmap(_previewBitmap), _savedFilePath);
            }
            else if (HasSavedFileOnDisk())
            {
                CyberSnap.UI.Editor.EditorForm.ShowEditorFromFile(_savedFilePath!);
            }
            else if (!string.IsNullOrWhiteSpace(_savedFilePath))
            {
                ShowSavedFileMissingError();
                return;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("toast.open-editor", ex.Message, ex);
        }

        DismissAnimated();
    }

    private void OpenHistory()
    {
        if (InteractiveActionsBlocked)
            return;

        ((App)Application.Current).ShowHistory(_savedFilePath);
        DismissAnimated();
    }

    private void SaveBtn_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!CanActivateKeyboardControl(sender, e) || InteractiveActionsBlocked)
            return;

        e.Handled = true;
        SavePreview();
    }

    private static bool IsKeyboardActivateKey(System.Windows.Input.KeyEventArgs e) =>
        e.Key is Key.Enter or Key.Space;

    private static bool CanActivateKeyboardControl(object sender, System.Windows.Input.KeyEventArgs e) =>
        IsKeyboardActivateKey(e) && sender is not UIElement { IsEnabled: false };

    private static bool CanActivateMouseControl(object sender) =>
        sender is not UIElement { IsEnabled: false };

    /// <summary>Settings layout-test notifications show buttons but must not run real actions.</summary>
    private bool InteractiveActionsBlocked => _spec.DisableInteractiveActions;

    /// <summary>User-initiated close (X button or click-action Close): dismiss immediately, no fade.</summary>
    private void CloseToast() => RequestDismiss(force: true);

    private void TogglePinned() => ApplyPinnedState(!_isPinned);

    private void SavePreview()
    {
        if (InteractiveActionsBlocked || _previewBitmap is null || _isSavingPreview)
            return;

        _isSavingPreview = true;
        SaveBtn.IsEnabled = false;
        RefreshOverlayButtonAccessibility(SaveBtn, Helpers.ToastButtonKind.Save);
        var wasPinnedBeforeSave = _isPinned;
        var remainingAutoDismissSeconds = PauseToastAutoDismiss();
        try
        {
            ApplyPinnedState(true);
            RegionOverlayForm.CloseTransientUi();

            // Determine if this is a GIF or MP4 recording based on the saved file path.
            bool isGif = _savedFilePath != null &&
                _savedFilePath.EndsWith(".gif", StringComparison.OrdinalIgnoreCase);
            bool isMp4 = _savedFilePath != null &&
                _savedFilePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
            bool isRecording = isGif || isMp4;

            string filter;
            string defaultExt;
            if (isGif)
            {
                filter = "GIF|*.gif";
                defaultExt = ".gif";
            }
            else if (isMp4)
            {
                filter = "MP4 Video|*.mp4";
                defaultExt = ".mp4";
            }
            else
            {
                filter = "PNG|*.png|JPEG|*.jpg|BMP|*.bmp";
                defaultExt = ".png";
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                FileName = _savedFilePath != null ? Path.GetFileName(_savedFilePath) : "screenshot.png",
                Filter = filter,
                DefaultExt = defaultExt
            };
            if (dlg.ShowDialog(this) != true)
            {
                if (!wasPinnedBeforeSave)
                    ResumeToastAutoDismiss(remainingAutoDismissSeconds);
                return;
            }

            if (isRecording)
            {
                // For GIF/MP4 recordings, copy the original file to preserve animation.
                try
                {
                    File.Copy(_savedFilePath!, dlg.FileName, true);
                    Show(ToastSpec.Standard("Saved", Path.GetFileName(dlg.FileName)));
                }
                catch (Exception ex)
                {
                    Show(ToastSpec.Error(
                        "Save failed",
                        BuildToastActionFailureBody("CyberSnap could not save the recording. Choose another folder or check write permissions.", ex.Message),
                        GetExistingSavedFilePathOrNull()));
                }
            }
            else
            {
                var format = dlg.FilterIndex switch
                {
                    2 => Models.CaptureImageFormat.Jpeg,
                    3 => Models.CaptureImageFormat.Bmp,
                    _ => Models.CaptureImageFormat.Png
                };

                try
                {
                    CaptureOutputService.SaveBitmap(_previewBitmap, dlg.FileName, format, jpegQuality: 92);
                    Show(ToastSpec.Standard("Saved", Path.GetFileName(dlg.FileName)));
                }
                catch (Exception ex)
                {
                    Show(ToastSpec.Error(
                        "Save failed",
                        BuildToastActionFailureBody("CyberSnap could not save the preview. Choose another folder or check write permissions.", ex.Message),
                        GetExistingSavedFilePathOrNull()));
                }
            }
        }
        finally
        {
            _isSavingPreview = false;
            SaveBtn.IsEnabled = true;
            RefreshOverlayButtonAccessibility(SaveBtn, Helpers.ToastButtonKind.Save);
        }
    }

    private void CopyPreview()
    {
        if (InteractiveActionsBlocked || _previewBitmap is null)
            return;

        try
        {
            ClipboardService.CopyToClipboard(_previewBitmap, _savedFilePath);
            Show(ToastSpec.Standard(Services.LocalizationService.Translate("Copied"), ""));
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("toast.copy-preview", ex.Message, ex);
            Show(ToastSpec.Error(
                "Copy failed",
                BuildToastActionFailureBody("CyberSnap could not copy the preview to the clipboard.", ex.Message),
                GetExistingSavedFilePathOrNull()));
        }
    }

    private double PauseToastAutoDismiss()
    {
        _timer.Stop();
        var progress = Math.Clamp(ProgressScale.ScaleX, 0, 1);
        var remaining = GetToastAutoDismissRemainingSeconds(progress, _activeDurationSeconds);
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressScale.ScaleX = progress;
        return remaining;
    }

    private void ResumeToastAutoDismiss(double remainingSeconds)
    {
        _isPinned = false;
        ProgressBar.Visibility = Visibility.Visible;
        ApplyToastOverlayButtonVisual(PinBtn, PinIcon, "pin", active: false);
        RefreshOverlayButtonAccessibility(PinBtn, Helpers.ToastButtonKind.Pin);
        if (_isHovered)
            return;

        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation { To = 0, Duration = Motion.Sec(remainingSeconds) });
        _timer.Interval = TimeSpan.FromSeconds(remainingSeconds);
        _timer.Start();
    }

    private static double GetToastAutoDismissRemainingSeconds(double progressScale, double durationSeconds) =>
        Math.Max(0.1, Math.Clamp(progressScale, 0, 1) * durationSeconds);

    private void ShareBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanActivateMouseControl(sender) || InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        SharePreview();
    }

    private void ShareBtn_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!CanActivateKeyboardControl(sender, e) || InteractiveActionsBlocked)
            return;

        e.Handled = true;
        SharePreview();
    }

    private async void SharePreview()
    {
        if (InteractiveActionsBlocked || _previewBitmap is null || _isRunningShareAction)
            return;

        var wasPinnedBeforeShare = _isPinned;
        var remainingAutoDismissSeconds = PauseToastAutoDismiss();
        ApplyPinnedState(true);
        RegionOverlayForm.CloseTransientUi();

        _isRunningShareAction = true;
        ShareBtn.IsEnabled = false;
        RefreshOverlayButtonAccessibility(ShareBtn, Helpers.ToastButtonKind.Share);

        try
        {
            var settings = SettingsService.LoadStatic() ?? new AppSettings();
            var provider = Services.Upload.ImageUploadService.GetDefaultProvider(settings);
            
            // Confirm upload if needed
            if (!Share.ImageShareFlow.ConfirmThirdPartyUploadIfNeeded(this, new System.Windows.Interop.WindowInteropHelper(this).Handle, provider, settings))
            {
                if (!wasPinnedBeforeShare)
                    ResumeToastAutoDismiss(remainingAutoDismissSeconds);
                return;
            }

            var result = await Share.ImageShareFlow.ShareBitmapAsync(_previewBitmap).ConfigureAwait(true);
            Share.ImageShareFlow.PresentResult(result);
            DismissAnimated();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("toast.share-preview", ex.Message, ex);
            Show(ToastSpec.Error(
                LocalizationService.Translate("Upload failed"),
                BuildToastActionFailureBody(LocalizationService.Translate("CyberSnap could not share the capture. Check your network or upload configuration in Settings."), ex.Message),
                GetExistingSavedFilePathOrNull()));
        }
        finally
        {
            _isRunningShareAction = false;
            ShareBtn.IsEnabled = true;
            RefreshOverlayButtonAccessibility(ShareBtn, Helpers.ToastButtonKind.Share);
        }
    }

    private bool HasSavedFileOnDisk()
        => !string.IsNullOrWhiteSpace(_savedFilePath) && File.Exists(_savedFilePath);

    private string? GetExistingSavedFilePathOrNull()
        => HasSavedFileOnDisk() ? _savedFilePath : null;

    private static string BuildToastActionFailureBody(string recoveryMessage, string details)
        => string.IsNullOrWhiteSpace(details) ? recoveryMessage : $"{recoveryMessage}\n{details}";

    private static bool SavedFilePathStillExists(string filePath)
        => !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);

    private bool IsCurrentToastState(int stateVersion)
        => _current == this && _toastStateVersion == stateVersion;

    private void ShowSavedFileMissingError(string? filePath = null)
    {
        Show(ToastSpec.Error(LocalizationService.Translate("File missing"), LocalizationService.Translate("The selected file is no longer on the original location."), filePath ?? _savedFilePath));
    }

    private static bool OpenExternalUrl(string url, string? filePath = null)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Show(ToastSpec.Error("Open failed", "No link is available.", filePath));
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("toast.external-url-open", $"Failed to open external URL: {ex.Message}", ex);
            Show(ToastSpec.Error(
                "Open failed",
                $"CyberSnap could not open the link. Try again from the toast, or open the link manually if it is still visible.\n{ex.Message}",
                filePath));
            return false;
        }
    }

    private void RefreshInteractiveTooltip(ToastSpec spec)
    {
        // One tooltip for the whole toast surface (window + preview image) so the body-click
        // action and file name never fight each other depending on where the cursor sits.
        var tip = BuildPreviewBodyTooltip(spec);
        ToolTip = tip;
        if (ImageArea.Visibility == Visibility.Visible)
            PreviewImage.ToolTip = tip;
    }

    /// <summary>
    /// Body-click action line, optionally with the capture file name on a second line.
    /// Used for both the window tooltip and the preview image (which fills most of the toast).
    /// </summary>
    private static string? BuildPreviewBodyTooltip(ToastSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.ClickActionLabel))
            return LocalizationService.Translate(spec.ClickActionLabel);

        // Non-preview toasts (system messages, etc.) leave tooltip empty unless a custom label was set.
        if (spec.PreviewBitmap is null)
            return null;

        ToastPreviewClickAction action;
        try
        {
            action = SettingsService.LoadStatic()?.ToastPreviewClickAction
                ?? ToastPreviewClickAction.OpenInEditor;
        }
        catch
        {
            action = ToastPreviewClickAction.OpenInEditor;
        }

        bool isPdf = !string.IsNullOrWhiteSpace(spec.FilePath) && spec.FilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        string tipKey = isPdf
            ? "Click to open in default viewer"
            : action switch
            {
                ToastPreviewClickAction.OpenInDefaultViewer => "Click to open in default viewer",
                ToastPreviewClickAction.CopyToClipboard => "Click to copy",
                ToastPreviewClickAction.Save => "Click to save",
                ToastPreviewClickAction.OpenInGallery => "Click to open in Gallery",
                ToastPreviewClickAction.Close => "Click to close",
                _ => "Click to open in editor"
            };
        string actionLine = LocalizationService.Translate(tipKey);

        if (string.IsNullOrWhiteSpace(spec.FilePath))
            return actionLine;

        string fileName = Path.GetFileName(spec.FilePath);
        return string.IsNullOrWhiteSpace(fileName)
            ? actionLine
            : $"{actionLine}\n{fileName}";
    }

    private void DeleteBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanActivateMouseControl(sender) || InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        DeleteSavedFile();
    }

    private void DeleteBtn_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!CanActivateKeyboardControl(sender, e) || InteractiveActionsBlocked)
            return;

        e.Handled = true;
        DeleteSavedFile();
    }

    private void DeleteSavedFile()
    {
        if (InteractiveActionsBlocked || _isDeletingSavedFile)
            return;

        if (!HasSavedFileOnDisk())
        {
            // Button is visible-but-disabled for notification-only captures; ignore stray clicks.
            return;
        }

        _isDeletingSavedFile = true;
        ApplyDeleteButtonEnabledState();
        var deletePath = _savedFilePath!;
        try
        {
            if (!SavedFilePathStillExists(deletePath))
            {
                _isDeletingSavedFile = false;
                ApplyDeleteButtonEnabledState();
                ShowSavedFileMissingError(deletePath);
                return;
            }

            if (!HistoryService.TryDeleteSavedCapture(deletePath))
                throw new IOException($"Could not delete {deletePath}");

            _savedFilePath = null;
            _deleteFileOnDismiss = false; // already deleted
            _isDeletingSavedFile = false;
            ApplyDeleteButtonEnabledState();
            DismissAnimated();
            Show(ToastSpec.Standard("Deleted", Path.GetFileName(deletePath) ?? deletePath));
        }
        catch (Exception ex)
        {
            _isDeletingSavedFile = false;
            ApplyDeleteButtonEnabledState();
            Show(ToastSpec.Error(
                "Delete failed",
                BuildToastActionFailureBody("CyberSnap could not delete the saved file. Open it from History or delete it manually in File Explorer.", ex.Message),
                GetExistingSavedFilePathOrNull()));
        }
    }

    private void ApplyPinnedState(bool pinned)
    {
        _isPinned = pinned;
        if (_isPinned)
        {
            _timer.Stop();
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressBar.Visibility = Visibility.Collapsed;
            ApplyToastOverlayButtonVisual(PinBtn, PinIcon, "pin", active: true);
            RefreshOverlayButtonAccessibility(PinBtn, Helpers.ToastButtonKind.Pin);
            PinBtn.Opacity = 1;
            return;
        }

        ProgressBar.Visibility = Visibility.Visible;
        ProgressScale.ScaleX = 1;
        if (_isHovered)
        {
            ApplyToastOverlayButtonVisual(PinBtn, PinIcon, "pin", active: false);
            RefreshOverlayButtonAccessibility(PinBtn, Helpers.ToastButtonKind.Pin);
            return;
        }

        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation { To = 0, Duration = Motion.Sec(_activeDurationSeconds) });
        _timer.Interval = TimeSpan.FromSeconds(_activeDurationSeconds);
        _timer.Start();
        ApplyToastOverlayButtonVisual(PinBtn, PinIcon, "pin", active: false);
        RefreshOverlayButtonAccessibility(PinBtn, Helpers.ToastButtonKind.Pin);
    }

    private void AnimateOverlayButtons(double targetOpacity, double pinnedOpacity)
    {
        CloseBtn.Opacity = 1;
        SaveBtn.Opacity = 1;
        CopyBtn.Opacity = 1;
        ShareBtn.Opacity = 1;
        DeleteBtn.Opacity = 1;
        HistoryBtn.Opacity = 1;
        EditBtn.Opacity = 1;
        PinBtn.Opacity = 1;
    }

    private void UpdateRootClip()
    {
        if (Root.ActualWidth <= 0 || Root.ActualHeight <= 0)
            return;

        // Default Dark: clip fill/content to the INNER radius. EdgeRing's solid padding ring
        // (outer radius) is outside Root, so the progress rail is clipped by this geometry and
        // follows the same arcs instead of painting square ends across the curve.
        if (IsDefaultDarkTheme())
        {
            double r = InnerCornerRadius;
            var geo = new RectangleGeometry(
                new Rect(0, 0, Root.ActualWidth, Root.ActualHeight),
                r, r);
            geo.Freeze();
            Root.Clip = geo;
            Root.CornerRadius = new CornerRadius(r);
            EdgeRing.CornerRadius = new CornerRadius(RootCornerRadius);
            ShadowPlate.CornerRadius = new CornerRadius(RootCornerRadius);
            return;
        }

        const double inset = 0.5;
        Root.Clip = new RectangleGeometry(
            new Rect(inset, inset, Math.Max(0, Root.ActualWidth - (inset * 2)), Math.Max(0, Root.ActualHeight - (inset * 2))),
            Math.Max(0, RootCornerRadius - inset),
            Math.Max(0, RootCornerRadius - inset));
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsToastOverlayButtonSource(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            return;
        }

        // Layout-test toasts: no drag-out and no body click actions.
        if (InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        _mouseDownPos = e.GetPosition(this);
        _isDragging = false;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (IsToastOverlayButtonSource(e.OriginalSource as DependencyObject))
        {
            CancelRootInteractionFromOverlaySource(e);
            return;
        }

        if (InteractiveActionsBlocked)
            return;

        if (!IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed)
            return;

        var diff = e.GetPosition(this) - _mouseDownPos;
        if (!_isDragging && Math.Abs(diff.X) < 5 && Math.Abs(diff.Y) < 5)
            return;

        if (!_isDragging)
        {
            _isDragging = true;
            BeginDragFeedback();
        }

        string? dragFile = null;
        System.Windows.GiveFeedbackEventHandler? feedback = null;
        try
        {
            dragFile = GetDragFilePath();
            if (dragFile is null)
            {
                EndDragFeedback(cancelled: false);
                ReleaseMouseCapture();
                if (!string.IsNullOrWhiteSpace(_savedFilePath))
                    ShowSavedFileMissingError();
                else
                    ShowToastDragError("No preview file is available to drag.");
                return;
            }

            var data = new System.Windows.DataObject();
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { dragFile });
            feedback = (_, args) =>
            {
                Mouse.SetCursor(System.Windows.Input.Cursors.Hand);
                args.UseDefaultCursors = false;
                args.Handled = true;
            };
            GiveFeedback += feedback;
            var result = System.Windows.DragDrop.DoDragDrop(this, data, System.Windows.DragDropEffects.Copy | System.Windows.DragDropEffects.Move);
            if (result == System.Windows.DragDropEffects.None)
            {
                EndDragFeedback(cancelled: true);
                return;
            }

            DismissAnimated();
        }
        catch (Exception ex)
        {
            EndDragFeedback(cancelled: true);
            ShowToastDragError(ex.Message);
        }
        finally
        {
            if (feedback is not null)
                GiveFeedback -= feedback;

            if (_savedFilePath is null && !string.IsNullOrWhiteSpace(dragFile) && File.Exists(dragFile))
            {
                try
                {
                    File.Delete(dragFile);
                }
                catch (Exception ex)
                {
                    AppDiagnostics.LogWarning("toast.drag-temp-delete", $"Failed to delete temporary drag file {Path.GetFileName(dragFile)}: {ex.Message}", ex);
                }
            }

            _isDragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsToastOverlayButtonSource(e.OriginalSource as DependencyObject))
        {
            CancelRootInteractionFromOverlaySource(e);
            return;
        }

        if (InteractiveActionsBlocked)
        {
            if (IsMouseCaptured)
                ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (!IsMouseCaptured)
            return;

        ReleaseMouseCapture();
        if (_isDragging)
            return;

        if (!string.IsNullOrWhiteSpace(_spec.ClickActionUrl))
        {
            if (_spec.ClickActionUrl == "cybersnap://update")
            {
                DismissAnimated();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var app = (App)Application.Current;
                    var result = app.LatestUpdateResult;
                    if (result != null)
                    {
                        app.ShowSettingsAndDownloadUpdate(result);
                    }
                });
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _spec.ClickActionUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogWarning("toast.click-action-open", $"Failed to open click action URL: {ex.Message}", ex);
                if (HasSavedFileOnDisk())
                {
                    OpenFileLocation(_savedFilePath);
                }
                else if (!string.IsNullOrWhiteSpace(_savedFilePath))
                {
                    ShowSavedFileMissingError();
                }
                else
                {
                    ShowToastOpenError("Could not open the linked target.");
                }
            }
            return;
        }

        // Capture-preview body click: honor Settings → Notifications → toast preview click action.
        // Spec-level ClickActionUrl (updates, etc.) was already handled above.
        if (_previewBitmap is not null || HasSavedFileOnDisk() || !string.IsNullOrWhiteSpace(_savedFilePath))
        {
            HandlePreviewBodyClick();
            return;
        }

        DismissAnimated();
    }

    private void HandlePreviewBodyClick()
    {
        if (InteractiveActionsBlocked)
            return;

        ToastPreviewClickAction action;
        try
        {
            action = SettingsService.LoadStatic()?.ToastPreviewClickAction
                ?? ToastPreviewClickAction.OpenInEditor;
        }
        catch
        {
            action = ToastPreviewClickAction.OpenInEditor;
        }

        switch (action)
        {
            case ToastPreviewClickAction.OpenInDefaultViewer:
                if (HasSavedFileOnDisk())
                {
                    OpenWithDefaultViewer();
                    return;
                }
                if (!string.IsNullOrWhiteSpace(_savedFilePath))
                {
                    ShowSavedFileMissingError();
                    return;
                }
                // Clipboard-only preview: fall back to the editor when possible.
                if (_previewBitmap is not null)
                {
                    OpenEditor();
                    return;
                }
                CloseToast();
                return;

            case ToastPreviewClickAction.CopyToClipboard:
                if (_previewBitmap is not null)
                {
                    CopyPreview();
                    return;
                }
                CloseToast();
                return;

            case ToastPreviewClickAction.Save:
                if (_previewBitmap is not null)
                {
                    SavePreview();
                    return;
                }
                if (HasSavedFileOnDisk())
                {
                    // Already on disk: open save-as still useful, but without a bitmap fall back to folder.
                    OpenFileLocation(_savedFilePath);
                    return;
                }
                CloseToast();
                return;

            case ToastPreviewClickAction.OpenInGallery:
                OpenHistory();
                return;

            case ToastPreviewClickAction.Close:
                CloseToast();
                return;

            case ToastPreviewClickAction.OpenInEditor:
            default:
                if (_previewBitmap is not null || HasSavedFileOnDisk() || IsVideoOrGifPath(_savedFilePath))
                {
                    OpenEditor();
                    return;
                }
                if (!string.IsNullOrWhiteSpace(_savedFilePath))
                {
                    ShowSavedFileMissingError();
                    return;
                }
                CloseToast();
                return;
        }
    }

    private static bool IsVideoOrGifPath(string? path)
        => path is not null &&
           (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Centered neutral play badge on video/GIF capture previews (same language as the designer mock).
    /// </summary>
    private void UpdateVideoPlayBadge(string? filePath)
    {
        if (VideoPlayBadge is null)
            return;

        bool show = IsVideoOrGifPath(filePath);
        VideoPlayBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
            VideoPlayBadge.Background = Theme.Brush(Color.FromArgb(180, 0, 0, 0));
    }

    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsToastOverlayButtonSource(e.OriginalSource as DependencyObject))
            return;

        if (InteractiveActionsBlocked)
        {
            e.Handled = true;
            return;
        }

        if (!HasSavedFileOnDisk())
            return;

        ShowImageContextMenu();
        e.Handled = true;
    }

    private void ShowImageContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var openFolderItem = new System.Windows.Controls.MenuItem
        {
            Header = "Mostrar captura en carpeta"
        };
        openFolderItem.Click += (_, _) => OpenFileLocation(_savedFilePath);
        menu.Items.Add(openFolderItem);

        var openViewerItem = new System.Windows.Controls.MenuItem
        {
            Header = "Mostrar el visor de imágenes predeterminado del sistema"
        };
        openViewerItem.Click += (_, _) => OpenWithDefaultViewer();
        menu.Items.Add(openViewerItem);

        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.PlacementTarget = this; // ensure menu closes when clicking outside
        menu.Closed += (_, _) => menu.Items.Clear(); // prevent leak
        menu.IsOpen = true;
    }

    private void OpenWithDefaultViewer()
    {
        if (!HasSavedFileOnDisk())
        {
            ShowSavedFileMissingError();
            return;
        }

        bool isPdf = _savedFilePath is not null && _savedFilePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _savedFilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("toast.open-default-viewer", $"Failed to open with default viewer: {ex.Message}", ex);
            ShowToastOpenError(isPdf ? "Could not open the PDF with the default viewer." : "Could not open the image with the default viewer.");
        }
    }

    private void BeginDragFeedback()
    {
        CancelDismissForHover();
        if (IsDefaultDarkTheme())
        {
            // Padding-ring: flash the solid EdgeRing fill brighter while dragging.
            _dragBorderBrush = EdgeRing.Background;
            _dragBorderThickness = EdgeRing.Padding;
            EdgeRing.Background = Theme.Brush(Color.FromRgb(230, 250, 255));
        }
        else
        {
            _dragBorderThickness = OuterShell.BorderThickness;
            _dragBorderBrush = OuterShell.BorderBrush;
            OuterShell.BorderBrush = Theme.Brush(Color.FromArgb(230, 255, 255, 255));
            OuterShell.BorderThickness = new Thickness(2.4);
        }
        DragScale.CenterX = ActualWidth / 2;
        DragScale.CenterY = ActualHeight / 2;
        DragScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            Motion.To(0.96, 160, Motion.SmoothOut));
        DragScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            Motion.To(0.96, 160, Motion.SmoothOut));
        Root.BeginAnimation(UIElement.OpacityProperty, Motion.To(0.88, 160, Motion.SoftOut));
    }

    private void EndDragFeedback(bool cancelled)
    {
        if (IsDefaultDarkTheme())
        {
            if (_dragBorderBrush is not null)
                EdgeRing.Background = _dragBorderBrush;
            EdgeRing.Padding = _dragBorderThickness == default
                ? new Thickness(EdgeRingStrokeWidth)
                : _dragBorderThickness;
        }
        else
        {
            if (_dragBorderBrush is not null)
                OuterShell.BorderBrush = _dragBorderBrush;
            OuterShell.BorderThickness = _dragBorderThickness;
        }
        _dragBorderBrush = null;
        DragScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            Motion.To(1, 140, Motion.SmoothOut));
        DragScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            Motion.To(1, 140, Motion.SmoothOut));
        Root.BeginAnimation(UIElement.OpacityProperty, Motion.To(1, 140, Motion.SoftOut));
        if (cancelled)
            ResumeDismissAfterAbortedInteractionIfNeeded();
    }

    private void CancelRootInteractionFromOverlaySource(System.Windows.Input.MouseEventArgs e)
    {
        if (_isDragging)
            EndDragFeedback(cancelled: true);
        else
            ResumeDismissAfterAbortedInteractionIfNeeded();

        _isDragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    private void ResumeDismissAfterAbortedInteractionIfNeeded()
    {
        if (!_resumeDismissOnMouseLeave || _isPinned)
            return;

        _isHovered = IsCursorOverToast();
        if (_isHovered)
            return;

        _resumeDismissOnMouseLeave = false;
        DismissAnimated();
    }

    private bool IsCursorOverToast()
    {
        if (!GetCursorPos(out var cursor))
            return IsMouseOver;

        return IsScreenPointOver(OuterShell, cursor);
    }

    private static bool IsScreenPointOver(FrameworkElement element, NativePoint point)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        var topLeft = element.PointToScreen(new System.Windows.Point(0, 0));
        return point.X >= topLeft.X &&
               point.X <= topLeft.X + element.ActualWidth &&
               point.Y >= topLeft.Y &&
               point.Y <= topLeft.Y + element.ActualHeight;
    }

    private static bool IsChildOf(DependencyObject? child, DependencyObject parent)
    {
        while (child != null)
        {
            if (child == parent) return true;
            child = GetVisualOrLogicalParent(child);
        }
        return false;
    }

    // VisualTreeHelper.GetParent throws on non-Visuals (e.g. a Run from an inline
    // TextBlock). Walk the visual tree for Visuals and fall back to the logical tree
    // for ContentElements so hit-test sources over inline text don't crash.
    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject child)
    {
        if (child is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(child);

        return LogicalTreeHelper.GetParent(child)
            ?? (child as FrameworkContentElement)?.Parent;
    }

    private bool IsToastOverlayButtonSource(DependencyObject? source) =>
        IsChildOf(source, CloseBtn) ||
        IsChildOf(source, PinBtn) ||
        IsChildOf(source, SaveBtn) ||
        IsChildOf(source, ShareBtn) ||
        IsChildOf(source, DeleteBtn) ||
        IsChildOf(source, HistoryBtn) ||
        IsChildOf(source, EditBtn) ||
        IsChildOf(source, TextCloseBtn);

    private string? GetDragFilePath()
    {
        if (HasSavedFileOnDisk())
            return _savedFilePath;

        if (_previewBitmap is null)
            return null;

        var temp = Path.Combine(Path.GetTempPath(), $"CyberSnap_toast_{Guid.NewGuid():N}.png");
        CaptureOutputService.SavePng(_previewBitmap, temp);
        return temp;
    }

    private void ShowToastOpenError(string message)
    {
        Show(ToastSpec.Error(
            LocalizationService.Translate("Open failed"),
            LocalizationService.Translate(message),
            _savedFilePath));
    }

    private void ShowToastDragError(string message)
    {
        Show(ToastSpec.Error("Drag failed", message, _savedFilePath));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            UpdateRootClip();
            ApplyPlacement(animateEntry: true, subtleEntry: false);

            if (!_isPinned)
                RestartVisibleTimer(_activeDurationSeconds);
        }, DispatcherPriority.Render);
    }

    private void CancelActiveToastState()
    {
        _toastStateVersion++;
        _timer.Stop();
        _isHovered = false;
        _isDragging = false;
        _isDismissing = false;
        _isFading = false;
        _closeAfterOpacityAnimation = false;
        _resumeDismissOnMouseLeave = false;
        _isSavingPreview = false;
        _isDeletingSavedFile = false;
        _isRunningShareAction = false;
        if (IsMouseCaptured)
            ReleaseMouseCapture();
        SaveBtn.IsEnabled = true;
        ShareBtn.IsEnabled = true;
        _deleteFileOnDismiss = false;
        // Delete enablement re-applied in ApplyOverlayButton when the next spec loads.
        RefreshOverlayButtonLayout();
        StopDismissAnimationTimer();
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(OpacityProperty, null);
        Root.BeginAnimation(UIElement.OpacityProperty, null);
        OuterShell.BeginAnimation(UIElement.OpacityProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        DragScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DragScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressScale.ScaleX = 1;
        ProgressBar.Visibility = Visibility.Visible;
        if (IsDefaultDarkTheme())
        {
            if (_dragBorderBrush is not null)
                EdgeRing.Background = _dragBorderBrush;
            EdgeRing.Padding = _dragBorderThickness == default
                ? new Thickness(EdgeRingStrokeWidth)
                : _dragBorderThickness;
        }
        else
        {
            if (_dragBorderBrush is not null)
                OuterShell.BorderBrush = _dragBorderBrush;
            OuterShell.BorderThickness = _dragBorderThickness == default ? new Thickness(1.0) : _dragBorderThickness;
        }
        _dragBorderBrush = null;
        _dragBorderThickness = default;
        Mouse.OverrideCursor = null;
    }

    private void PulseRefreshAnimation()
    {
        DragScale.CenterX = ActualWidth / 2;
        DragScale.CenterY = ActualHeight / 2;
        DragScale.ScaleX = 0.985;
        DragScale.ScaleY = 0.985;
        Root.Opacity = 0.94;
        DragScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.To(1, 140, Motion.SmoothOut));
        DragScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.To(1, 140, Motion.SmoothOut));
        Root.BeginAnimation(UIElement.OpacityProperty, Motion.To(1, 140, Motion.SmoothOut));
    }

    private void ApplyPlacement(bool animateEntry, bool subtleEntry)
    {
        ApplyTimelineLayout();

        // 1. Get the target monitor
        var screens = PopupWindowHelper.GetSortedScreens();
        var screen = (_monitorIndex >= 0 && _monitorIndex < screens.Length)
            ? screens[_monitorIndex]
            : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);

        // 2. Move window to the target monitor physically first to let WPF update its DPI context.
        var phys = screen.WorkingArea;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            Native.User32.SetWindowPos(hwnd, IntPtr.Zero, phys.X, phys.Y, 0, 0,
                Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE | Native.User32.SWP_NOZORDER);
        }

        // 3. Now calculate the work area in DIPs using PointFromScreen.
        var topLeft = PointFromScreen(new System.Windows.Point(phys.Left, phys.Top));
        var bottomRight = PointFromScreen(new System.Windows.Point(phys.Right, phys.Bottom));
        
        var wa = new Rect(
            this.Left + topLeft.X,
            this.Top + topLeft.Y,
            bottomRight.X - topLeft.X,
            bottomRight.Y - topLeft.Y);

        var (targetLeft, targetTop, startLeft, startTop, animateLeft) = PopupWindowHelper.GetPlacement(
            _position, ActualWidth, ActualHeight, wa, Edge);

        Left = targetLeft;
        Top = targetTop;
        Opacity = 1;
        Root.Opacity = 1;
        OuterShell.Opacity = 1;

        SlideTransform.BeginAnimation(TranslateTransform.XProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);

        if (!animateEntry)
        {
            SlideTransform.X = 0;
            SlideTransform.Y = 0;
            return;
        }

        double offsetX;
        double offsetY;
        if (subtleEntry)
        {
            const double subtleDistance = 18;
            offsetX = animateLeft
                ? (startLeft < targetLeft ? -subtleDistance : subtleDistance)
                : 0;
            offsetY = animateLeft
                ? 0
                : (startTop < targetTop ? -subtleDistance : subtleDistance);
        }
        else
        {
            offsetX = animateLeft ? startLeft - targetLeft : 0;
            offsetY = animateLeft ? 0 : startTop - targetTop;
        }

        SlideTransform.X = offsetX;
        SlideTransform.Y = offsetY;

        // Celebrations settle in with a small elastic overshoot; everything else uses
        // the calm smooth-out glide.
        var celebrateEntry = _spec.Celebrate && !_spec.IsError && !subtleEntry;
        var dur = Motion.Ms(celebrateEntry ? 360 : (subtleEntry ? 160 : 200));
        IEasingFunction? ease = celebrateEntry
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
            : Motion.Ease(Motion.SmoothOut);
        SlideTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            To = 0,
            Duration = dur,
            EasingFunction = ease
        });
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = 0,
            Duration = dur,
            EasingFunction = ease
        });

        if (subtleEntry)
            PulseRefreshAnimation();
    }

    private void DismissAnimated()
    {
        if (!IsLoaded)
        {
            TryForceClose(force: true);
            return;
        }

        // Toasts always dismiss with a fade-out. SlideAway() is retained for a possible
        // future "slide" option but is intentionally not wired up right now.
        FadeAway();
    }

    private void RestartVisibleTimer(double seconds)
    {
        _timer.Stop();
        _timer.Interval = TimeSpan.FromSeconds(seconds);
        ProgressBar.Visibility = Visibility.Visible;
        ProgressScale.ScaleX = Math.Clamp(seconds / _activeDurationSeconds, 0, 1);
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation { To = 0, Duration = Motion.Sec(seconds) });
        _timer.Start();
    }

    private void CancelDismissForHover()
    {
        if (!_isFading && !_isDismissing)
            return;

        _resumeDismissOnMouseLeave = true;
        _isDismissing = false;
        _isFading = false;
        _closeAfterOpacityAnimation = false;
        StopDismissAnimationTimer();
        Opacity = 1;
        Root.Opacity = 1;
        OuterShell.Opacity = 1;
        SlideTransform.X = 0;
        SlideTransform.Y = 0;
    }

    private void FadeAway()
    {
        if (_isDismissing || _isFading)
            return;

        _resumeDismissOnMouseLeave = false;
        _isDismissing = true;
        _isFading = true;
        _timer.Stop();
        ProgressBar.Visibility = Visibility.Collapsed;
        _closeAfterOpacityAnimation = true;
        StartDismissAnimation(Motion.Sec(_fadeOutSeconds), slide: false, 0, 0);
    }

    private void SlideAway()
    {
        if (_isDismissing) return;
        _resumeDismissOnMouseLeave = false;
        _isDismissing = true;
        _isFading = false;
        _timer.Stop();
        _closeAfterOpacityAnimation = true;
        ProgressBar.Visibility = Visibility.Collapsed;

        var dur = Motion.Ms(240);
        var (dismissOffsetX, dismissOffsetY) = GetDismissOffset();
        StartDismissAnimation(dur, slide: true, dismissOffsetX, dismissOffsetY);
    }

    private void StartDismissAnimation(TimeSpan duration, bool slide, double offsetX, double offsetY)
    {
        StopDismissAnimationTimer();
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Opacity = 1;
        Root.Opacity = 1;
        OuterShell.Opacity = 1;
        var dismissToken = _dismissAnimationToken;
        IEasingFunction ease = slide
            ? Motion.SmoothOut
            : Motion.SmoothInOut;

        if (slide)
        {
            var wa = PopupWindowHelper.GetCurrentWorkArea();
            var (exitLeft, exitTop, animateLeft) = PopupWindowHelper.GetDismissPlacement(
                _position, ActualWidth, ActualHeight, wa, Edge);
            if (animateLeft)
            {
                BeginAnimation(LeftProperty, new DoubleAnimation
                {
                    To = exitLeft,
                    Duration = duration,
                    EasingFunction = ease
                });
            }
            else
            {
                BeginAnimation(TopProperty, new DoubleAnimation
                {
                    To = exitTop,
                    Duration = duration,
                    EasingFunction = ease
                });
            }
        }

        var opacityAnimation = new DoubleAnimation
        {
            To = 0,
            Duration = duration,
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };
        opacityAnimation.Completed += (_, _) =>
        {
            if (dismissToken != _dismissAnimationToken)
                return;

            if (_closeAfterOpacityAnimation)
                Dispatcher.BeginInvoke(new Action(() => TryForceClose()));
        };
        BeginAnimation(OpacityProperty, opacityAnimation);
    }

    private void StopDismissAnimationTimer()
    {
        _dismissAnimationToken++;
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
    }

    internal void RequestDismiss(bool force = false)
    {
        if (force)
        {
            TryForceClose(force: true);
            return;
        }

        if (Dispatcher.CheckAccess())
            DismissAnimated();
        else
            Dispatcher.BeginInvoke(DismissAnimated);
    }

    private static double Lerp(double from, double to, double t) => from + ((to - from) * t);

    private static double EaseInOutQuad(double t)
        => t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;

    private static double EaseInOutCubic(double t)
        => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private bool TryForceClose(bool force = false)
    {
        RunOnClosedCleanup("toast.force-close.stop-timer", () => _timer.Stop());
        RunOnClosedCleanup("toast.force-close.stop-dismiss-animation", StopDismissAnimationTimer);
        _resumeDismissOnMouseLeave = false;
        if (_isPinned && !force)
            return false;

        if (_current == this) _current = null;
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("toast.force-close", ex.Message, ex);
        }

        return true;
    }

    protected override void OnClosed(EventArgs e)
    {
        RunOnClosedCleanup("toast.closed.stop-timer", () => _timer.Stop());
        RunOnClosedCleanup("toast.closed.stop-dismiss-animation", StopDismissAnimationTimer);
        if (_current == this) _current = null;
        RunOnClosedCleanup("toast.closed.dispose-preview", () => _previewBitmap?.Dispose());
        _previewBitmap = null;
        RunOnClosedCleanup("toast.closed.clear-preview-source", () => PreviewImage.Source = null);
        RunOnClosedCleanup("toast.closed.clear-inline-source", () => InlinePreviewImage.Source = null);
        if (_deleteFileOnDismiss)
        {
            var path = _savedFilePath;
            _deleteFileOnDismiss = false;
            RunOnClosedCleanup("toast.closed.delete-ephemeral", () =>
            {
                Helpers.CaptureSavePath.TryDeleteTempRecording(path);
                // Also best-effort for non-temp ephemeral paths (should not happen).
                if (!string.IsNullOrWhiteSpace(path)
                    && !Helpers.CaptureSavePath.IsTempRecordingPath(path)
                    && File.Exists(path))
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                }
            });
        }
        base.OnClosed(e);
    }

    private static void RunOnClosedCleanup(string diagnosticKey, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning(diagnosticKey, ex.Message, ex);
        }
    }

    private static (double x, double y) GetDismissOffset() => _position switch
    {
        CyberSnap.Models.ToastPosition.TopLeft => (0, -32),
        CyberSnap.Models.ToastPosition.TopRight => (0, -32),
        CyberSnap.Models.ToastPosition.BottomLeft => (-56, 0),
        CyberSnap.Models.ToastPosition.TopCenter => (0, -32),
        CyberSnap.Models.ToastPosition.BottomCenter => (0, 32),
        CyberSnap.Models.ToastPosition.Left => (-56, 0),
        CyberSnap.Models.ToastPosition.Right => (56, 0),
        _ => (56, 0)
    };

    // Sets the body text, optionally followed by the app's signature capture icon
    // (the same "captureRect" icon shown for Area Capture in the widget) for celebrations —
    // a friendly "stamp" at the end of the second line.
    private void SetBodyContent(string text, bool withCelebrationIcon, string? celebrationIconId = null)
    {
        BodyText.Inlines.Clear();

        if (!withCelebrationIcon)
        {
            BodyText.Text = text;
            return;
        }

        // Default to the cyan capture motif; a "trophy" (or other) icon is tinted gold so it reads
        // as a reward on achievement toasts instead of an unrelated capture icon.
        var iconId = string.IsNullOrEmpty(celebrationIconId) ? "captureRect" : celebrationIconId;
        var iconColor = iconId == "captureRect"
            ? System.Drawing.Color.FromArgb(0x00, 0xF2, 0xFF)
            : System.Drawing.Color.FromArgb(0xFF, 0xC1, 0x07);

        var icon = new System.Windows.Controls.Image
        {
            Source = FluentIcons.RenderWpf(iconId, iconColor, 16),
            Width = 14,
            Height = 14,
            Margin = new Thickness(5, 0, 0, 0),
            Stretch = Stretch.Uniform
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        BodyText.Inlines.Add(new System.Windows.Documents.Run(text));
        BodyText.Inlines.Add(new System.Windows.Documents.InlineUIContainer(icon)
        {
            BaselineAlignment = BaselineAlignment.Center
        });
    }

    // Celebration flourish: swaps the timeline to a seamless flowing rainbow that
    // sweeps continuously; restores the normal gradient when off. Cheapest effect of
    // the celebration-mode roadmap.
    // Builds the sober silver progress brush used in the grayscale theme, replacing the
    // cyberpunk cyan/purple/magenta so the timeline bar stays monochrome. When loopable,
    // the stops are periodic and symmetric (start == end) so the celebration sweep can
    // translate 0->1 with no visible seam; otherwise it's a static left-to-right sheen.
    private static System.Windows.Media.LinearGradientBrush CreateSilverProgressBrush(
        bool loopable, TranslateTransform? sweep = null)
    {
        // Sober silver ramp: bright highlight through mid-silver to a dim edge.
        var bright = Color.FromRgb(0xE6, 0xEA, 0xEF);
        var mid = Color.FromRgb(0xB8, 0xBE, 0xC6); // mirrors Theme.GraySilver
        var dim = Color.FromRgb(0x7C, 0x82, 0x8C);

        var stops = loopable
            ? new GradientStopCollection
            {
                new GradientStop(bright, 0.0),
                new GradientStop(mid, 0.25),
                new GradientStop(dim, 0.5),
                new GradientStop(mid, 0.75),
                new GradientStop(bright, 1.0),
            }
            : new GradientStopCollection
            {
                new GradientStop(bright, 0.0),
                new GradientStop(mid, 0.5),
                new GradientStop(dim, 1.0),
            };

        return new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0.5),
            EndPoint = new System.Windows.Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            SpreadMethod = loopable ? GradientSpreadMethod.Repeat : GradientSpreadMethod.Pad,
            GradientStops = stops,
            RelativeTransform = sweep
        };
    }

    private void ApplyCelebrationVisual(bool on)
    {
        if (on)
        {
            if (_celebrationBrush is null)
            {
                _celebrationSweep = new TranslateTransform();
                _celebrationBrush = Theme.IsGray
                    ? CreateSilverProgressBrush(loopable: true, _celebrationSweep)
                    : new System.Windows.Media.LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0.5),
                    EndPoint = new System.Windows.Point(1, 0.5),
                    MappingMode = BrushMappingMode.RelativeToBoundingBox,
                    SpreadMethod = GradientSpreadMethod.Repeat,
                    // Symmetric, periodic stops (start color == end color) so a 0->1
                    // translate loops with no visible seam.
                    GradientStops = new GradientStopCollection
                    {
                        new GradientStop(Color.FromRgb(0x00, 0xF2, 0xFF), 0.0),  // cyan
                        new GradientStop(Color.FromRgb(0x7A, 0x00, 0xFF), 0.25), // purple
                        new GradientStop(Color.FromRgb(0xFF, 0x00, 0xD0), 0.5),  // magenta
                        new GradientStop(Color.FromRgb(0x7A, 0x00, 0xFF), 0.75), // purple
                        new GradientStop(Color.FromRgb(0x00, 0xF2, 0xFF), 1.0),  // cyan
                    },
                    // RelativeTransform (not Transform): a translate of X here is a
                    // fraction of the bar width, so 0->1 slides a full width. Using
                    // Transform would translate in absolute units (~1px) and be invisible.
                    RelativeTransform = _celebrationSweep
                };
            }

            ProgressBar.Background = _celebrationBrush;
            _celebrationSweep!.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = Motion.Sec(1.4),
                RepeatBehavior = RepeatBehavior.Forever
            });

            // Pronounced "breathing" glow that pulses while the rainbow flows, so the
            // flourish clearly reads as celebratory rather than a rendering glitch.
            ProgressGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, new DoubleAnimation
            {
                From = 10,
                To = 34,
                Duration = Motion.Sec(0.8),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
            ProgressGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation
            {
                From = 0.55,
                To = 1.0,
                Duration = Motion.Sec(0.8),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });

            // The bar's host clips to its 4px row, which would swallow the glow halo.
            // Let it bleed into the toast body during celebrations (the rounded Root
            // still clips the outer edge).
            ProgressHost.ClipToBounds = false;
        }
        else
        {
            _celebrationSweep?.BeginAnimation(TranslateTransform.XProperty, null);
            ProgressGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, null);
            ProgressGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, null);
            // Default Dark keeps the rail glow off so bottom corner arcs stay sharp.
            if (IsDefaultDarkTheme())
            {
                ProgressGlow.BlurRadius = 0;
                ProgressGlow.Opacity = 0;
            }
            else
            {
                ProgressGlow.BlurRadius = 8;
                ProgressGlow.Opacity = 0.8;
            }
            ProgressHost.ClipToBounds = true;
            // Don't clobber the red error brush; restore the normal gradient otherwise.
            if (!_spec.IsError && _defaultProgressBrush is not null)
                ProgressBar.Background = _defaultProgressBrush;
        }
    }

    // Places the timeline bar on the screen-facing edge (top for Top* positions,
    // bottom otherwise) and makes it shrink symmetrically toward the center for
    // the centered positions. Driven by the current static _position.
    private void ApplyTimelineLayout()
    {
        bool topEdge = _position is CyberSnap.Models.ToastPosition.TopLeft
            or CyberSnap.Models.ToastPosition.TopRight
            or CyberSnap.Models.ToastPosition.TopCenter;
        bool centered = _position is CyberSnap.Models.ToastPosition.TopCenter
            or CyberSnap.Models.ToastPosition.BottomCenter;

        System.Windows.Controls.Grid.SetRow(ProgressHost, topEdge ? 0 : 2);
        TopProgressRow.Height = new GridLength(topEdge ? 4 : 0);
        BottomProgressRow.Height = new GridLength(topEdge ? 0 : 4);

        // Centered toasts shrink from both ends toward the middle; the others keep
        // the left-anchored right-to-left shrink.
        ProgressBar.RenderTransformOrigin = centered
            ? new System.Windows.Point(0.5, 0.5)
            : new System.Windows.Point(0, 0.5);

        // When the bar sits on top, square the image's upper corners so the rounded
        // notch doesn't peek out beneath the timeline. Inner radius matches EdgeRing inset.
        double imageR = IsDefaultDarkTheme() ? InnerCornerRadius : RootCornerRadius;
        ImageFrame.CornerRadius = topEdge
            ? new CornerRadius(0)
            : new CornerRadius(imageR, imageR, 0, 0);
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
