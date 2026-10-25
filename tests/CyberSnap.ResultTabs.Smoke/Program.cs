using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using CyberSnap.Services;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.UI;
using ZXing;

// Run on Windows: dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release
// Uses real WPF windows and temporary settings, without starting tray/IPC/hotkeys,
// calling translation providers, or changing the clipboard.
internal static partial class Program
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        Application app = args.Contains("--zoom-only") || args.Contains("--pin-only") || args.Contains("--pin-shadow-only") || args.Contains("--pin-chooser-only") || args.Contains("--pin-appearance-only") || args.Contains("--pin-lifecycle-only") || args.Contains("--widget-cursor-only") || args.Contains("--hotkeys-only") ? new ZoomFlowTestApp() : new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        // Real result windows request foreground. Isolate this automated fixture
        // from physical input on the shared desktop; all test edits are explicit.
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is KeyEventArgs or TextCompositionEventArgs or MouseButtonEventArgs or MouseWheelEventArgs)
                e.Cancel();
        };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"))
            .Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "ui", "clr-namespace:CyberSnap.UI;assembly=CyberSnap"),
            resources.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        if (args.Length == 3 && args[0] == "--widget-restart-check")
            return CheckWidgetRestart(args[1], args[2]);
        string temp = Path.Combine(Path.GetTempPath(), "CyberSnap-ResultTabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string settingsPath = Path.Combine(temp, "settings.json");
        using var settings = new SettingsService(settingsPath);
        if (app is ZoomFlowTestApp) SetZoomAppField(app, "_settingsService", settings);
        settings.Settings.InterfaceLanguage = "en";
        settings.Save();
        try
        {
            CheckSqliteRuntime();
            if (args.Contains("--pin-formats-only"))
            {
                CheckPinImageFormats(temp);
                Console.WriteLine($"PASS: {_checks} Pin image format checks.");
                return 0;
            }
            if (args.Contains("--pin-shadow-only"))
            {
                string? renders = args.FirstOrDefault(a => !a.StartsWith("--"));
                if (renders is not null) Directory.CreateDirectory(renders);
                CheckPinWindowShadow(renders);
                Console.WriteLine($"PASS: {_checks} Pin shadow checks.");
                return 0;
            }
            if (args.Contains("--pin-lifecycle-only"))
            {
                CheckPinMenuLifecycle();
                Console.WriteLine($"PASS: {_checks} Pin menu lifecycle checks.");
                return 0;
            }
            if (args.Contains("--pin-appearance-only"))
            {
                CheckPinImageAppearance(args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} Pin appearance checks.");
                return 0;
            }
            if (args.Contains("--pin-chooser-only"))
            {
                CheckPinChooserPresentation(args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} Pin chooser checks.");
                return 0;
            }
            if (args.Contains("--hotkeys-only"))
            {
                CheckUnifiedToolHotkeys(temp, settings, (CyberSnap.App)app, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} unified hotkey and toolbar keyboard checks.");
                return 0;
            }
            if (args.Contains("--widget-tooltips-only"))
            {
                CheckWidgetToolTips(settings, args.FirstOrDefault(a => !a.StartsWith("--")));
                CheckTrayToolTips(args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} widget and tray tooltip checks.");
                return 0;
            }
            if (args.Contains("--widget-animation-only"))
            {
                CheckWidgetCollapseAppearance(settings, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} widget collapse appearance checks.");
                return 0;
            }
            if (args.Contains("--widget-cursor-only"))
            {
                CheckWidgetCursorPreferences(temp);
                Console.WriteLine($"PASS: {_checks} widget cursor and Pin integration checks.");
                return 0;
            }
            if (args.Contains("--pin-only"))
            {
                CheckPin(temp, settings, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} Pin checks.");
                return 0;
            }
            if (args.Contains("--zoom-only"))
            {
                CheckZoom(temp, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} Zoom checks.");
                return 0;
            }
            if (args.Contains("--tools-only"))
            {
                CheckToolsSettings(temp, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} tool settings checks.");
                return 0;
            }
            if (args.Contains("--about-only"))
            {
                CheckAboutUpdates(settings, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} About update layout checks.");
                return 0;
            }
            if (args.Contains("--backups-only"))
            {
                CheckAutomaticBackups(temp, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} automatic backup checks.");
                return 0;
            }
            if (args.Contains("--tray-menu-only"))
            {
                CheckTrayToolTips(args.FirstOrDefault(a => !a.StartsWith("--")));
                CheckTrayMenuMonitors(args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} tray menu placement checks.");
                return 0;
            }
            if (args.Contains("--widget-active-only"))
            {
                CheckWidgetActiveMonitor(settings);
                Console.WriteLine($"PASS: {_checks} widget foreground checks.");
                return 0;
            }
            if (args.Contains("--widget-popup-only"))
            {
                CheckWidgetPopups(settings, args.FirstOrDefault(a => !a.StartsWith("--")));
                CheckWidgetActiveMonitor(settings);
                Console.WriteLine($"PASS: {_checks} widget popup, peek and foreground checks.");
                return 0;
            }
            if (args.Contains("--widget-monitors-only"))
            {
                CheckWidgetMonitors(settings);
                Console.WriteLine($"PASS: {_checks} widget monitor/DPI checks.");
                return 0;
            }
            if (args.Contains("--widget-preferences-only"))
            {
                CheckWidgetPreferences(temp, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} widget preference synchronization checks.");
                return 0;
            }
            if (args.Contains("--delivery-only"))
            {
                CheckDeliverySynchronization(temp, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} delivery synchronization checks.");
                return 0;
            }
            if (args.Contains("--widget-only"))
            {
                CheckWidgetToolTips(settings);
                CheckWidget(settings, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} widget checks.");
                return 0;
            }
            if (args.Contains("--trimmer-only"))
            {
                CheckTrimmer(settings, temp, args.FirstOrDefault(a => !a.StartsWith("--")));
                Console.WriteLine($"PASS: {_checks} Trimmer integration checks.");
                return 0;
            }
            CheckIndependentAutoCopy(temp);
            CheckZoom(temp, args.FirstOrDefault());
            CheckToolsSettings(temp, args.FirstOrDefault());
            CheckAutomaticBackups(temp, args.FirstOrDefault());
            CheckAboutUpdates(settings, args.FirstOrDefault());
            CheckTrayMenuMonitors();
            CheckWidgetMonitors(settings);
            CheckWidgetPopups(settings, args.FirstOrDefault());
            CheckWidgetActiveMonitor(settings);
            CheckWidgetPreferences(temp, args.FirstOrDefault());
            CheckDeliverySynchronization(temp, args.FirstOrDefault());
            CheckOcr(settings, args.FirstOrDefault());
            CheckQr(settings, args.FirstOrDefault());
            CheckFooters(settings, settingsPath, args.FirstOrDefault());
            CheckWidgetToolTips(settings);
            CheckWidget(settings, args.FirstOrDefault());
            CheckTrimmer(settings, temp, args.FirstOrDefault());
            Console.WriteLine($"PASS: {_checks} result-tab integration checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray())
            {
                if (window.GetType().Name is "OcrResultWindow" or "QrResultWindow")
                {
                    object host = Field(window, "_tabs");
                    while (window.IsVisible)
                        Call(host, "CloseActive");
                }
            }
            settings.Dispose();
            Directory.Delete(temp, recursive: true);
            app.Shutdown();
        }
    }

    private static void CheckOcr(SettingsService settings, string? renderDirectory)
    {
        var preview = new DrawingImage(new GeometryDrawing(Brushes.Cyan, null, new RectangleGeometry(new Rect(0, 0, 120, 80))));
        var window = OcrResultWindow.ShowResult("first OCR text", settings, preview, "first OCR text");
        Pump();
        var strip = Element<PreviewTabStrip>(window, "ResultTabStrip");
        var text = Element<TextBox>(window, "OcrTextBox");
        var search = Element<TextBox>(window, "SearchTextBox");
        var translated = Element<TextBox>(window, "TranslatedTextBox");
        object host = Field(window, "_tabs");
        object first = Property(host, "Active");
        Assert(strip.Visibility == Visibility.Collapsed, "OCR hides tabs for a single result");
        Assert(Element<TextBlock>(window, "CopyBtnText").Text == "Close", "OCR preserves auto-copy delivery state");
        text.Text = "edited OCR text with match and match";
        search.Text = "match";
        Call(window, "MoveToSearchMatch", 1);
        int matchIndex = (int)Field(window, "_currentMatchIndex");
        text.Select(7, 3);
        Call(window, "SetTranslationPanelExpanded", true, false);
        translated.Text = "traducción conservada";
        SelectTag(Element<ComboBox>(window, "FromLanguageCombo"), "en");
        translated.Text = "traducción conservada";

        var same = OcrResultWindow.ShowResult("second OCR", settings);
        Pump();
        Assert(ReferenceEquals(window, same), "OCR reuses its window");
        Assert(strip.Visibility == Visibility.Visible && text.Text == "second OCR", $"OCR adds and selects the new result (actual: {text.Text}, {strip.Visibility})");
        Assert(search.Text == "" && translated.Text == "", "new OCR result starts with isolated state");
        SelectTag(Element<ComboBox>(window, "FromLanguageCombo"), "es");
        Call(host, "ActivateAt", 0);
        Pump();
        Assert(text.Text == "edited OCR text with match and match", $"OCR restores edited text (actual: {text.Text})");
        Assert(text.SelectionStart == 7 && text.SelectionLength == 3, "OCR restores selection");
        Assert(search.Text == "match" && (int)Field(window, "_currentMatchIndex") == matchIndex, "OCR restores search and match");
        Assert(translated.Text == "traducción conservada", "OCR restores translation");
        Assert(ReferenceEquals(Element<System.Windows.Controls.Image>(window, "OcrPreviewImage").Source, preview), "OCR restores source preview");
        Assert(((ComboBoxItem)Element<ComboBox>(window, "FromLanguageCombo").SelectedItem).Tag as string == "en", "OCR restores language choice");
        Assert(settings.Settings.OcrDefaultTranslateFrom == "es", "tab switching does not change saved defaults");
        Assert(!text.CanUndo, "undo cannot restore text from another result");

        // Complete an inactive result without a network request. The same UI
        // apply path is used by asynchronous translation completion.
        Call(host, "ActivateAt", 1);
        SetProperty(first, "TranslatedText", "background translation");
        Call(window, "ApplyTranslationSession", first);
        Assert(translated.Text == "", "background translation cannot alter the selected result");
        Call(host, "ActivateAt", 0);
        Assert(translated.Text == "background translation", "background result is available when its tab is selected");
        using var pending = new CancellationTokenSource();
        SetProperty(first, "TranslationCts", pending);
        SetProperty(first, "CheckingTranslation", true);
        Call(host, "ActivateAt", 1);
        Assert(!pending.IsCancellationRequested, "switching tabs keeps translation alive");
        Call(host, "CloseAt", 0);
        Assert(pending.IsCancellationRequested, "closing an inactive tab cancels its translation");
        Assert(text.Text == "second OCR" && strip.Visibility == Visibility.Collapsed, "closing inactive OCR preserves selected result");
        Call(host, "Reopen");
        Assert(text.Text.StartsWith("edited OCR") && translated.Text == "background translation", "OCR can reopen its last closed result");

        settings.Settings.InterfaceLanguage = "es";
        window.RefreshLocalization();
        Pump();
        Assert(text.Text == "edited OCR text with match and match", $"localization refresh preserves recognized text (actual: {text.Text})");
        Assert(translated.Text == "background translation", "localization refresh preserves the current translation");
        Assert(LocalizationService.Translate("Close all results?") == "¿Cerrar todos los resultados?", "Spanish close-all text is available");
        Render(window, renderDirectory, "ocr-tabs-es.png");
        RespondToConfirmation(false);
        window.Close();
        Assert(window.IsVisible && !(bool)Property(host, "IsClosing"), "canceling close-all keeps the OCR window usable");
        Assert(ReferenceEquals(window, OcrResultWindow.ShowResult("third OCR", settings)), "OCR still accepts results after canceling close-all");
        RespondToConfirmation(true);
        window.Close();
        Assert(!window.IsVisible && (bool)Property(host, "IsClosing"), "confirming close-all closes OCR");
        window = OcrResultWindow.ShowResult("fresh OCR", settings);
        Assert(!ReferenceEquals(window, same), "OCR creates a fresh window after the last one closes");
        Call(Field(window, "_tabs"), "CloseActive");
    }

    private static void CheckQr(SettingsService settings, string? renderDirectory)
    {
        settings.Settings.InterfaceLanguage = "en";
        var window = QrResultWindow.ShowResult("https://example.com", BarcodeFormat.QR_CODE, settings, alreadyCopiedText: "https://example.com");
        Pump();
        object host = Field(window, "_tabs");
        var text = Element<TextBox>(window, "ContentTextBox");
        Assert(Element<PreviewTabStrip>(window, "ResultTabStrip").Visibility == Visibility.Collapsed, "QR hides tabs for one result");
        Assert(Element<TextBlock>(window, "CopyButtonText").Text == "Close", "QR preserves copied state");
        text.Select(3, 4);
        Assert(ReferenceEquals(window, QrResultWindow.ShowResult("12345", BarcodeFormat.CODE_128, settings)), "QR and barcodes reuse one window");
        Assert(Element<TextBlock>(window, "FormatText").Text == "Code 128" && text.Text == "12345", "QR switches format with content");
        Call(host, "ActivateAt", 0);
        Assert(text.Text == "https://example.com" && text.SelectionStart == 3 && text.SelectionLength == 4, "QR restores content and selection");
        Assert(Element<TextBlock>(window, "CopyButtonText").Text == "Close", "QR restores copied status");
        Call(host, "CloseActive");
        Assert(window.IsVisible && text.Text == "12345", "QR closes only the active result");
        Call(host, "Reopen");
        window.WindowState = WindowState.Minimized;
        QrResultWindow.ShowResult("aztec", BarcodeFormat.AZTEC, settings);
        Assert(window.WindowState == WindowState.Normal && text.Text == "aztec", "new QR results restore a minimized window");
        for (int i = 0; i < 12; i++)
            QrResultWindow.ShowResult("code " + i, BarcodeFormat.EAN_13, settings);
        Pump();
        Render(window, renderDirectory, "qr-tabs-en.png");
        Assert(Application.Current.Windows.OfType<QrResultWindow>().Count() == 1, "many scans still use one QR window");
        RespondToConfirmation(true);
        window.Close();
        Assert(!window.IsVisible, "QR close-all closes every result");
    }

    private static void SelectTag(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(item => item.Tag as string == tag);

    private static void CheckFooters(SettingsService settings, string settingsPath, string? renderDirectory)
    {
        var pixels = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = 1000, Height = 1000, Margin = 2 }
        }.Write("Green QR");
        var preview = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Bgra32, null, pixels.Pixels, pixels.Width * 4);
        preview.Freeze();
        foreach (string language in new[] { "en", "es" })
        foreach (double scale in new[] { 1.0, 1.4 })
        {
            settings.Settings.InterfaceLanguage = language;
            settings.Settings.UiScale = scale;
            settings.Settings.OcrShowResultWindow = true;
            settings.Settings.ScanShowResultWindow = true;
            settings.Save();
            var ocr = OcrResultWindow.ShowResult("Footer OCR sample", settings, preview);
            var qr = QrResultWindow.ShowResult("Green QR", BarcodeFormat.QR_CODE, settings, preview);
            CheckDeliveryOptions(ocr, settings, settingsPath, AutoCopyKind.Ocr);
            CheckDeliveryOptions(qr, settings, settingsPath, AutoCopyKind.Scan);
            OcrResultWindow.ShowResult("Second OCR sample", settings, preview);
            QrResultWindow.ShowResult("Green QR", BarcodeFormat.QR_CODE, settings, preview);

            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            {
                Theme.SetMode(mode);
                Theme.ApplyTo(Application.Current.Resources);
                ocr.ApplyTheme();
                qr.ApplyTheme();
                foreach (bool copied in new[] { false, true })
                {
                    ocr.Width = 900 * scale;
                    ocr.Height = 600 * scale;
                    qr.Width = 560 * scale;
                    qr.Height = 440 * scale;
                    Call(ocr, "SetTranslationPanelExpanded", false, false);
                    SetField(ocr, "_copiedText", copied ? "Second OCR sample" : null);
                    SetField(qr, "_copiedText", copied ? "Green QR" : null);
                    Call(ocr, "ApplyCopyActionState");
                    Call(qr, "ApplyCopyActionState");
                    CheckFooterBounds(ocr, "OcrWorkbench", "CopyOnlyBtn", "CopyBtn", "CopiedBadge");
                    CheckFooterBounds(qr, "QrWorkbench", "CopyOnlyButton", "CopyButton", "CopyStatusText");
                    ocr.Width = ocr.MinWidth;
                    ocr.Height = ocr.MinHeight;
                    qr.Width = qr.MinWidth;
                    qr.Height = qr.MinHeight;
                    Call(ocr, "SetTranslationPanelExpanded", true, false);
                    CheckFooterBounds(ocr, "OcrWorkbench", "CopyOnlyBtn", "CopyBtn", "CopiedBadge");
                    CheckFooterBounds(qr, "QrWorkbench", "CopyOnlyButton", "CopyButton", "CopyStatusText");
                }
            }
            Console.WriteLine($"PASS: footer layout for {language}, scale {scale:0.0}, dark/light, copied/uncopied, default/minimum sizes.");
            Theme.SetMode(AppThemeMode.Dark);
            Theme.ApplyTo(Application.Current.Resources);
            ocr.ApplyTheme();
            qr.ApplyTheme();
            ocr.Width = 900 * scale;
            ocr.Height = 600 * scale;
            qr.Width = 560 * scale;
            qr.Height = 440 * scale;
            if (scale == 1.0)
            {
                Render(ocr, renderDirectory, $"ocr-footer-{language}.png");
                Render(qr, renderDirectory, $"qr-footer-{language}.png");
                qr.Width = qr.MinWidth;
                qr.Height = qr.MinHeight;
                Render(qr, renderDirectory, $"qr-footer-{language}-minimum.png");
            }
            Call(Field(ocr, "_tabs"), "CloseActive");
            Call(Field(ocr, "_tabs"), "CloseActive");
            Call(Field(qr, "_tabs"), "CloseActive");
            Call(Field(qr, "_tabs"), "CloseActive");
        }
    }

    private static void CheckDeliveryOptions(Window window, SettingsService settings, string settingsPath, AutoCopyKind kind)
    {
        var auto = Element<CheckBox>(window, "AutoCopyCheck");
        var show = Element<CheckBox>(window, "ShowWindowCheck");
        var autoLabel = Element<Label>(window, "AutoCopyLabel");
        var showLabel = Element<Label>(window, "ShowWindowLabel");
        Assert((string)autoLabel.Content == LocalizationService.Translate("Auto-copy")
            && (string)showLabel.Content == LocalizationService.Translate("Show this window"), "delivery labels are visible and localized");
        Assert(ReferenceEquals(AutomationProperties.GetLabeledBy(auto), autoLabel)
            && ReferenceEquals(AutomationProperties.GetLabeledBy(show), showLabel), "delivery toggles have associated accessible labels");
        show.IsChecked = true;
        auto.IsChecked = false;
        show.IsChecked = false;
        Assert(auto.IsChecked == true && show.IsChecked == false && AutoCopyPreferences.ShouldCopy(settings.Settings, kind), "hiding the window enables auto-copy");
        auto.IsChecked = false;
        Assert(auto.IsChecked == false && show.IsChecked == true, "disabling the last delivery restores the window");
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonUpEvent
        };
        autoLabel.RaiseEvent(click);
        Assert(auto.IsChecked == true && show.IsChecked == true, "clicking a label toggles its option and permits both deliveries");
        Call(window, "SetShowResultWindow", false);
        Assert(show.IsChecked == false && auto.IsChecked == true, "title-bar preference changes refresh footer toggles");

        settings.FlushPendingWrites();
        using var external = new SettingsService(settingsPath);
        external.Load();
        bool savedShowWindow = kind == AutoCopyKind.Ocr ? external.Settings.OcrShowResultWindow : external.Settings.ScanShowResultWindow;
        Assert(!savedShowWindow && AutoCopyPreferences.ShouldCopy(external.Settings, kind), "delivery choices persist to the settings file");
        ResultDelivery.SetCopyEnabled(external.Settings, kind, false);
        external.Settings.ImageAutoCopyToClipboard = false;
        var otherKinds = CopyFlags(external.Settings);
        external.Save();
        SettingsService.PublishAutoCopyPreferences(external.Settings);
        Assert(auto.IsChecked == false && show.IsChecked == true, "footer refreshes preferences changed by a separate service");
        auto.IsChecked = true;
        AssertOnlyCopyChanged(otherKinds, settings.Settings, kind, true, "footer enables only its own auto-copy preference");
        external.FlushPendingWrites();
        settings.FlushPendingWrites();
    }

    private static void CheckFooterBounds(Window window, string workbenchName, string copyName, string closeName, string badgeName)
    {
        Pump();
        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var footer = Element<FrameworkElement>(window, "FooterPanel");
        Rect footerBounds = Bounds(footer);
        Check(Bounds(Element<FrameworkElement>(window, workbenchName)).Bottom <= footerBounds.Top + 0.5,
            $"{window.GetType().Name} preview respects the footer ({window.ActualWidth:0}x{window.ActualHeight:0})");
        Rect rootBounds = Bounds(root);
        foreach (string name in new[] { "AutoCopyLabel", "AutoCopyCheck", "ShowWindowLabel", "ShowWindowCheck", copyName, closeName, badgeName })
        {
            var control = Element<FrameworkElement>(window, name);
            if (control.Visibility != Visibility.Visible)
                continue;
            Rect bounds = Bounds(control);
            Check(bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= rootBounds.Left - 0.5
                && bounds.Right <= rootBounds.Right + 0.5 && bounds.Top >= footerBounds.Top - 0.5
                && bounds.Bottom <= rootBounds.Bottom + 0.5, $"{name} is fully inside the footer");
        }
        var copy = Element<Button>(window, copyName);
        var close = Element<Button>(window, closeName);
        Check(copy.ActualHeight >= 38 && close.ActualHeight >= 38 && copy.Padding.Left >= 16 && close.Padding.Left >= 16,
            "copy actions have larger targets and padding");
        var badge = Element<FrameworkElement>(window, badgeName);
        if (badge.Visibility == Visibility.Visible)
            Check(badge.ActualHeight + 8 <= copy.ActualHeight && !Bounds(badge).IntersectsWith(Bounds(copy)), "copied badge is distinct from copy buttons");

        Rect Bounds(FrameworkElement control) => control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
        void Check(bool condition, string message) => Assert(condition, message, announce: false);
    }

    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static T Element<T>(Window window, string name) where T : class => (T)window.FindName(name);
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
    private static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void RespondToConfirmation(bool confirm)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        int ticks = 0;
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.GetType().Name == "ThemedConfirmDialog");
            if (dialog is not null)
            {
                timer.Stop();
                // Exercise the dialog's real result contract.
                dialog.GetType().GetField("_confirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, confirm);
                dialog.DialogResult = confirm;
            }
            else if (++ticks > 100)
            {
                timer.Stop();
                throw new TimeoutException("The close-all confirmation did not appear.");
            }
        };
        timer.Start();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Render(FrameworkElement window, string? directory, string name)
    {
        if (directory is null)
            return;
        Pump();
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }

    private static void Assert(bool condition, string message, bool announce = true)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
        _checks++;
        if (announce)
            Console.WriteLine("PASS: " + message);
    }

}
