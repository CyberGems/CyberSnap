using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CyberSnap.Helpers;
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
    /// <summary>
    /// Which title button received the current press. Set on preview-down (which runs
    /// before bubble handlers act). Menu buttons only toggle on mouse-up when the press
    /// started on them: acting on mouse-down (e.g. maximize resizes the window
    /// synchronously) can shift buttons under a stationary cursor, otherwise delivering
    /// the up to a neighbor that would wrongly open its menu.
    /// </summary>
    private Border? _pressOrigin;

    public CyberSnapTitleBar()
    {
        InitializeComponent();
        AttachTactileFeedback();
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
        DonateBtn.ToolTip = Services.LocalizationService.Translate("Donate");
        CloseBtn.ToolTip = ResolveCloseToolTip(this);
        ApplyTooltipPlacement(MinimizeBtn);
        ApplyTooltipPlacement(MaximizeBtn);
        ApplyTooltipPlacement(DonateBtn);
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
        DonateIcon.Source = Helpers.FluentIcons.RenderWpf("heart", titleIcon, 18, active: DonateBtn.IsMouseOver);
        MinimizeIcon.Source = Helpers.FluentIcons.RenderWpf("minimize", titleIcon, 18, active: MinimizeBtn.IsMouseOver);

        bool isMaximized = OwnerWindow?.WindowState == WindowState.Maximized;
        string maxIconId = isMaximized ? "restore" : "maximize";
        // Keep the maximize glyph at its regular geometry on hover. Its filled variant
        // expands toward the viewBox edges and gets clipped at title-bar scale.
        MaximizeIcon.Source = Helpers.FluentIcons.RenderWpf(maxIconId, titleIcon, 18);
        RefreshTooltips();

        // If the pointer is still over Close, keep the high-contrast hover glyph.
        var closeIconColor = CloseBtn.IsMouseOver ? TitleBarCloseHoverIconColor : titleIcon;
        CloseIcon.Source = Helpers.FluentIcons.RenderWpf("close", closeIconColor, 18, active: CloseBtn.IsMouseOver);
        // Hamburger burger menu icon (crisp Fluent vector)
        BurgerIcon.Source = Helpers.FluentIcons.RenderWpf("menu", titleIcon, 18, active: BurgerBtn.IsMouseOver);
        // "Open editor" shortcut \u2014 the Fluent "Compose" icon (shared with the tray/widget menus)
        AnnotationIcon.Source = Helpers.FluentIcons.RenderWpf("compose", titleIcon, 18, active: AnnotationBtn.IsMouseOver);
        AnnotationIcon.Opacity = 1.0;

        // About is a compact info window — no maximize (same idea as Toast / widget chrome).
        MaximizeBtn.Visibility = OwnerWindow is AboutWindow
            ? Visibility.Collapsed
            : Visibility.Visible;

        RefreshPinIcon();

        InitializeActionBtn(titleIcon);
        BurgerSeparator.Visibility = BurgerBtn.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        WindowControlsSeparator.Visibility =
            BurgerBtn.Visibility == Visibility.Visible || DonateBtn.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;

        if (DonateBtn.IsMouseOver) ApplyButtonHoverVisual(DonateBtn, true);
        if (BurgerBtn.IsMouseOver) ApplyButtonHoverVisual(BurgerBtn, true);
        if (AnnotationBtn.IsMouseOver) ApplyButtonHoverVisual(AnnotationBtn, true);
        if (ActionBtn.IsMouseOver) ApplyButtonHoverVisual(ActionBtn, true);
        if (PinBtn.IsMouseOver) ApplyButtonHoverVisual(PinBtn, true);
        if (MinimizeBtn.IsMouseOver) ApplyButtonHoverVisual(MinimizeBtn, true);
        if (MaximizeBtn.IsMouseOver) ApplyButtonHoverVisual(MaximizeBtn, true);
        if (CloseBtn.IsMouseOver) ApplyButtonHoverVisual(CloseBtn, true);
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

    private static System.Windows.Controls.Image CreateMenuIcon(string id, System.Drawing.Color color, int size = 16)
    {
        var img = new System.Windows.Controls.Image
        {
            Source = Helpers.FluentIcons.RenderWpf(id, color, size),
            Width = size,
            Height = size,
            SnapsToDevicePixels = true,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    /// <summary>Builds the "Help" submenu: contextual wiki page, homepage, update check and About.</summary>
    private MenuItem CreateHelpSubmenu(ContextMenu rootMenu, string wikiPage, string wikiHeaderKey, string wikiTooltipKey, System.Drawing.Color titleIcon)
    {
        var helpItem = new MenuItem
        {
            Header = LocalizationService.Translate("Help"),
            Icon = CreateMenuIcon("question", titleIcon, 16)
        };
        helpItem.SetResourceReference(FrameworkElement.StyleProperty, "HistoryActionsMenuItem");
        helpItem.SetResourceReference(MenuItem.ItemContainerStyleProperty, "HistoryActionsMenuItem");

        var wikiItem = new MenuItem
        {
            Header = LocalizationService.Translate(wikiHeaderKey),
            Icon = CreateMenuIcon("question", titleIcon, 16),
            ToolTip = LocalizationService.Translate(wikiTooltipKey)
        };
        wikiItem.Click += (_, _) =>
        {
            rootMenu.IsOpen = false;
            WikiLinks.Open(wikiPage);
        };

        var homepageItem = new MenuItem
        {
            Header = LocalizationService.Translate("CyberGems Website..."),
            Icon = CreateMenuIcon("home", titleIcon, 16),
            ToolTip = LocalizationService.Translate("Visit CyberGems website")
        };
        homepageItem.Click += (_, _) =>
        {
            rootMenu.IsOpen = false;
            WikiLinks.OpenUrl(WikiLinks.HomepageUrl);
        };

        var updatesItem = new MenuItem
        {
            Header = LocalizationService.Translate("Check for Updates..."),
            Icon = CreateMenuIcon("redo", titleIcon, 16),
            ToolTip = LocalizationService.Translate("Check for the latest version")
        };
        updatesItem.Click += (_, _) =>
        {
            rootMenu.IsOpen = false;
            ((App)Application.Current).ShowAboutAndCheckForUpdates();
        };

        var aboutItem = new MenuItem
        {
            Header = LocalizationService.Translate("About CyberSnap..."),
            Icon = CreateMenuIcon("info", titleIcon, 16),
            ToolTip = LocalizationService.Translate("Open About CyberSnap")
        };
        aboutItem.Click += (_, _) =>
        {
            rootMenu.IsOpen = false;
            _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Background,
                () => ((App)Application.Current).ShowAbout());
        };

        helpItem.Items.Add(wikiItem);
        helpItem.Items.Add(homepageItem);
        helpItem.Items.Add(updatesItem);
        helpItem.Items.Add(aboutItem);
        return helpItem;
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
            StyleBurgerMenu(menu);

            // Editor shortcut
            var editorItem = new MenuItem
            {
                Header = WithEllipsis(LocalizationService.Translate("Annotations Editor")),
                Icon = CreateMenuIcon("compose", titleIcon, 16),
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
                Header = WithEllipsis(LocalizationService.Translate("Capture Gallery")),
                Icon = CreateMenuIcon("history", titleIcon, 16),
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
                Header = LocalizationService.Translate("Achievements..."),
                Icon = CreateMenuIcon("trophy", titleIcon, 16),
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
                Header = WithEllipsis(LocalizationService.Translate("Setup wizard")),
                Icon = CreateMenuIcon("gear", titleIcon, 16),
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

            // Help (wiki / homepage / updates / about)
            menu.Items.Add(CreateHelpSubmenu(menu, WikiLinks.SettingsPage,
                "Wiki: Settings...", "Open the Settings page in the CyberSnap wiki.", titleIcon));

            ApplyMenuItemStyles(menu);

            menu.Opened += (_, _) =>
            {
                ApplyButtonHoverVisual(BurgerBtn, true);
                System.Windows.Controls.ToolTipService.SetIsEnabled(BurgerBtn, false);
            };

            menu.Closed += (_, _) =>
            {
                RecordContextMenuClosed();
                if (!BurgerBtn.IsMouseOver)
                    ApplyButtonHoverVisual(BurgerBtn, false);
                System.Windows.Controls.ToolTipService.SetIsEnabled(BurgerBtn, true);
            };

            BurgerBtn.ContextMenu = menu;
        }
        else if (OwnerWindow is HistoryWindow)
        {
            AnnotationBtn.Visibility = Visibility.Collapsed;

            ActionBtn.Visibility = Visibility.Visible;
            ActionBtn.ToolTip = LocalizationService.Translate("Menu");
            ActionIcon.Source = Helpers.FluentIcons.RenderWpf("menu", titleIcon, 18);

            // Build burger menu with toggles + Configuration
            var menu = new ContextMenu();
            StyleBurgerMenu(menu);

            var searchToggle = new MenuItem
            {
                Header = LocalizationService.Translate("Search bar"),
                IsCheckable = true,
                ToolTip = LocalizationService.Translate("Show or hide the search bar")
            };
            searchToggle.Checked += (_, _) => ToggleSetting("ShowImageSearchBar", true);
            searchToggle.Unchecked += (_, _) => ToggleSetting("ShowImageSearchBar", false);
            menu.Items.Add(searchToggle);

            menu.Opened += (_, _) =>
            {
                ApplyButtonHoverVisual(ActionBtn, true);
                var settings = ((App)Application.Current).GetSettings();
                searchToggle.IsChecked = settings.ShowImageSearchBar;
            };

            menu.Items.Add(new Separator());

            var configItem = new MenuItem
            {
                Header = WithEllipsis(LocalizationService.Translate("Gallery settings")),
                Icon = CreateMenuIcon("gear", titleIcon, 16),
                ToolTip = LocalizationService.Translate("Open Gallery settings")
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
                Header = LocalizationService.Translate("Achievements..."),
                Icon = CreateMenuIcon("trophy", titleIcon, 16),
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

            menu.Items.Add(CreateHelpSubmenu(menu, WikiLinks.GalleryPage,
                "Wiki: Gallery...", "Open the Gallery page in the CyberSnap wiki.", titleIcon));

            ApplyMenuItemStyles(menu);

            menu.Closed += (_, _) =>
            {
                RecordContextMenuClosed();
                if (!ActionBtn.IsMouseOver)
                    ActionBtn.Background = System.Windows.Media.Brushes.Transparent;
            };

            ActionBtn.ContextMenu = menu;
        }
        else if (OwnerWindow is AboutWindow)
        {
            AnnotationBtn.Visibility = Visibility.Collapsed;
            ActionBtn.Visibility = Visibility.Collapsed;
            BurgerBtn.Visibility = Visibility.Collapsed;
        }
        else if (OwnerWindow is not null)
        {
            // OCR / Trimmer / Capture Preview / Achievements / other chrome windows: About + Configuration
            AnnotationBtn.Visibility = Visibility.Collapsed;
            ActionBtn.Visibility = Visibility.Collapsed;

            BurgerBtn.Visibility = Visibility.Visible;
            BurgerBtn.ToolTip = LocalizationService.Translate("Menu");

            var menu = new ContextMenu();
            StyleBurgerMenu(menu);

            if (OwnerWindow is CapturePreviewDialog previewWindow)
            {
                var autoCloseToggle = new MenuItem
                {
                    Header = LocalizationService.Translate("Autoclose this window"),
                    IsCheckable = true,
                    ToolTip = LocalizationService.Translate("The preview window auto-closes when the timer expires.")
                };
                autoCloseToggle.Checked += (_, _) => previewWindow.SetAutoCloseEnabled(true);
                autoCloseToggle.Unchecked += (_, _) => previewWindow.SetAutoCloseEnabled(false);
                menu.Opened += (_, _) => autoCloseToggle.IsChecked = previewWindow.IsAutoCloseEnabled;
                menu.Items.Add(autoCloseToggle);
                menu.Items.Add(new Separator());
            }

            var configItem = new MenuItem
            {
                Header = LocalizationService.Translate("Configuration..."),
                Icon = CreateMenuIcon("gear", titleIcon, 16),
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

            if (OwnerWindow is not AchievementsWindow)
            {
                var achievementsItem = new MenuItem
                {
                    Header = LocalizationService.Translate("Achievements..."),
                    Icon = CreateMenuIcon("trophy", titleIcon, 16),
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
            }

            // Contextual wiki page per chrome window (defaults to the wiki home).
            string wikiPage = OwnerWindow switch
            {
                OcrResultWindow => WikiLinks.OcrAndTranslationPage,
                VideoTrimmerWindow => WikiLinks.ScreenRecordingPage,
                CapturePreviewDialog => WikiLinks.CapturePreviewPage,
                _ => WikiLinks.HomePage
            };
            string wikiHeaderKey = OwnerWindow switch
            {
                OcrResultWindow => "Wiki: OCR & Translation...",
                VideoTrimmerWindow => "Wiki: Screen Recording...",
                CapturePreviewDialog => "Wiki: Capture Preview...",
                _ => "Wiki: CyberSnap Help..."
            };
            string wikiTooltipKey = OwnerWindow switch
            {
                OcrResultWindow => "Open the OCR & Translation page in the CyberSnap wiki.",
                VideoTrimmerWindow => "Open the Screen Recording page in the CyberSnap wiki.",
                CapturePreviewDialog => "Open the Capture Preview page in the CyberSnap wiki.",
                _ => "Open the CyberSnap wiki home page."
            };
            menu.Items.Add(CreateHelpSubmenu(menu, wikiPage, wikiHeaderKey, wikiTooltipKey, titleIcon));

            ApplyMenuItemStyles(menu);

            menu.Opened += (_, _) =>
            {
                ApplyButtonHoverVisual(BurgerBtn, true);
                System.Windows.Controls.ToolTipService.SetIsEnabled(BurgerBtn, false);
            };
            menu.Closed += (_, _) =>
            {
                RecordContextMenuClosed();
                if (!BurgerBtn.IsMouseOver)
                    ApplyButtonHoverVisual(BurgerBtn, false);
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

    private void StyleBurgerMenu(ContextMenu menu)
    {
        menu.SetResourceReference(ContextMenu.StyleProperty, "HistoryActionsMenuStyle");
    }

    private static void ApplyMenuItemStyles(ContextMenu menu)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.SetResourceReference(FrameworkElement.StyleProperty, "HistoryActionsMenuItem");
    }

    private static string WithEllipsis(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        if (text.EndsWith("...", StringComparison.Ordinal) || text.EndsWith("…", StringComparison.Ordinal))
            return text;
        return text + "...";
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

    private void DonateBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DonationLinks.Open();
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

        ApplyButtonHoverVisual(border, true);
        PlayHoverPop(border, true);
    }

    private void TitleBtn_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border border)
            return;

        if (ReferenceEquals(border, BurgerBtn) && BurgerBtn.ContextMenu?.IsOpen == true)
            return;
        if (ReferenceEquals(border, ActionBtn) && ActionBtn.ContextMenu?.IsOpen == true)
            return;

        ApplyButtonHoverVisual(border, false);
        // The pointer may leave mid-press (release happens outside); always settle.
        PlayHoverPop(border, false);
        ReleasePressScale(border, hovered: false);
    }

    private void ApplyButtonHoverVisual(Border border, bool hovered)
    {
        bool isClose = ReferenceEquals(border, CloseBtn);
        border.Background = hovered
            ? Theme.Brush(isClose ? Theme.DangerHover : Theme.BgHover)
            : System.Windows.Media.Brushes.Transparent;

        var iconColor = hovered
            ? (isClose ? TitleBarCloseHoverIconColor : TitleBarHoverIconColor)
            : TitleBarIconColor;
        bool active = hovered;

        if (ReferenceEquals(border, DonateBtn))
            DonateIcon.Source = Helpers.FluentIcons.RenderWpf("heart", iconColor, 18, active);
        else if (ReferenceEquals(border, BurgerBtn))
            BurgerIcon.Source = Helpers.FluentIcons.RenderWpf("menu", iconColor, 18, active);
        else if (ReferenceEquals(border, AnnotationBtn))
            AnnotationIcon.Source = Helpers.FluentIcons.RenderWpf("compose", iconColor, 18, active);
        else if (ReferenceEquals(border, ActionBtn))
            ActionIcon.Source = Helpers.FluentIcons.RenderWpf("menu", iconColor, 18, active);
        else if (ReferenceEquals(border, PinBtn))
        {
            if (hovered)
                PinIcon.Source = Helpers.FluentIcons.RenderWpf("pin", iconColor, 18, active: true);
            else
                RefreshPinIcon();
        }
        else if (ReferenceEquals(border, MinimizeBtn))
            MinimizeIcon.Source = Helpers.FluentIcons.RenderWpf("minimize", iconColor, 18, active);
        else if (ReferenceEquals(border, MaximizeBtn))
        {
            string maxIconId = OwnerWindow?.WindowState == WindowState.Maximized ? "restore" : "maximize";
            MaximizeIcon.Source = Helpers.FluentIcons.RenderWpf(maxIconId, iconColor, 18);
        }
        else if (ReferenceEquals(border, CloseBtn))
            CloseIcon.Source = Helpers.FluentIcons.RenderWpf("close", iconColor, 18, active);
    }

    #region Tactile micro-animations (visual-haptic feedback)

    // Shared by every window: all title-bar buttons live in this control, so wiring
    // press/hover scale here covers Settings, History, About, OCR, QR, Trimmer,
    // Capture Preview and Achievements at once.
    private const double PressedButtonScale = 0.88;
    private const double PressedIconScale = 0.84;
    private const double HoverIconScale = 1.1;
    private const double PressedIconOpacity = 0.7;
    private const double RestIconOpacity = 0.95;
    private const int PressMs = 90;
    private const int HoverMs = 120;
    private const int ReleaseMs = 200;

    private IEnumerable<(Border Button, System.Windows.Controls.Image Icon)> TitleButtons()
    {
        yield return (DonateBtn, DonateIcon);
        yield return (BurgerBtn, BurgerIcon);
        yield return (AnnotationBtn, AnnotationIcon);
        yield return (ActionBtn, ActionIcon);
        yield return (PinBtn, PinIcon);
        yield return (MinimizeBtn, MinimizeIcon);
        yield return (MaximizeBtn, MaximizeIcon);
        yield return (CloseBtn, CloseIcon);
    }

    private void AttachTactileFeedback()
    {
        foreach (var (button, icon) in TitleButtons())
        {
            if (button is null || icon is null)
                continue;

            EnsureScale(button);
            EnsureScale(icon);
            icon.Opacity = RestIconOpacity;

            button.PreviewMouseLeftButtonDown += TitleBtn_Press;
            button.PreviewMouseLeftButtonUp += TitleBtn_Release;
            button.PreviewTouchDown += TitleBtn_Press;
            button.PreviewTouchUp += TitleBtn_Release;
            button.PreviewStylusDown += TitleBtn_Press;
            button.PreviewStylusUp += TitleBtn_Release;
            button.Unloaded += TitleBtn_ClearAnimations;
        }
    }

    private void TitleBtn_Press(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
            return;

        _pressOrigin = border;
        var icon = IconFor(border);
        AnimateScale(border, PressedButtonScale, PressMs, Motion.SmoothOut);
        if (icon is not null)
        {
            AnimateScale(icon, PressedIconScale, PressMs, Motion.SmoothOut);
            AnimateOpacity(icon, PressedIconOpacity, PressMs, Motion.SmoothOut);
        }
    }

    private void TitleBtn_Release(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
            return;

        ReleasePressScale(border, hovered: border.IsMouseOver);
    }

    private void TitleBtn_ClearAnimations(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border border)
            return;

        border.BeginAnimation(UIElement.OpacityProperty, null);
        border.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        border.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (border.RenderTransform is ScaleTransform buttonScale)
            buttonScale.ScaleX = buttonScale.ScaleY = 1.0;

        var icon = IconFor(border);
        if (icon is null)
            return;
        icon.BeginAnimation(UIElement.OpacityProperty, null);
        icon.Opacity = RestIconOpacity;
        icon.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        icon.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (icon.RenderTransform is ScaleTransform iconScale)
            iconScale.ScaleX = iconScale.ScaleY = 1.0;
    }

    private void PlayHoverPop(Border border, bool hovered)
    {
        var icon = IconFor(border);
        if (icon is null)
            return;

        // A gentle icon swell on hover plus a settle back on leave. Skipped while
        // pressed so the press squash wins over the hover swell.
        if (IsPressed(border))
            return;

        AnimateScale(icon, hovered ? HoverIconScale : 1.0, HoverMs, Motion.SmoothOut);
    }

    private void ReleasePressScale(Border border, bool hovered)
    {
        var releaseEase = Motion.Disabled
            ? null
            : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        AnimateScale(border, 1.0, ReleaseMs, releaseEase);

        var icon = IconFor(border);
        if (icon is null)
            return;
        AnimateScale(icon, hovered ? HoverIconScale : 1.0, ReleaseMs, releaseEase);
        AnimateOpacity(icon, RestIconOpacity, ReleaseMs, Motion.SmoothOut);
    }

    private System.Windows.Controls.Image? IconFor(Border border)
    {
        if (ReferenceEquals(border, DonateBtn)) return DonateIcon;
        if (ReferenceEquals(border, BurgerBtn)) return BurgerIcon;
        if (ReferenceEquals(border, AnnotationBtn)) return AnnotationIcon;
        if (ReferenceEquals(border, ActionBtn)) return ActionIcon;
        if (ReferenceEquals(border, PinBtn)) return PinIcon;
        if (ReferenceEquals(border, MinimizeBtn)) return MinimizeIcon;
        if (ReferenceEquals(border, MaximizeBtn)) return MaximizeIcon;
        if (ReferenceEquals(border, CloseBtn)) return CloseIcon;
        return null;
    }

    private static bool IsPressed(Border border) =>
        Mouse.LeftButton == MouseButtonState.Pressed && border.IsMouseOver;

    private static void EnsureScale(FrameworkElement element)
    {
        if (element.RenderTransform is not ScaleTransform)
        {
            element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            element.RenderTransform = new ScaleTransform(1.0, 1.0);
        }
        else if (element.RenderTransformOrigin == new System.Windows.Point(0, 0))
        {
            element.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        }
    }

    private static void AnimateScale(FrameworkElement element, double scale, int milliseconds, IEasingFunction? easing)
    {
        EnsureScale(element);
        if (Motion.Disabled)
        {
            element.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            element.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (element.RenderTransform is ScaleTransform st)
                st.ScaleX = st.ScaleY = scale;
            return;
        }

        element.RenderTransform.BeginAnimation(
            ScaleTransform.ScaleXProperty, Motion.To(scale, milliseconds, easing ?? Motion.SmoothOut));
        element.RenderTransform.BeginAnimation(
            ScaleTransform.ScaleYProperty, Motion.To(scale, milliseconds, easing ?? Motion.SmoothOut));
    }

    private static void AnimateOpacity(UIElement element, double opacity, int milliseconds, IEasingFunction? easing)
    {
        if (Motion.Disabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = opacity;
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, Motion.To(opacity, milliseconds, easing ?? Motion.SmoothOut));
    }

    #endregion

    private static System.Drawing.Color TitleBarIconColor =>
        System.Drawing.Color.FromArgb(210, Theme.TextSecondary.R, Theme.TextSecondary.G, Theme.TextSecondary.B);

    private static System.Drawing.Color TitleBarHoverIconColor =>
        System.Drawing.Color.FromArgb(255, Theme.Accent.R, Theme.Accent.G, Theme.Accent.B);

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
        if (!ReferenceEquals(_pressOrigin, BurgerBtn))
            return;
        _pressOrigin = null;
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
        if (!ReferenceEquals(_pressOrigin, ActionBtn))
            return;
        _pressOrigin = null;
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
