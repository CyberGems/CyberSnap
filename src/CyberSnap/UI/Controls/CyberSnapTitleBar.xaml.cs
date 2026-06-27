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

    public event EventHandler? CloseRequested;
    public event EventHandler? PinRequested;

    private Window? _subscribedWindow;

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

    private static void OnShowPinButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CyberSnapTitleBar tb && tb.PinBtn != null)
            tb.PinBtn.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OnIsPinActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CyberSnapTitleBar tb) tb.RefreshPinIcon();
    }

    public void RefreshIcons()
    {
        TitleLogo.Source = ThemedLogo.Square(18);
        var titleIcon = System.Drawing.Color.FromArgb(210, Theme.TextSecondary.R, Theme.TextSecondary.G, Theme.TextSecondary.B);
        MinimizeIcon.Source = Helpers.FluentIcons.RenderWpf("minimize", titleIcon, 18);

        bool isMaximized = OwnerWindow?.WindowState == WindowState.Maximized;
        string maxIconId = isMaximized ? "restore" : "maximize";
        MaximizeIcon.Source = Helpers.FluentIcons.RenderWpf(maxIconId, titleIcon, 18);
        MaximizeBtn.ToolTip = Services.LocalizationService.Translate(isMaximized ? "Restore" : "Maximize");

        CloseIcon.Source = Helpers.FluentIcons.RenderWpf("close", titleIcon, 18);
        // Hamburger burger menu icon
        BurgerIcon.Source = RenderHamburgerIcon(titleIcon, 18);
        // "Open editor" shortcut \u2014 the Fluent "Compose" icon (shared with the tray/widget menus)
        AnnotationIcon.Source = Helpers.FluentIcons.RenderWpf("compose", titleIcon, 18);
        AnnotationIcon.Opacity = 1.0;

        RefreshPinIcon();

        InitializeActionBtn(titleIcon);
    }

    private void RefreshPinIcon()
    {
        if (PinIcon == null) return;
        var titleIcon = System.Drawing.Color.FromArgb(210, Theme.TextSecondary.R, Theme.TextSecondary.G, Theme.TextSecondary.B);
        PinIcon.Source = Helpers.FluentIcons.RenderWpf("pin", titleIcon, 18, IsPinActive);
        PinBtn.ToolTip = LocalizationService.Translate(IsPinActive ? "Unpin" : "Pin");
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

            // Search settings toggle
            var searchItem = new MenuItem
            {
                Header = LocalizationService.Translate("Search settings"),
                IsCheckable = true,
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("search", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Show or hide the search bar")
            };
            searchItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => settingsWin.ToggleSearchBar());
            };
            menu.Items.Add(searchItem);

            menu.Items.Add(new Separator());

            // Editor shortcut
            var editorItem = new MenuItem
            {
                Header = LocalizationService.Translate("Editor..."),
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
                Header = LocalizationService.Translate("Gallery..."),
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

            menu.Items.Add(new Separator());

            // About CyberSnap
            var aboutItem = new MenuItem
            {
                Header = LocalizationService.Translate("About CyberSnap"),
                Icon = new System.Windows.Controls.Image { Source = Helpers.FluentIcons.RenderWpf("info", titleIcon, 16), Width = 16, Height = 16 },
                ToolTip = LocalizationService.Translate("Open the About section in Configuration")
            };
            aboutItem.Click += (_, _) =>
            {
                menu.IsOpen = false;
                _ = ((App)Application.Current).Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => ((App)Application.Current).ShowSettings("about"));
            };
            menu.Items.Add(aboutItem);

            menu.Opened += (_, _) =>
            {
                searchItem.IsChecked = settingsWin.IsSearchBarVisible();
                System.Windows.Controls.ToolTipService.SetIsEnabled(BurgerBtn, false);
            };

            menu.Closed += (_, _) =>
            {
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

            ActionBtn.ContextMenu = menu;
        }
        else
        {
            AnnotationBtn.Visibility = Visibility.Collapsed;
            ActionBtn.Visibility = Visibility.Collapsed;
        }
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (e.ClickCount == 2)
        {
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
        if (OwnerWindow is not { } window)
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

        border.Background = Theme.Brush(ReferenceEquals(border, CloseBtn) ? Theme.DangerHover : Theme.AccentHover);
    }

    private void TitleBtn_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Border border)
            border.Background = System.Windows.Media.Brushes.Transparent;
    }

    private void BurgerBtn_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (BurgerBtn.ContextMenu is { } menu)
        {
            menu.PlacementTarget = BurgerBtn;
            menu.IsOpen = true;
        }
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
        e.Handled = true;
        if (OwnerWindow is SettingsWindow)
        {
            ((App)Application.Current).ShowHistory();
        }
        else if (OwnerWindow is HistoryWindow)
        {
            if (ActionBtn.ContextMenu is { } menu)
            {
                menu.PlacementTarget = ActionBtn;
                menu.IsOpen = true;
            }
        }
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
