using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using CyberSnap.Helpers;
using CyberSnap.Native;
using CyberSnap.Services;
using CyberSnap.UI.Controls;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Binding = System.Windows.Data.Binding;

namespace CyberSnap.UI;

public sealed class PinSourceWindow : Window
{
    private IntPtr _captureHandle;
    private readonly System.Drawing.Point _openMonitorPoint;
    private readonly double _uiScale;
    private PinSourceBackdrop? _backdrop;
    private bool _closed;
    private bool _centered;
    private DispatcherTimer? _attentionTimer;
    private System.Drawing.Point _attentionOrigin;
    private readonly Func<bool> _clipboardContainsImage;
    private Button _clipboardButton = null!;
    private HwndSource? _windowSource;
    private bool _clipboardListenerRegistered;
    private DispatcherTimer? _clipboardPollTimer;
    public event Action<string>? SourceRequested;
    public PinSourceWindow() : this(() => User32.IsClipboardFormatAvailable(User32.CF_BITMAP)) { }

    internal PinSourceWindow(Func<bool> clipboardContainsImage)
    {
        _clipboardContainsImage = clipboardContainsImage;
        _openMonitorPoint = PopupWindowHelper.TakeMonitorHintPoint() ?? System.Windows.Forms.Cursor.Position;
        _uiScale = UiScale.Current;
        Theme.Refresh();
        Theme.ApplyTo(Resources);
        Theme.ApplyTo(Application.Current.Resources);
        FontFamily = new System.Windows.Media.FontFamily(UiChrome.PreferredFamilyName);
        Title = LocalizationService.Translate("Pin to screen");
        Width = 620; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Opacity = 0;
        // This fixed-size transparent window already gets its rounded silhouette
        // from the Border below. A second, native WindowChrome region can retain
        // the old monitor's pixel width during DPI adoption and crop the right side.
        var panel = new StackPanel();
        var title = new CyberSnapTitleBar { Title = Title };
        title.CloseRequested += (_, _) => Close();
        panel.Children.Add(title);
        var body = new StackPanel { Margin = new Thickness(22, 12, 22, 22) };
        body.Children.Add(new TextBlock
        {
            Text = LocalizationService.Translate("Keep an image visible while you work."),
            Foreground = Theme.Brush(Theme.TextSecondary), FontSize = 14,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18)
        });
        var sources = new Grid();
        var sourceButtonStyle = new Style(typeof(Button), (Style)FindResource("ChromeButtonBase"))
        {
            Setters =
            {
                new Setter(TemplateProperty, FindResource("CyberSnapActionButtonTemplate")),
                new Setter(BackgroundProperty, new DynamicResourceExtension("ThemeInputBackgroundBrush")),
                new Setter(ForegroundProperty, new DynamicResourceExtension("ThemeTextPrimaryBrush")),
                new Setter(BorderBrushProperty, new DynamicResourceExtension("ThemeInputBorderBrush")),
                new Setter(BorderThicknessProperty, new Thickness(1))
            }
        };
        foreach (var _ in new[] { 0, 1, 2 }) sources.ColumnDefinitions.Add(new ColumnDefinition());
        var entries = new[] { ("region", "Select region...", "captureRect"), ("clipboard", "From clipboard", "paste"), ("file", "Select image...", "folder") };
        for (int i = 0; i < entries.Length; i++)
        {
            var (id, text, icon) = entries[i];
            var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            var glyph = new System.Windows.Shapes.Path
            {
                Data = FluentIcons.GetGeometry(icon),
                Width = 25, Height = 25, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 12)
            };
            glyph.SetBinding(System.Windows.Shapes.Shape.FillProperty, new Binding(nameof(Button.Foreground))
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
            content.Children.Add(glyph);
            content.Children.Add(new TextBlock { Text = LocalizationService.Translate(text), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
            var button = new Button { Content = content, MinHeight = 105, Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0), Padding = new Thickness(10) };
            button.Style = sourceButtonStyle;
            System.Windows.Automation.AutomationProperties.SetName(button, LocalizationService.Translate(text));
            if (id == "clipboard")
            {
                _clipboardButton = button;
                ToolTipService.SetShowOnDisabled(button, true);
                RefreshClipboardAvailability();
            }
            button.Click += (_, _) =>
            {
                if (id == "clipboard")
                {
                    RefreshClipboardAvailability();
                    if (!button.IsEnabled) return;
                }
                SourceRequested?.Invoke(id);
            };
            Grid.SetColumn(button, i); sources.Children.Add(button);
        }
        body.Children.Add(sources); panel.Children.Add(body);
        Content = new Border
        {
            Background = Theme.Brush(Theme.BgPrimary), BorderBrush = Theme.Brush(Theme.Border), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Child = panel
        };
        UiScale.ApplyToWindow(this, panel, scaleWindowBounds: true);
        ClearValue(MinWidthProperty);
        ClearValue(MinHeightProperty);
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; Close(); } };
        SourceInitialized += (_, _) =>
        {
            var interop = new WindowInteropHelper(this);
            _captureHandle = interop.Handle;
            _windowSource = HwndSource.FromHwnd(_captureHandle);
            _windowSource?.AddHook(WindowProc);
            _clipboardListenerRegistered = User32.AddClipboardFormatListener(_captureHandle);
            if (!_clipboardListenerRegistered)
            {
                AppDiagnostics.LogWarning("pin.clipboard-listener",
                    $"Clipboard notifications unavailable (Win32 {Marshal.GetLastWin32Error()}); using periodic refresh.");
                _clipboardPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _clipboardPollTimer.Tick += (_, _) => RefreshClipboardAvailability();
                _clipboardPollTimer.Start();
            }
            RefreshClipboardAvailability();
            Capture.CaptureWindowExclusion.Apply(_captureHandle);
            _backdrop = new PinSourceBackdrop();
            _backdrop.MouseDown += (_, _) => RequestAttention();
            _backdrop.Show();
            interop.Owner = _backdrop.Handle;
        };
        ContentRendered += (_, _) =>
        {
            if (_centered || _closed) return;
            _centered = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_closed) return;
                // Finish positioning while hidden, including SizeToContent and any
                // render-DPI update queued by the initial native monitor move.
                PopupWindowHelper.CenterWindowOnPhysicalMonitor(this, _openMonitorPoint);
                SynchronizeRenderDpi();
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;
                    Width = 620 * _uiScale;
                    UpdateLayout();
                    CenterOnOpenMonitor();
                    Opacity = 1;
                }), DispatcherPriority.ContextIdle);
            }), DispatcherPriority.ContextIdle);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (_backdrop is { IsDisposed: false }) _backdrop.Visible = IsVisible;
        };
        Activated += (_, _) => RefreshClipboardAvailability();
        Closed += (_, _) =>
        {
            _closed = true;
            StopAttention();
            _clipboardPollTimer?.Stop();
            if (_clipboardListenerRegistered)
                User32.RemoveClipboardFormatListener(_captureHandle);
            _clipboardListenerRegistered = false;
            _windowSource?.RemoveHook(WindowProc);
            _windowSource = null;
            Capture.CaptureWindowExclusion.Unregister(_captureHandle);
            _backdrop?.Close();
            _backdrop?.Dispose();
            _backdrop = null;
        };
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == User32.WM_CLIPBOARDUPDATE && !_closed)
            RefreshClipboardAvailability();
        return IntPtr.Zero;
    }

    private void RefreshClipboardAvailability()
    {
        if (!_closed) UpdateClipboardAvailability(_clipboardContainsImage());
    }

    internal void UpdateClipboardAvailability(bool hasImage)
    {
        if (_closed) return;
        _clipboardButton.IsEnabled = hasImage;
        _clipboardButton.ToolTip = hasImage ? null
            : LocalizationService.Translate("The clipboard does not contain an image.");
    }

    private void RequestAttention()
    {
        if (_closed) return;
        Activate();
        if (Motion.Disabled)
        {
            StopAttention();
            return;
        }
        if (!_centered || _attentionTimer is not null
            || !User32.GetWindowRect(_captureHandle, out var bounds)) return;

        // Move the native window, not its content, to avoid clipping its edges or
        // mixing WPF coordinates between monitors. Repeated clicks cannot drift it.
        _attentionOrigin = new System.Drawing.Point(bounds.Left, bounds.Top);
        double amplitude = 4 * VisualTreeHelper.GetDpi(this).DpiScaleX;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        _attentionTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _attentionTimer.Tick += (_, _) =>
        {
            double progress = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds / 280;
            if (_closed || Motion.Disabled || progress >= 1)
            {
                StopAttention();
                return;
            }
            int offset = (int)Math.Round(amplitude * Math.Sin(progress * 4 * Math.PI) * (1 - progress));
            MoveForAttention(_attentionOrigin.X + offset, _attentionOrigin.Y);
        };
        _attentionTimer.Start();
    }

    private void StopAttention()
    {
        if (_attentionTimer is null) return;
        _attentionTimer.Stop();
        _attentionTimer = null;
        if (!_closed) MoveForAttention(_attentionOrigin.X, _attentionOrigin.Y);
    }

    private void MoveForAttention(int x, int y)
        => User32.SetWindowPos(_captureHandle, IntPtr.Zero, x, y, 0, 0,
            User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);

    private void SynchronizeRenderDpi()
    {
        // Transparent WPF windows can miss the first monitor DPI notification.
        // Reissue the actual destination DPI to this HWND after it has been shown,
        // so layout and text rendering agree with Windows before revealing it.
        var target = PopupWindowHelper.GetScaleForPoint(_openMonitorPoint);
        var render = VisualTreeHelper.GetDpi(this);
        if (Math.Abs(target.X - render.DpiScaleX) < .001 && Math.Abs(target.Y - render.DpiScaleY) < .001) return;
        if (!User32.GetWindowRect(_captureHandle, out var bounds)) return;
        var rect = Marshal.AllocHGlobal(Marshal.SizeOf<User32.RECT>());
        try
        {
            Marshal.StructureToPtr(bounds, rect, false);
            int dpiX = (int)Math.Round(target.X * 96), dpiY = (int)Math.Round(target.Y * 96);
            User32.SendMessage(_captureHandle, 0x02E0, new IntPtr(dpiX | (dpiY << 16)), rect);
        }
        finally { Marshal.FreeHGlobal(rect); }
    }

    private void CenterOnOpenMonitor()
    {
        // Read the final HWND size after layout and DPI adoption. Never write WPF
        // Left/Top afterward: their coordinate conversion drifts between 100% and 150%.
        var screen = System.Windows.Forms.Screen.FromPoint(_openMonitorPoint);
        var work = PopupWindowHelper.TryGetNativeMonitorInfo(screen, out _, out var nativeWork)
            ? nativeWork : screen.WorkingArea;
        if (!User32.GetWindowRect(_captureHandle, out var bounds)) return;
        int x = work.Left + Math.Max(0, (work.Width - (bounds.Right - bounds.Left)) / 2);
        int y = work.Top + Math.Max(0, (work.Height - (bounds.Bottom - bounds.Top)) / 2);
        User32.SetWindowPos(_captureHandle, User32.HWND_TOPMOST, x, y, 0, 0,
            User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }
}
