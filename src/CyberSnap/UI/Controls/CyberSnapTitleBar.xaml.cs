using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace CyberSnap.UI.Controls;

public partial class CyberSnapTitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(CyberSnapTitleBar),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShowPinButtonProperty =
        DependencyProperty.Register(
            nameof(ShowPinButton),
            typeof(bool),
            typeof(CyberSnapTitleBar),
            new PropertyMetadata(false, OnShowPinButtonChanged));

    public static readonly DependencyProperty IsPinActiveProperty =
        DependencyProperty.Register(
            nameof(IsPinActive),
            typeof(bool),
            typeof(CyberSnapTitleBar),
            new PropertyMetadata(false, OnIsPinActiveChanged));

    public static readonly DependencyProperty CloseToolTipProperty =
        DependencyProperty.Register(
            nameof(CloseToolTip),
            typeof(string),
            typeof(CyberSnapTitleBar),
            new PropertyMetadata(null, OnCloseToolTipChanged));

    public event EventHandler? CloseRequested;
    public event EventHandler? PinRequested;

    private Window? _subscribedWindow;
    /// <summary>
    /// When a ContextMenu closes from an outside click, WPF closes it before our
    /// button handler runs. Without this cooldown the same click reopens the menu,
    /// and PlacementMode.MousePoint (the default) can park it at screen (0,0) under
    /// mixed/150% DPI + AllowsTransparency windows.
    /// </summary>
    private DateTime _contextMenuClosedAt = DateTime.MinValue;

    public CyberSnapTitleBar()
    {
        InitializeComponent();
        Loaded += (s, e) =>
        {
            if (OwnerWindow is { } window)
            {
                if (_subscribedWindow != window)
                {
                    if (_subscribedWindow != null)
                    {
                        _subscribedWindow.StateChanged -= Window_StateChanged;
                    }
                    _subscribedWindow = window;
                    _subscribedWindow.StateChanged += Window_StateChanged;
                }
            }
            RefreshIcons();
        };
        Unloaded += (s, e) =>
        {
            if (_subscribedWindow != null)
            {
                _subscribedWindow.StateChanged -= Window_StateChanged;
                _subscribedWindow = null;
            }
        };
        IsVisibleChanged += (_, _) => RefreshIcons();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        RefreshIcons();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool ShowPinButton
    {
        get => (bool)GetValue(ShowPinButtonProperty);
        set => SetValue(ShowPinButtonProperty, value);
    }

    public bool IsPinActive
    {
        get => (bool)GetValue(IsPinActiveProperty);
        set => SetValue(IsPinActiveProperty, value);
    }

    public string CloseToolTip
    {
        get => (string)GetValue(CloseToolTipProperty);
        set => SetValue(CloseToolTipProperty, value);
    }

    private static void OnShowPinButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CyberSnapTitleBar tb && tb.PinBtn != null)
            tb.PinBtn.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnCloseToolTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CyberSnapTitleBar tb && tb.CloseBtn != null)
            tb.CloseBtn.ToolTip = ResolveCloseToolTip(tb);
    }

    private static string ResolveCloseToolTip(CyberSnapTitleBar tb) =>
        !string.IsNullOrEmpty(tb.CloseToolTip)
            ? Services.LocalizationService.Translate(tb.CloseToolTip)
            : Services.LocalizationService.Translate("Close");

    /// <summary>
    /// Re-applies the localized title-bar button tooltips. Called by owner windows
    /// after a language switch, since LocalizationService.ApplyTo caches the
    /// already-translated tooltip strings as their translation source.
    /// </summary>
    public void RefreshTooltips()
    {
        bool isMaximized = OwnerWindow?.WindowState == WindowState.Maximized;
        MinimizeBtn.ToolTip = Services.LocalizationService.Translate("Minimize");
        MaximizeBtn.ToolTip = Services.LocalizationService.Translate(isMaximized ? "Restore" : "Maximize");
        CloseBtn.ToolTip = ResolveCloseToolTip(this);
        ApplyTooltipPlacement(MinimizeBtn);
        ApplyTooltipPlacement(MaximizeBtn);
        ApplyTooltipPlacement(CloseBtn);
    }

    /// <summary>Anchor a title-bar button tooltip above the button instead of WPF's default
    /// cursor-relative placement (tips drifted under the pointer and could spill off-window
    /// on long texts).</summary>
    private static void ApplyTooltipPlacement(FrameworkElement element)
    {
        System.Windows.Controls.ToolTipService.SetPlacement(element, System.Windows.Controls.Primitives.PlacementMode.Top);
        System.Windows.Controls.ToolTipService.SetPlacementTarget(element, element);
        System.Windows.Controls.ToolTipService.SetVerticalOffset(element, -6);
    }

    private static void OnIsPinActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CyberSnapTitleBar tb) tb.RefreshPinIcon();
    }

    public void RefreshIcons()
    {
        TitleLogo.Source = OwnerWindow?.Icon ?? ThemedLogo.Square(18);
        var titleIcon = TitleBarIconColor;
        MinimizeIcon.Source = Helpers.FluentIcons.RenderWpf("minimize", titleIcon, 18);

        bool isMaximized = OwnerWindow?.WindowState == WindowState.Maximized;
        string maxIconId = isMaximized ? "restore" : "maximize";
        MaximizeIcon.Source = Helpers.FluentIcons.RenderWpf(maxIconId, titleIcon, 18);
        RefreshTooltips();

        // If the pointer is still over Close, keep the high-contrast hover glyph.
        var closeIconColor = CloseBtn.IsMouseOver ? TitleBarCloseHoverIconColor : titleIcon;
        CloseIcon.Source = Helpers.FluentIcons.RenderWpf("close", closeIconColor, 18);
        // Hamburger burger menu icon
        BurgerIcon.Source = RenderHamburgerIcon(titleIcon, 18);
        // "Open editor" shortcut \u2014 the Fluent "Compose" icon (shared with the tray/widget menus)
        AnnotationIcon.Source = Helpers.FluentIcons.RenderWpf("compose", titleIcon, 18);
        AnnotationIcon.Opacity = 1.0;

        // About is a compact info window — no maximize (same idea as Toast / widget chrome).
        MaximizeBtn.Visibility = OwnerWindow is AboutWindow
            ? Visibility.Collapsed
            : Visibility.Visible;

        RefreshPinIcon();

        InitializeActionBtn(titleIcon);
    }

    private void RefreshPinIcon()
    {
        if (PinIcon == null) return;
        var pinColor = IsPinActive
            ? System.Drawing.Color.FromArgb(230, 220, 92, 92)
            : System.Drawing.Color.FromArgb(190, Theme.TextSecondary.R, Theme.TextSecondary.G, Theme.TextSecondary.B);
        PinIcon.Source = Helpers.FluentIcons.RenderWpf("pin", pinColor, 18, active: true);
        PinBtn.ToolTip = LocalizationService.Translate(IsPinActive ? "Unpin" : "Pin");
        ApplyTooltipPlacement(PinBtn);
    }

    private void InitializeActionBtn(System.Drawing.Color titleIcon)
    {
        if (OwnerWindow is SettingsWindow settingsWin)
        {
            // Hide old shortcuts — replaced by the burger menu
            AnnotationBtn.Visibility = Visibility.Collapsed;
            ActionBtn.Visibility = Visibility.Collapsed;

            // Burger menu with toggles + shortcuts
            BurgerBtn.Visibility = Visibility.Visible;
            BurgerBtn.ToolTip = LocalizationService.Translate("Menu");

            var menu = new ContextMenu();
            menu.SetResourceReference(ContextMenu.StyleProperty, "HistoryActionsMenuStyle");

            // Editor shortcut
            var editorItem = new MenuItem
            {
                Header = LocalizationService.Translate("Annotations Editor"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("compose", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open the post-capture editor for annotations.")
            };
            editorItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => Editor.EditorForm.ShowEditorEmptyOrPrompt());
            };
            menu.Items.Add(editorItem);

            // Gallery shortcut
            var galleryItem = new MenuItem
            {
                Header = LocalizationService.Translate("Capture Gallery"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("history", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open the Capture Gallery")
            };
            galleryItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowHistory());
            };
            menu.Items.Add(galleryItem);

            // Achievements / Logros
            var achievementsItem = new MenuItem
            {
                Header = LocalizationService.Translate("Achievements"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("trophy", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open Achievements")
            };
            achievementsItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowAchievements());
            };
            menu.Items.Add(achievementsItem);

            menu.Items.Add(new Separator());

            // Setup wizard
            var wizardItem = new MenuItem
            {
                Header = LocalizationService.Translate("Setup wizard"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("gear", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Re-run the setup wizard")
            };
            wizardItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => settingsWin.RunSetupWizard());
            };
            menu.Items.Add(wizardItem);

            // About CyberSnap
            var aboutItem = new MenuItem
            {
                Header = LocalizationService.Translate("About CyberSnap"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("info", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open About CyberSnap")
            };
            aboutItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowAbout());
            };
            menu.Items.Add(aboutItem);

            menu.Opened += (_, _) =>
            {
                System.Windows.Controls.ToolTipService.SetIsEnabled(BurgerBtn, false);
            };

            menu.Closed += (_, _) =>
            {
                RecordContextMenuClosed();
                System.Windows.Controls.ToolTipService.SetIsEnabled(BurgerBtn, true);
            };

            BurgerBtn.ContextMenu = menu;
        }
        else if (OwnerWindow is HistoryWindow)
        {
            AnnotationBtn.Visibility = Visibility.Collapsed;

            ActionBtn.Visibility = Visibility.Visible;
            ActionBtn.ToolTip = LocalizationService.Translate("Menu");
            // Render hamburger icon ☰ as bitmap (streamline set has no "menu"/"navigation" icon)
            ActionIcon.Source = RenderHamburgerIcon(titleIcon, 18);

            // Build burger menu with toggles + Configuration
            var menu = new ContextMenu();
            menu.SetResourceReference(ContextMenu.StyleProperty, "HistoryActionsMenuStyle");

            var searchToggle = new MenuItem
            {
                Header = LocalizationService.Translate("Search bar"),
                IsCheckable = true,
                ToolTip = LocalizationService.Translate("Show or hide the search bar")
            };
            searchToggle.Checked += (_, _) => ToggleSetting("ShowImageSearchBar", true);
            searchToggle.Unchecked += (_, _) => ToggleSetting("ShowImageSearchBar", false);
            menu.Items.Add(searchToggle);

            var pruneToggle = new MenuItem
            {
                Header = LocalizationService.Translate("Auto-Pruning"),
                IsCheckable = true,
                ToolTip = LocalizationService.Translate("Show or hide the auto-pruning controls")
            };
            pruneToggle.Checked += (_, _) => ToggleSetting("ShowAutoPrune", true);
            pruneToggle.Unchecked += (_, _) => ToggleSetting("ShowAutoPrune", false);
            menu.Items.Add(pruneToggle);

            menu.Opened += (_, _) =>
            {
                var settings = ((App)Application.Current).GetSettings();
                searchToggle.IsChecked = settings.ShowImageSearchBar;
                pruneToggle.IsChecked = settings.ShowAutoPrune;
            };

            menu.Items.Add(new Separator());

            var configItem = new MenuItem
            {
                Header = LocalizationService.Translate("Configuration..."),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("gear", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open the full Configuration window")
            };
            configItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                // Defer to avoid layout jump when context menu closes
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowSettings("gallery"));
            };
            menu.Items.Add(configItem);

            var achievementsItem = new MenuItem
            {
                Header = LocalizationService.Translate("Achievements"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("trophy", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open Achievements")
            };
            achievementsItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowAchievements());
            };
            menu.Items.Add(achievementsItem);

            var aboutItem = new MenuItem
            {
                Header = LocalizationService.Translate("About CyberSnap"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("info", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open About CyberSnap")
            };
            aboutItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowAbout());
            };
            menu.Items.Add(aboutItem);

            menu.Closed += (_, _) => RecordContextMenuClosed();

            ActionBtn.ContextMenu = menu;
        }
        else if (OwnerWindow is AboutWindow or AchievementsWindow)
        {
            AnnotationBtn.Visibility = Visibility.Collapsed;
            ActionBtn.Visibility = Visibility.Collapsed;
            BurgerBtn.Visibility = Visibility.Collapsed;
        }
        else if (OwnerWindow is not null)
        {
            // OCR / Trimmer / Capture Preview / other chrome windows: About + Configuration
            AnnotationBtn.Visibility = Visibility.Collapsed;
            ActionBtn.Visibility = Visibility.Collapsed;

            BurgerBtn.Visibility = Visibility.Visible;
            BurgerBtn.ToolTip = LocalizationService.Translate("Menu");

            var menu = new ContextMenu();
            menu.SetResourceReference(ContextMenu.StyleProperty, "HistoryActionsMenuStyle");

            var configItem = new MenuItem
            {
                Header = LocalizationService.Translate("Configuration..."),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("gear", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open the full Configuration window")
            };
            configItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowSettings());
            };
            menu.Items.Add(configItem);

            var achievementsItem = new MenuItem
            {
                Header = LocalizationService.Translate("Achievements"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("trophy", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open Achievements")
            };
            achievementsItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowAchievements());
            };
            menu.Items.Add(achievementsItem);

            if (OwnerWindow is CapturePreviewDialog previewWindow)
            {
                var autoCloseToggle = new MenuItem
                {
                    Header = LocalizationService.Translate("Capture preview auto-close"),
                    IsCheckable = true,
                    ToolTip = LocalizationService.Translate("The preview window auto-closes when the timer expires.")
                };
                autoCloseToggle.Checked += (_, _) => previewWindow.SetAutoCloseEnabled(true);
                autoCloseToggle.Unchecked += (_, _) => previewWindow.SetAutoCloseEnabled(false);
                menu.Opened += (_, _) => autoCloseToggle.IsChecked = previewWindow.IsAutoCloseEnabled;
                menu.Items.Add(autoCloseToggle);
            }

            var aboutItem = new MenuItem
            {
                Header = LocalizationService.Translate("About CyberSnap"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("info", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open About CyberSnap")
            };
            aboutItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowAbout());
            };
            menu.Items.Add(aboutItem);

            menu.Opened += (_, _) =>
            {
                System.Windows.Controls.ToolTipService.SetIsEnabled(BurgerBtn, false);
            };
            menu.Closed += (_, _) =>
            {
                RecordContextMenuClosed();
                System.Windows.Controls.ToolTipService.SetIsEnabled(BurgerBtn, true);
            };

            BurgerBtn.ContextMenu = menu;
        }
        else
        {
            AnnotationBtn.Visibility = Visibility.Collapsed;
            ActionBtn.Visibility = Visibility.Collapsed;
            BurgerBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void RecordContextMenuClosed()
    {
        _contextMenuClosedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Opens/closes a title-bar ContextMenu without the reopen-at-(0,0) glitch under DPI scaling.
    /// </summary>
    private void ToggleContextMenu(ContextMenu? menu, FrameworkElement target)
    {
        if (menu is null)
            return;

        if (menu.IsOpen)
        {
            menu.IsOpen = false;
            return;
        }

        // The outside-click that closed the menu reaches this handler next; treat it as a toggle-off.
        if ((DateTime.UtcNow - _contextMenuClosedAt).TotalMilliseconds < 250)
            return;

        menu.PlacementTarget = target;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 2;
        menu.IsOpen = true;
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (e.ClickCount == 2)
        {
            if (OwnerWindow is not AboutWindow)
                ToggleMaximize();
            return;
        }

        try { OwnerWindow?.DragMove(); } catch { }
    }

    private void MinimizeBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (OwnerWindow is { } window)
            window.WindowState = WindowState.Minimized;
    }

    private void MaximizeBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        if (OwnerWindow is not { } window || window is AboutWindow)
            return;

        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        RefreshIcons();
    }

    private void PinBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PinRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CloseBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void TitleBtn_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border border)
            return;

        var isClose = ReferenceEquals(border, CloseBtn);
        border.Background = Theme.Brush(isClose ? Theme.DangerHover : Theme.AccentHover);
        // Red hover wash washes out the muted gray X — switch to white for contrast.
        if (isClose)
            CloseIcon.Source = Helpers.FluentIcons.RenderWpf("close", TitleBarCloseHoverIconColor, 18);
    }

    private void TitleBtn_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border)
            return;

        border.Background = System.Windows.Media.Brushes.Transparent;
        if (ReferenceEquals(border, CloseBtn))
            CloseIcon.Source = Helpers.FluentIcons.RenderWpf("close", TitleBarIconColor, 18);
    }

    private static System.Drawing.Color TitleBarIconColor =>
        System.Drawing.Color.FromArgb(210, Theme.TextSecondary.R, Theme.TextSecondary.G, Theme.TextSecondary.B);

    private static System.Drawing.Color TitleBarCloseHoverIconColor =>
        System.Drawing.Color.FromArgb(255, 255, 255, 255);

    private void BurgerBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Swallow mouse-down so the title bar doesn't start a DragMove under the button.
        e.Handled = true;
    }

    private void BurgerBtn_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleContextMenu(BurgerBtn.ContextMenu, BurgerBtn);
    }

    private void AnnotationBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (OwnerWindow is SettingsWindow)
        {
            CyberSnap.UI.Editor.EditorForm.ShowEditorEmptyOrPrompt();
        }
    }

    private void ActionBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Swallow mouse-down so History's burger click doesn't start a window drag.
        e.Handled = true;
        if (OwnerWindow is SettingsWindow)
        {
            ((App)Application.Current).ShowHistory();
        }
    }

    private void ActionBtn_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (OwnerWindow is HistoryWindow)
            ToggleContextMenu(ActionBtn.ContextMenu, ActionBtn);
    }

    private static void ToggleSetting(string propertyName, bool value)
    {
        ((App)Application.Current).ToggleHistorySetting(propertyName, value);
    }

    /// <summary>Renders a hamburger menu icon (☰) as a WPF bitmap.</summary>
    private static System.Windows.Media.Imaging.BitmapSource RenderHamburgerIcon(System.Drawing.Color color, int size)
    {
        var text = "\u2630"; // ☰ trigram for heaven = hamburger icon
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var typeface = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI Symbol"),
                System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal, System.Windows.FontStretches.Normal);
            // Slightly translucent to match other title bar icons
            var wpfColor = System.Windows.Media.Color.FromArgb(220, color.R, color.G, color.B);
            var brush = new System.Windows.Media.SolidColorBrush(wpfColor);
            var formatted = new System.Windows.Media.FormattedText(text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight, typeface, size * 0.9, brush, 1.0);
            dc.DrawText(formatted, new System.Windows.Point(0, -2));
        }
        var renderTarget = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        renderTarget.Render(visual);
        return renderTarget;
    }
}
