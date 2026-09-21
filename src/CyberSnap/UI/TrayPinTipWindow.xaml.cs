using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Text.RegularExpressions;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace CyberSnap.UI;

public partial class TrayPinTipWindow : Window
{
    private const int TailSize = 10;
    private readonly SettingsService _settingsService;
    private readonly TrayIcon _trayIcon;
    private bool _closing;

    public TrayPinTipWindow(SettingsService settingsService, TrayIcon trayIcon)
    {
        _settingsService = settingsService;
        _trayIcon = trayIcon;
        InitializeComponent();

        Theme.Refresh();
        Theme.ApplyTo(Resources);
        if (Application.Current != null)
            Theme.ApplyTo(Application.Current.Resources);

        TitleText.Text = T("Keep CyberSnap visible in the tray");
        SetBodyContent(T("Windows hides new tray icons behind the overflow (^). Drag CyberSnap onto the taskbar, or pin it in Windows Settings."));
        DontShowAgainCheck.Content = T("Don't show again");
        GotItBtn.Content = T("Got it");
        OpenSettingsBtn.Content = T("Open Windows Settings");
        CloseBtn.ToolTip = T("Dismiss");

        TipIcon.Source = ThemedLogo.Square(20);

        SourceInitialized += (_, _) =>
        {
            PopupWindowHelper.ApplyNoActivateChrome(this);
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                CaptureWindowExclusion.Apply(hwnd);
        };
        Closed += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                CaptureWindowExclusion.Unregister(hwnd);
        };
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            PositionNearTray();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.pin-tip.position", ex.Message, ex);
        }
    }

    private void PositionNearTray()
    {
        _trayIcon.TryGetIconScreenRect(out var anchor);
        if (anchor.Width <= 0 || anchor.Height <= 0)
            anchor = new System.Drawing.Rectangle(System.Windows.Forms.Cursor.Position, new System.Drawing.Size(16, 16));

        var edge = WindowsNotificationArea.GetTaskbarEdge(anchor);
        ApplyTail(edge);

        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point(anchor.X + anchor.Width / 2, anchor.Y + anchor.Height / 2));
        var physWork = screen.WorkingArea;

        try
        {
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            if (hwnd != IntPtr.Zero)
            {
                Native.User32.SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    physWork.X,
                    physWork.Y,
                    0,
                    0,
                    Native.User32.SWP_NOSIZE | Native.User32.SWP_NOACTIVATE | Native.User32.SWP_NOZORDER);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.pin-tip.setwindowpos", ex.Message, ex);
        }

        Rect PhysicalToWindowDips(System.Drawing.Rectangle r)
        {
            var tl = PointFromScreen(new System.Windows.Point(r.Left, r.Top));
            var br = PointFromScreen(new System.Windows.Point(r.Right, r.Bottom));
            return new Rect(
                Left + tl.X,
                Top + tl.Y,
                Math.Max(0, br.X - tl.X),
                Math.Max(0, br.Y - tl.Y));
        }

        UpdateLayout();
        double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        double windowHeight = ActualHeight > 0 ? ActualHeight : 220;
        if (windowWidth <= 0) windowWidth = 360;
        if (windowHeight <= 0) windowHeight = 220;

        var workArea = PhysicalToWindowDips(physWork);
        var anchorDip = PhysicalToWindowDips(anchor);
        double ax = anchorDip.X + anchorDip.Width / 2;
        double ay = anchorDip.Y + anchorDip.Height / 2;
        const double gap = 6;

        double left;
        double top;
        switch (edge)
        {
            case WindowsNotificationArea.TaskbarEdge.Top:
                left = ax - windowWidth / 2;
                top = anchorDip.Bottom + gap;
                break;
            case WindowsNotificationArea.TaskbarEdge.Left:
                left = anchorDip.Right + gap;
                top = ay - windowHeight / 2;
                break;
            case WindowsNotificationArea.TaskbarEdge.Right:
                left = anchorDip.Left - windowWidth - gap;
                top = ay - windowHeight / 2;
                break;
            default:
                left = ax - windowWidth / 2;
                top = anchorDip.Top - windowHeight - gap;
                break;
        }

        double minLeft = workArea.Left + 8;
        double maxLeft = workArea.Right - windowWidth - 8;
        double minTop = workArea.Top + 8;
        double maxTop = workArea.Bottom - windowHeight - 8;

        if (maxLeft < minLeft) left = workArea.Left + (workArea.Width - windowWidth) / 2;
        else left = Math.Clamp(left, minLeft, maxLeft);

        if (maxTop < minTop) top = workArea.Top + (workArea.Height - windowHeight) / 2;
        else top = Math.Clamp(top, minTop, maxTop);

        Left = left;
        Top = top;
    }

    private void ApplyTail(WindowsNotificationArea.TaskbarEdge edge)
    {
        TopTail.Visibility = Visibility.Collapsed;
        BottomTail.Visibility = Visibility.Collapsed;
        LeftTail.Visibility = Visibility.Collapsed;
        RightTail.Visibility = Visibility.Collapsed;
        TopTailRow.Height = new GridLength(0);
        BottomTailRow.Height = new GridLength(0);
        LeftTailCol.Width = new GridLength(0);
        RightTailCol.Width = new GridLength(0);

        switch (edge)
        {
            case WindowsNotificationArea.TaskbarEdge.Top:
                TopTail.Visibility = Visibility.Visible;
                TopTailRow.Height = new GridLength(TailSize);
                break;
            case WindowsNotificationArea.TaskbarEdge.Left:
                LeftTail.Visibility = Visibility.Visible;
                LeftTailCol.Width = new GridLength(TailSize);
                break;
            case WindowsNotificationArea.TaskbarEdge.Right:
                RightTail.Visibility = Visibility.Visible;
                RightTailCol.Width = new GridLength(TailSize);
                break;
            default:
                BottomTail.Visibility = Visibility.Visible;
                BottomTailRow.Height = new GridLength(TailSize);
                break;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismiss(markSeen: true);
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Dismiss(markSeen: true);

    private void GotItBtn_Click(object sender, RoutedEventArgs e) => Dismiss(markSeen: true);

    private void OpenSettingsBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowsNotificationArea.OpenIconSettings();
        Dismiss(markSeen: true);
    }

    private void Dismiss(bool markSeen)
    {
        if (_closing) return;
        _closing = true;
        try
        {
            if (markSeen && DontShowAgainCheck.IsChecked != false)
                MarkSeen();
            Close();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.pin-tip.close", ex.Message, ex);
        }
    }

    private void MarkSeen()
    {
        try
        {
            if (_settingsService.Settings.HasSeenTrayPinTip)
                return;
            _settingsService.Settings.HasSeenTrayPinTip = true;
            _settingsService.Save();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("tray.pin-tip.save", ex.Message, ex);
        }
    }

    private static string T(string text) => LocalizationService.Translate(text);

    private void SetBodyContent(string body)
    {
        const string marker = "(^)";
        int markerIndex = body.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            BodyText.Text = body;
            return;
        }

        string before = body[..markerIndex];
        string after = body[(markerIndex + marker.Length)..];
        Match match = Regex.Match(before, @"^(.*\s)(\S+\s+\S+\s*)$", RegexOptions.Singleline);
        string prefix = match.Success ? match.Groups[1].Value : before;
        string anchorText = match.Success ? match.Groups[2].Value.TrimEnd() : string.Empty;

        BodyText.Inlines.Clear();
        BodyText.Inlines.Add(new Run(prefix));

        var label = new TextBlock
        {
            Text = anchorText,
            FontFamily = BodyText.FontFamily,
            FontSize = BodyText.FontSize,
            Foreground = BodyText.Foreground,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var noWrapGroup = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        noWrapGroup.Children.Add(label);
        noWrapGroup.Children.Add(CreateTrayOverflowIndicator());

        BodyText.Inlines.Add(new InlineUIContainer(noWrapGroup)
        {
            BaselineAlignment = BaselineAlignment.Baseline,
        });
        BodyText.Inlines.Add(new Run(after));
    }

    private Border CreateTrayOverflowIndicator()
    {
        var chevron = new Path
        {
            Data = Geometry.Parse("M 1,6 L 4.5,2.5 L 8,6"),
            Stretch = Stretch.Fill,
            StrokeThickness = 1.5,
            Width = 9,
            Height = 9,
        };
        chevron.SetResourceReference(Shape.StrokeProperty, "ThemeTextSecondaryBrush");

        var indicator = new Border
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(2.5, 0, 2.5, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = chevron,
            VerticalAlignment = VerticalAlignment.Center,
        };
        indicator.SetResourceReference(Border.BorderBrushProperty, "ThemeInputBorderBrush");
        indicator.SetResourceReference(Border.BackgroundProperty, "ThemeTabHoverBrush");
        return indicator;
    }
}
