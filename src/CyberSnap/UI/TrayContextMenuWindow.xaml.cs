using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace CyberSnap.UI;

public partial class TrayContextMenuWindow : Window
{
    private readonly TrayIcon _trayIcon;
    private readonly System.Drawing.Point _clickPoint;
    private bool _isClosing = false;
    private bool _isCompact;
    private bool _showRecording = true;
    private bool _showTools = true;
    private bool _showGallery = true;
    private bool _panelMenuOpen;
    private System.Windows.Controls.Button? _panelMenuResetRow;
    private System.Windows.Controls.Primitives.Popup? _panelPopup;
    private WpfToolTip? _activeTooltip;
    private FrameworkElement? _activeTooltipOwner;

    public TrayContextMenuWindow(TrayIcon trayIcon, System.Drawing.Point clickPoint)
    {
        _trayIcon = trayIcon;
        _clickPoint = clickPoint;
        InitializeComponent();
        AddHandler(UIElement.PreviewMouseMoveEvent, new System.Windows.Input.MouseEventHandler(Window_PreviewMouseMove), true);

        // Refresh local theme resources to guarantee proper look on creation
        Theme.Refresh();
        Theme.ApplyTo(Resources);
        Theme.ApplyTo(Application.Current.Resources);

        LoadLocalizedLabels();
        LoadIcons();

        // Restore persisted panel state (no animation on first show).
        // Area, header and footer are the panel skeleton and are never hidden.
        var saved = SettingsService.LoadStatic();
        _isCompact = saved?.QuickPanelCompact ?? false;
        _showRecording = saved?.QuickPanelShowRecording ?? true;
        _showTools = saved?.QuickPanelShowTools ?? true;
        _showGallery = saved?.QuickPanelShowGallery ?? true;
        ApplyCompactMode(animate: false);
        ApplySectionVisibility(animate: false);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Physical cursor captured at tray click (Win32 virtual-screen pixels).
            var physicalCursor = _clickPoint;
            var screen = System.Windows.Forms.Screen.FromPoint(physicalCursor);
            var physWork = screen.WorkingArea;
            var physBounds = screen.Bounds;

            // Move onto the click monitor first so WPF adopts that monitor's per-monitor DPI.
            // Without this, Left/Top and PointFromScreen use the wrong scale on mixed-DPI setups
            // (e.g. 150% primary + 125% secondary) and the menu lands far away or off-screen.
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
                AppDiagnostics.LogWarning("traymenu.setwindowpos", ex.Message, ex);
            }

            // Map physical rects/points into this window's DIP space (same approach as Toast/Widget).
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

            var workArea = PhysicalToWindowDips(physWork);
            var screenArea = PhysicalToWindowDips(physBounds);

            var cursorLocal = PointFromScreen(new System.Windows.Point(physicalCursor.X, physicalCursor.Y));
            double cursorX = Left + cursorLocal.X;
            double cursorY = Top + cursorLocal.Y;

            UpdateLayout();
            double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
            double windowHeight = ActualHeight > 0 ? ActualHeight : 360;

            const double gap = 8;
            const double eps = 2; // ignore sub-pixel work-area vs bounds differences
            double left;
            double top;

            // Detect taskbar dock side from work area vs full bounds on the click monitor.
            bool taskbarLeft = workArea.Left > screenArea.Left + eps;
            bool taskbarRight = workArea.Right < screenArea.Right - eps;
            bool taskbarTop = workArea.Top > screenArea.Top + eps;

            if (taskbarLeft)
            {
                left = workArea.Left + gap;
                top = cursorY - (windowHeight / 2);
            }
            else if (taskbarRight)
            {
                left = workArea.Right - windowWidth - gap;
                top = cursorY - (windowHeight / 2);
            }
            else if (taskbarTop)
            {
                left = cursorX - (windowWidth / 2);
                top = workArea.Top + gap;
            }
            else
            {
                // Bottom taskbar (default) or auto-hide
                left = cursorX - (windowWidth / 2);
                top = workArea.Bottom - windowHeight - gap;
            }

            // Clamp so the ENTIRE window stays inside the work area.
            // Previous bug set left/top to (edge - gap) without subtracting size, which shoved
            // most of the menu off-screen (and made it "disappear" on the secondary monitor).
            double minLeft = workArea.Left + gap;
            double maxLeft = workArea.Right - windowWidth - gap;
            double minTop = workArea.Top + gap;
            double maxTop = workArea.Bottom - windowHeight - gap;

            if (maxLeft < minLeft) left = workArea.Left + (workArea.Width - windowWidth) / 2;
            else left = Math.Clamp(left, minLeft, maxLeft);

            if (maxTop < minTop) top = workArea.Top + (workArea.Height - windowHeight) / 2;
            else top = Math.Clamp(top, minTop, maxTop);

            Left = left;
            Top = top;

            Activate();
            Focus();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("traymenu.loaded", ex);
        }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Dismiss when clicking outside using the reentrancy-safe CloseMenu helper
        CloseMenu();
    }

    private void CloseMenu()
    {
        if (_isClosing) return;
        _isClosing = true;
        try
        {
            // A StaysOpen popup would otherwise outlive its panel as an orphan.
            ClosePanelMenu();
            Close();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("traymenu.close", ex.Message, ex);
        }
    }

    private void LoadLocalizedLabels()
    {
        TitleTextBlock.Text = $"CyberSnap  {UpdateService.GetCurrentVersionLabel()}";
        
        var app = Application.Current as App;
        bool updateAvailable = app?.LatestUpdateResult?.IsUpdateAvailable ?? false;
        if (updateAvailable)
        {
            UpdateLed.Visibility = Visibility.Visible;
            SetTooltip(UpdateLed, T("Update available"));
            SetTooltip(AboutHeaderBtn, $"{T("Open About CyberSnap")} ({T("Update available")})");
        }
        else
        {
            UpdateLed.Visibility = Visibility.Collapsed;
            UpdateLed.ToolTip = null;
            SetTooltip(AboutHeaderBtn, T("Open About CyberSnap"));
        }

        // Shorter labels for buttons
        AreaCaptureText.Text = T("Area");
        FullscreenText.Text = T("Fullscreen short");
        ActiveWindowText.Text = T("Active window short");
        RepeatLastAreaText.Text = T("Repeat short");
        ScrollQuickText.Text = T("Scrolling");
        OcrText.Text = T("OCR");
        QrText.Text = T("QR");
        ColorPickerText.Text = T("Color");
        RulerText.Text = T("Ruler");
        AnnotationsText.Text = T("Editor");
        GalleryText.Text = T("Gallery");
        
        // Tooltips — short action descriptions (labels stay compact on the tiles).
        SetTooltip(AreaCaptureBtn, T("Capture a rectangular region of the screen"));
        SetTooltip(ScrollQuickBtn, T("Capture a long scrolling page or window"));
        SetTooltip(FullscreenBtn, T("Fullscreen capture"));
        SetTooltip(ActiveWindowBtn, T("Active window"));
        SetTooltip(RepeatLastAreaBtn, T("Repeat last area"));
        SetTooltip(OcrBtn, T("Extract text from a region of the screen"));
        SetTooltip(QrBtn, T("Scan QR codes and barcodes on screen"));
        SetTooltip(ColorPickerBtn, T("Capture a color sample from the screen"));
        SetTooltip(RulerBtn, T("Measure distances on the screen"));
        SetTooltip(AnnotationsBtn, T("Open the annotations editor"));
        SetTooltip(GalleryBtn, T("Browse and manage your captures"));
        SetTooltip(GifRecordBtn, T("Record the screen as an animated GIF"));
        SetTooltip(VideoRecordBtn, T("Record the screen as an MP4 video"));

        SetTooltip(SettingsBtn, T("Open CyberSnap settings"));
        SetTooltip(AchievementsBtn, T("Open Achievements"));
        SetTooltip(DonateBtn, T("Donate to project"));
        System.Windows.Automation.AutomationProperties.SetName(DonateBtn, T("Donate"));
        SetTooltip(AboutBtn, T("Open About CyberSnap"));
        SetTooltip(ExitBtn, T("Quit CyberSnap"));
        SetTooltip(PanelMenuBtn, T("Panel options"));

        // Determine recording state and localize the compact record button.
        bool isRecording = Capture.RecordingForm.Current != null;
        if (isRecording)
        {
            VideoRecordText.Text = T("Stop recording");
            SetTooltip(VideoRecordBtn, T("Stop the current screen recording"));
            VideoRecordBtn.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            GifRecordBtn.IsEnabled = false;
        }
        else
        {
            VideoRecordText.Text = T("Record");
            SetTooltip(VideoRecordBtn, T("Record the screen as an MP4 video"));
            VideoRecordBtn.ClearValue(ForegroundProperty);
            GifRecordBtn.IsEnabled = true;
            GifRecordText.Text = T("Record") + " GIF";
        }
    }

    private void LoadIcons()
    {
        var fgColor = Theme.TextPrimary;
        // Primary capture matches the widget: accent cyan so it reads as the default action.
        var accentColor = Theme.Accent;

        AppLogoImage.Source = ThemedLogo.SquareGrayscale(20);

        AreaCaptureRing.Fill = new SolidColorBrush(accentColor);
        AreaCaptureCenter.Fill = new SolidColorBrush(accentColor);
        AreaCaptureCenterGlow.Color = accentColor;
        ScrollQuickIcon.Source = GetIcon("scrollCapture", fgColor, 22);
        FullscreenIcon.Source = GetIcon("fullscreen", fgColor, 22);
        ActiveWindowIcon.Source = GetIcon("activeWindow", fgColor, 22);
        RepeatLastAreaIcon.Source = GetIcon("captureBack", fgColor, 22);
        OcrIcon.Source = GetIcon("ocr", fgColor, 20);
        QrIcon.Source = GetIcon("scan", fgColor, 20);
        ColorPickerIcon.Source = GetIcon("picker", fgColor, 20);
        RulerIcon.Source = GetIcon("ruler", fgColor, 20);
        // Bottom row: larger assets for accessibility while keeping compact tile height.
        AnnotationsIcon.Source = GetIcon("compose", fgColor, 20);
        GalleryIcon.Source = GetIcon("history", fgColor, 20);

        SettingsIcon.Source = GetIcon("gear", fgColor, 20);
        AchievementsIcon.Source = GetIcon("trophy", fgColor, 20);
        // Donate rests in the same gray-cyan as trophy/info/gear so only Exit reads
        // as "danger" at rest; it reclaims rose #F43F5E on hover (see DonateBtn_MouseEnter).
        DonateIcon.Source = GetIcon("heart", fgColor, 20);
        AboutIcon.Source = GetIcon("info", fgColor, 20);
        ExitIcon.Source = GetDangerIcon("signOut", 20);
        PanelMenuIcon.Source = GetIcon("more", Theme.TextSecondary, 16);

        bool isRecording = Capture.RecordingForm.Current != null;
        if (isRecording)
        {
            VideoRecordIcon.Source = GetDangerIcon("play", 20);
        }
        else
        {
            VideoRecordIcon.Source = GetIcon("record", fgColor, 20);
            GifRecordIcon.Source = GetIcon("recordGif", fgColor, 20);
        }
    }

    private ImageSource? GetIcon(string id, System.Windows.Media.Color mediaColor, int size)
    {
        var drawingColor = System.Drawing.Color.FromArgb(mediaColor.A, mediaColor.R, mediaColor.G, mediaColor.B);
        return FluentIcons.RenderWpf(id, drawingColor, size);
    }

    private ImageSource? GetDangerIcon(string id, int size)
    {
        return FluentIcons.RenderWpf(id, System.Drawing.Color.FromArgb(239, 68, 68), size);
    }

    private static string T(string text) => LocalizationService.Translate(text);

    private void SetTooltip(FrameworkElement element, string text)
    {
        var tooltip = new WpfToolTip
        {
            Content = text,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse
        };

        tooltip.Opened += (_, _) =>
        {
            if (!ReferenceEquals(_activeTooltip, tooltip))
                DismissActiveTooltip();

            _activeTooltip = tooltip;
            _activeTooltipOwner = element;
        };
        tooltip.Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeTooltip, tooltip))
            {
                _activeTooltip = null;
                _activeTooltipOwner = null;
            }
        };

        element.ToolTip = tooltip;
        ToolTipService.SetInitialShowDelay(element, 400);
        ToolTipService.SetBetweenShowDelay(element, 0);
        ToolTipService.SetShowDuration(element, 5000);
    }

    private void Window_PreviewMouseMove(object sender, WpfMouseEventArgs e)
    {
        if (_activeTooltip is null || _activeTooltipOwner is null)
            return;

        if (e.OriginalSource is DependencyObject source &&
            !IsWithinElement(source, _activeTooltipOwner))
        {
            DismissActiveTooltip();
        }
    }

    private void DismissActiveTooltip()
    {
        if (_activeTooltip is not null)
            _activeTooltip.IsOpen = false;
    }

    private static bool IsWithinElement(DependencyObject source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is Visual || element is Visual3D)
            return VisualTreeHelper.GetParent(element);

        return LogicalTreeHelper.GetParent(element);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.close-btn", ex); }
    }

    private void PanelMenu_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Deterministic toggle: the StaysOpen popup takes no mouse capture, so every
            // press reaches Click truthfully — no timing guards needed.
            if (_panelPopup?.IsOpen == true)
            {
                ClosePanelMenu();
                return;
            }

            System.Windows.Controls.Primitives.ToggleButton MakeToggle(string text, bool isChecked, Action<bool> onToggle)
            {
                var row = new System.Windows.Controls.Primitives.ToggleButton
                {
                    Content = T(text),
                    IsChecked = isChecked,
                };
                row.SetResourceReference(FrameworkElement.StyleProperty, "PanelMenuCheckRow");
                row.Click += (_, _) =>
                {
                    try
                    {
                        onToggle(row.IsChecked == true);
                        RefreshResetItem();
                        RefreshResetItemDelayed();
                    }
                    catch (Exception ex) { AppDiagnostics.LogError("traymenu.panel-toggle", ex); }
                };
                return row;
            }

            var modesRow = MakeToggle("Capture modes", !_isCompact, checkedOn =>
            {
                _isCompact = !checkedOn;
                ApplyCompactMode(animate: true);
                SettingsService.SaveQuickPanelCompact(_isCompact);
            });
            var recordingRow = MakeToggle("Recording", _showRecording, checkedOn =>
            {
                _showRecording = checkedOn;
                ApplySectionVisibility(animate: true);
                SettingsService.SaveQuickPanelShowRecording(_showRecording);
            });
            var toolsRow = MakeToggle("Tools", _showTools, checkedOn =>
            {
                _showTools = checkedOn;
                ApplySectionVisibility(animate: true);
                SettingsService.SaveQuickPanelShowTools(_showTools);
            });
            var galleryRow = MakeToggle("Gallery and editor", _showGallery, checkedOn =>
            {
                _showGallery = checkedOn;
                ApplySectionVisibility(animate: true);
                SettingsService.SaveQuickPanelShowGallery(_showGallery);
            });

            var resetContent = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = System.Windows.VerticalAlignment.Center };
            var resetIconHost = new Grid { Width = 24, Margin = new Thickness(0, 0, 4, 0) };
            resetIconHost.Children.Add(new System.Windows.Controls.Image
            {
                Source = GetIcon("restore", Theme.TextPrimary, 16),
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
            resetContent.Children.Add(resetIconHost);
            resetContent.Children.Add(new TextBlock { Text = T("Reposition panel"), VerticalAlignment = System.Windows.VerticalAlignment.Center });

            var resetRow = new System.Windows.Controls.Button { Content = resetContent };
            resetRow.SetResourceReference(FrameworkElement.StyleProperty, "PanelMenuActionRow");
            resetRow.ToolTip = T("Brings the panel back into view when it extends beyond the screen");
            // Disabled rows hide their tooltip by default; this one explains exactly
            // why it is off, so keep it readable in both states.
            System.Windows.Controls.ToolTipService.SetShowOnDisabled(resetRow, true);
            resetRow.Click += (_, _) =>
            {
                try
                {
                    ClosePanelMenu();
                    ClampToWorkArea();
                }
                catch (Exception ex) { AppDiagnostics.LogError("traymenu.reposition", ex); }
            };
            _panelMenuResetRow = resetRow;

            Border Divider()
            {
                var divider = new Border
                {
                    Height = 1,
                    Margin = new Thickness(8, 4, 8, 4),
                    SnapsToDevicePixels = true,
                };
                divider.SetResourceReference(Border.BackgroundProperty, "ThemeSeparatorBrush");
                return divider;
            }

            var stack = new StackPanel();
            stack.Children.Add(modesRow);
            stack.Children.Add(Divider());
            stack.Children.Add(recordingRow);
            stack.Children.Add(toolsRow);
            stack.Children.Add(galleryRow);
            stack.Children.Add(Divider());
            stack.Children.Add(resetRow);

            var shell = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(4),
                MinWidth = 180,
                SnapsToDevicePixels = true,
                BorderThickness = new Thickness(1),
                Child = stack,
            };
            shell.SetResourceReference(Border.BackgroundProperty, "ThemeCardBrush");
            shell.SetResourceReference(Border.BorderBrushProperty, "ThemeInputBorderBrush");

            var popup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = PanelMenuBtn,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
                HorizontalOffset = 0,
                VerticalOffset = 2,
                AllowsTransparency = true,
                // Takes no mouse capture: ⋯ keeps receiving presses, so the toggle above
                // (and hover) always sees the truth. Dismissal is fully explicit.
                StaysOpen = true,
                Child = shell,
            };
            popup.Closed += (_, _) =>
            {
                _panelMenuOpen = false;
                _panelPopup = null;
                _panelMenuResetRow = null;
                SyncPanelMenuHover();
                // Sections may have grown the panel past the work area while choosing;
                // glide back now that the popup is gone.
                ClampToWorkArea();
            };
            popup.Opened += (_, _) =>
            {
                _panelMenuOpen = true;
                // Open-state indication: keep the highlight while choosing.
                try { PanelMenuBtn.Background = Theme.Brush(Theme.TabHoverBg); }
                catch { }
            };

            _panelPopup = popup;
            RefreshResetItem();
            RefreshResetItemDelayed();
            popup.IsOpen = true;
        }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.panel-menu", ex); }
    }

    private void ClosePanelMenu()
    {
        try
        {
            if (_panelPopup?.IsOpen == true)
                _panelPopup.IsOpen = false;
        }
        catch (Exception ex) { AppDiagnostics.LogWarning("traymenu.panel-menu-close", ex.Message, ex); }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            // Presses inside the popup never reach this window (separate visual tree),
            // so anything arriving here with the popup open is a dismissal press —
            // except on ⋯ itself, which Click toggles deterministically below.
            if (_panelPopup?.IsOpen != true)
                return;
            if (e.OriginalSource is DependencyObject source && IsWithinElement(source, PanelMenuBtn))
                return;
            ClosePanelMenu();
        }
        catch { }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        try
        {
            if (e.Key == Key.Escape && _panelPopup?.IsOpen == true)
            {
                ClosePanelMenu();
                e.Handled = true;
            }
        }
        catch { }
    }

    private void PanelMenuBtn_MouseEnter(object sender, WpfMouseEventArgs e)
    {
        try { PanelMenuBtn.Background = Theme.Brush(Theme.TabHoverBg); }
        catch { }
    }

    private void PanelMenuBtn_MouseLeave(object sender, WpfMouseEventArgs e)
    {
        // While the menu is open the cursor lives on the popup: keep the highlight
        // as the open-state indication instead of clearing it mid-choice.
        if (_panelMenuOpen)
            return;
        try { PanelMenuBtn.ClearValue(BackgroundProperty); }
        catch { }
    }

    /// <summary>
    /// Explicit hover sync for the ⋯ open-state indication (mirrors the suite title-bar
    /// buttons). With no capture in play IsMouseOver reads truthfully here.
    /// </summary>
    private void SyncPanelMenuHover()
    {
        try
        {
            if (PanelMenuBtn.IsMouseOver)
                PanelMenuBtn.Background = Theme.Brush(Theme.TabHoverBg);
            else
                PanelMenuBtn.ClearValue(BackgroundProperty);
        }
        catch { }
    }

    /// <summary>
    /// The reset entry is only meaningful when the panel actually overflows the work
    /// area. Re-checked after each toggle (once animations settle) so it lights up
    /// exactly when there is something to fix — never just because sections are hidden.
    /// </summary>
    private void RefreshResetItem()
    {
        try
        {
            if (_panelMenuResetRow is null)
                return;
            _panelMenuResetRow.IsEnabled = NeedsRefit();
        }
        catch { }
    }

    /// <summary>
    /// Deferred re-check: right after a toggle the window is still mid-animation, so
    /// the overflow measurement would be stale. Fires once, harmless if the menu or
    /// panel is already gone by then.
    /// </summary>
    private void RefreshResetItemDelayed()
    {
        try
        {
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(220),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                RefreshResetItem();
                RefreshResetItemDelayed();
            };
            timer.Start();
        }
        catch { }
    }

    /// <summary>Overflow check without moving anything (used to enable the reset entry).</summary>
    private bool NeedsRefit()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return false;
            if (!Native.User32.GetWindowRect(hwnd, out var rect))
                return false;
            var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea; // physical px
            return rect.Right > work.Right || rect.Left < work.Left
                || rect.Bottom > work.Bottom || rect.Top < work.Top;
        }
        catch { return false; }
    }

    /// <summary>
    /// Glides the panel back inside its monitor's work area after section toggles.
    /// Runs on menu close (not per toggle) so the open menu never detaches from ⋯,
    /// and animated with the same easing as the sections so there is no jump.
    /// </summary>
    private void ClampToWorkArea()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            if (!Native.User32.GetWindowRect(hwnd, out var rect))
                return;
            var work = System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea; // physical px

            int dx = 0, dy = 0;
            if (rect.Right > work.Right) dx = work.Right - rect.Right;
            if (rect.Left + dx < work.Left) dx = work.Left - rect.Left;
            if (rect.Bottom > work.Bottom) dy = work.Bottom - rect.Bottom;
            if (rect.Top + dy < work.Top) dy = work.Top - rect.Top;
            if (dx == 0 && dy == 0)
                return;

            var fromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                ?? System.Windows.Media.Matrix.Identity;
            var offset = fromDevice.Transform(new Vector(dx, dy));

            const double animDuration = 0.15; // seconds — same feel as the section slides
            var duration = new Duration(TimeSpan.FromSeconds(animDuration));
            var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

            if (!double.IsNaN(Left) && offset.X != 0)
            {
                double targetLeft = Left + offset.X;
                var leftAnim = new DoubleAnimation(Left, targetLeft, duration) { EasingFunction = ease };
                leftAnim.Completed += (_, _) =>
                {
                    BeginAnimation(Window.LeftProperty, null);
                    Left = targetLeft;
                };
                BeginAnimation(Window.LeftProperty, leftAnim);
            }

            if (!double.IsNaN(Top) && offset.Y != 0)
            {
                double targetTop = Top + offset.Y;
                var topAnim = new DoubleAnimation(Top, targetTop, duration) { EasingFunction = ease };
                topAnim.Completed += (_, _) =>
                {
                    BeginAnimation(Window.TopProperty, null);
                    Top = targetTop;
                };
                BeginAnimation(Window.TopProperty, topAnim);
            }
        }
        catch (Exception ex) { AppDiagnostics.LogWarning("traymenu.clamp", ex.Message, ex); }
    }

    private void ApplyCompactMode(bool animate)
    {
        const double animDuration = 0.15; // seconds — fast but smooth
        var duration = new Duration(TimeSpan.FromSeconds(animDuration));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        if (animate)
        {
            // Animate capture modes panel width to avoid size jump
            if (_isCompact)
            {
                // Collapse: animate Width from current to 0, then set Visibility
                double currentWidth = CaptureModesPanel.ActualWidth > 0 ? CaptureModesPanel.ActualWidth : 132;
                CaptureModesPanel.Width = currentWidth;
                var widthAnim = new DoubleAnimation(currentWidth, 0, duration) { EasingFunction = ease };
                widthAnim.Completed += (_, _) =>
                {
                    CaptureModesPanel.Visibility = Visibility.Collapsed;
                    CaptureModesPanel.Width = 132; // restore for next expand
                };
                CaptureModesPanel.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
            }
            else
            {
                // Expand: set Visibility first, then animate Width from 0 to 132
                CaptureModesPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                CaptureModesPanel.Width = 0;
                CaptureModesPanel.Visibility = Visibility.Visible;
                var widthAnim = new DoubleAnimation(0, 132, duration) { EasingFunction = ease };
                widthAnim.Completed += (_, _) =>
                {
                    CaptureModesPanel.BeginAnimation(FrameworkElement.WidthProperty, null);
                    CaptureModesPanel.Width = 132;
                };
                CaptureModesPanel.BeginAnimation(FrameworkElement.WidthProperty, widthAnim);
            }
        }
        else
        {
            // Instant (first show)
            CaptureModesPanel.Visibility = _isCompact ? Visibility.Collapsed : Visibility.Visible;
            CaptureModesPanel.Width = 132;
        }
    }

    private void ApplySectionVisibility(bool animate)
    {
        // Each block owns its top separator, so a single divider always travels with
        // its section and no orphaned lines are possible.
        AnimateBlock(RecordingBlock, _showRecording, animate);
        AnimateBlock(ToolsBlock, _showTools, animate);
        AnimateBlock(GalleryBlock, _showGallery, animate);
    }

    /// <summary>
    /// Same technique as the capture-modes collapse, rotated 90°: the container's
    /// height slides with clipping while everything inside (including the separator
    /// margins) rides along. Height-only on purpose — opacity fades on top of window
    /// reflow are what made the previous version stutter.
    /// </summary>
    private static void AnimateBlock(FrameworkElement block, bool show, bool animate)
    {
        const double animDuration = 0.15; // seconds — matches the capture-modes collapse
        var duration = new Duration(TimeSpan.FromSeconds(animDuration));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        block.BeginAnimation(FrameworkElement.HeightProperty, null);

        if (!animate)
        {
            // Instant (first show)
            block.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            block.Height = double.NaN;
            return;
        }

        if (show)
        {
            if (block.Visibility == Visibility.Visible)
                return; // already there; ignore stray re-toggles
            // Measure the natural height, then grow from 0.
            block.Height = double.NaN;
            block.Visibility = Visibility.Visible;
            block.UpdateLayout();
            double target = block.ActualHeight > 0 ? block.ActualHeight : 0;
            if (target <= 0)
            {
                block.Height = double.NaN;
                return;
            }

            block.Height = 0;
            var heightAnim = new DoubleAnimation(0, target, duration) { EasingFunction = ease };
            heightAnim.Completed += (_, _) =>
            {
                block.BeginAnimation(FrameworkElement.HeightProperty, null);
                block.Height = double.NaN;
            };
            block.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
        }
        else
        {
            if (block.Visibility != Visibility.Visible)
                return;
            // Collapse: animate Height from current to 0, then hide.
            double current = block.ActualHeight > 0 ? block.ActualHeight : 0;
            if (current <= 0)
            {
                block.Visibility = Visibility.Collapsed;
                block.Height = double.NaN;
                return;
            }

            block.Height = current;
            var heightAnim = new DoubleAnimation(current, 0, duration) { EasingFunction = ease };
            heightAnim.Completed += (_, _) =>
            {
                block.Visibility = Visibility.Collapsed;
                block.BeginAnimation(FrameworkElement.HeightProperty, null);
                block.Height = double.NaN; // restore for next expand
            };
            block.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
        }
    }

    private void DonateBtn_MouseEnter(object sender, WpfMouseEventArgs e)
    {
        try
        {
            DonateIcon.Source = FluentIcons.RenderWpf("heart", System.Drawing.Color.FromArgb(244, 63, 94), 20);
            DismissAboutHeaderToolTip();
        }
        catch { }
    }

    private void DonateBtn_MouseLeave(object sender, WpfMouseEventArgs e)
    {
        try
        {
            var media = Theme.TextPrimary;
            var drawing = System.Drawing.Color.FromArgb(media.A, media.R, media.G, media.B);
            DonateIcon.Source = FluentIcons.RenderWpf("heart", drawing, 20);
        }
        catch { }
    }

    private void AreaCapture_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerCapture(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.area-capture", ex); }
    }

    private void ScrollCapture_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerScrollCapture(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.scroll-capture", ex); }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerFullscreen(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.fullscreen", ex); }
    }

    private void ActiveWindow_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerActiveWindow(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.active-window", ex); }
    }

    private void RepeatLastArea_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerRepeatLastArea(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.repeat-last-area", ex); }
    }

    private void Ocr_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerOcr(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.ocr", ex); }
    }

    private void QrScan_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerScan(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.qr-scan", ex); }
    }

    private void ColorPicker_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerColorPicker(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.color-picker", ex); }
    }

    private void Ruler_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerRuler(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.ruler", ex); }
    }

    private void Annotations_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerAnnotationEditor(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.annotations", ex); }
    }

    private void Gallery_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerHistory(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.gallery", ex); }
    }

    private void VideoRecord_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            CloseMenu();
            bool isRecording = Capture.RecordingForm.Current != null;
            if (isRecording)
            {
                if (Capture.RecordingForm.Current != null)
                    Capture.RecordingForm.Current.RequestStop();
            }
            else
            {
                _trayIcon.TriggerRecord(RecordingFormat.MP4);
            }
        }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.video-record", ex); }
    }

    private void GifRecord_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerRecord(RecordingFormat.GIF); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.gif-record", ex); }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerSettings(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.settings", ex); }
    }

    private void Achievements_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerAchievements(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.achievements", ex); }
    }

    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); DonationLinks.Open(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.donate", ex); }
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerAbout(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.about", ex); }
    }

    private void AboutHeader_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            e.Handled = true;
            CloseMenu();
            _trayIcon.TriggerAbout();
        }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.about", ex); }
    }

    private void AboutHeader_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        try
        {
            System.Windows.Controls.ToolTipService.SetIsEnabled(AboutHeaderBtn, true);
            AboutHeaderBtn.Background = System.Windows.Media.Brushes.Transparent;
            TitleTextBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextPrimaryBrush");
            AppLogoImage.Source = ThemedLogo.Square(20);
            AppLogoImage.Opacity = 1;
        }
        catch { }
    }

    private void AboutHeader_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        try
        {
            AboutHeaderBtn.Background = System.Windows.Media.Brushes.Transparent;
            TitleTextBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ThemeTextSecondaryBrush");
            AppLogoImage.Source = ThemedLogo.SquareGrayscale(20);
            AppLogoImage.Opacity = 0.75;
            DismissAboutHeaderToolTip();
        }
        catch { }
    }

    private void TrayCloseBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        DismissAboutHeaderToolTip();
    }

    private void DismissAboutHeaderToolTip()
    {
        try
        {
            // Force-hide the currently visible tooltip when moving to another control.
            DismissActiveTooltip();
        }
        catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        try { CloseMenu(); _trayIcon.TriggerQuit(); }
        catch (Exception ex) { AppDiagnostics.LogError("traymenu.exit", ex); }
    }
}
