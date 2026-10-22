using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using CyberSnap.AppModel.Settings;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;

internal static partial class Program
{
    private const string TrayDescriptionKey = "Windows hides new tray icons behind the overflow (^). Drag CyberSnap onto the taskbar, or pin it in Windows Settings.";

    private static void CheckToolsSettings(string temp, string? renderDirectory)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        double originalScale = UiScale.Current;
        var toolsPage = SettingsSchemaCatalog.Pages.Single(page => page.Key == "tools");
        var toolBindings = toolsPage.Sections.SelectMany(section => section.Items).Select(item => item.BindingPath).ToArray();
        Assert(new[] { "OcrLanguageTag", "ColorDetailCopyFormat", "ScanShowResultWindow", "RulerCaptureAllScreens", "ZoomInitialLevel", "ZoomSmoothImage", "ZoomAnimate", "ZoomShowHelpBar", "PinInitialScale", "PinSmoothImage", "PinAlwaysOnTop", "PinIncludeInCaptures", "SaveStandaloneToHistory" }
            .All(toolBindings.Contains), "the settings catalog routes all six tools and their shared history to Tools");
        Assert(!SettingsSchemaCatalog.Pages.Where(page => page.Key != "tools").SelectMany(page => page.Sections)
            .SelectMany(section => section.Items).Any(item => toolBindings.Contains(item.BindingPath)),
            "moved tool settings do not keep stale search routes in other pages");
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (bool narrow in new[] { false, true })
            {
                string path = Path.Combine(temp, $"tools-{language}-{narrow}.json");
                using var settings = new SettingsService(path);
                var initial = settings.Settings;
                initial.InterfaceLanguage = language;
                initial.UiScale = narrow ? 1.4 : 1.0;
                initial.DisableAnimations = true;
                initial.OcrLanguageTag = "auto";
                initial.OcrDefaultTranslateFrom = "es";
                initial.OcrDefaultTranslateTo = "en";
                initial.ColorDetailCopyFormat = ColorDetailCopyFormat.Rgb;
                initial.ColorDetailIncludeHash = false;
                initial.RulerCaptureAllScreens = true;
                initial.RulerContextMenuEnabled = false;
                initial.SaveStandaloneToHistory = false;
                settings.Save(); settings.FlushPendingWrites();
                UiScale.Set(initial.UiScale);
                Theme.SetMode(narrow ? AppThemeMode.Light : AppThemeMode.Dark);
                LocalizationService.ApplyCurrentCulture(language);
                using var history = new HistoryService();
                using var search = new ImageSearchIndexService();
                var window = new SettingsWindow(settings, history, search);
                string context = language;
                try
                {
                    window.Show(); Pump();
                    window.Width = narrow ? window.MinWidth : 960;
                    window.Height = 780;
                    Pump();
                    context = $"{language}, actual width={window.ActualWidth:F1}, scale={initial.UiScale}";
                    Assert(window.Width >= window.MinWidth && window.ActualWidth >= window.MinWidth - 6,
                        "the tool layout fixture respects the application's actual minimum width: " + context, false);
                    var before = ToolPreferenceSnapshot(settings.Settings);
                    foreach (string key in new[] { "ocr", "picker", "qr", "ruler", "zoom", "pin" })
                    {
                        Call(window, "SelectSettingsTab", key); Pump();
                        Assert(Element<RadioButton>(window, "ToolsTab").IsChecked == true
                            && Element<TextBlock>(window, "PageTitleText").Text == LocalizationService.Translate("Tools"),
                            "legacy and tool-specific routes open the localized Tools page: " + key + ", " + context, false);
                        foreach (string tool in new[] { "Ocr", "Picker", "Qr", "Ruler", "Zoom", "Pin" })
                            Assert(Element<StackPanel>(window, tool + "ToolPanel").Visibility == (tool.Equals(key, StringComparison.OrdinalIgnoreCase)
                                ? Visibility.Visible : Visibility.Collapsed), "only the selected tool is visible: " + key + ", " + context, false);
                        var strip = Element<WrapPanel>(window, "ToolsTabStrip");
                        var bounds = strip.Children.OfType<RadioButton>().Select(button => button.TransformToAncestor(window)
                            .TransformBounds(new Rect(button.RenderSize))).ToArray();
                        Assert(bounds.All(rect => rect.Right <= window.ActualWidth - 12)
                            && !bounds.SelectMany((rect, index) => bounds.Skip(index + 1).Select(other => Rect.Intersect(rect, other)))
                                .Any(rect => !rect.IsEmpty && rect.Width > 1 && rect.Height > 1),
                            "tool navigation wraps without clipping or overlapping: " + key + ", " + context, false);
                        foreach (string control in key switch
                        {
                            "ocr" => new[] { "OcrLanguageCombo", "TranslateFromCombo", "TranslateToCombo" },
                            "picker" => new[] { "AutoCopyColorCheck", "ColorCopyFormatCombo", "ColorIncludeHashCheck" },
                            "qr" => new[] { "AutoCopyScanCheck", "ScanShowResultWindowCheck" },
                            "zoom" => new[] { "ZoomInitialLevelCombo", "ZoomSmoothImageCheck", "ZoomAnimateCheck", "ZoomShowHelpBarCheck" },
                            "pin" => new[] { "PinInitialScaleCombo", "PinSmoothImageCheck", "PinAlwaysOnTopCheck", "PinIncludeInCapturesCheck" },
                            _ => new[] { "RulerCaptureAllScreensCheck", "RulerContextMenuEnabledCheck" }
                        })
                        {
                            var element = Element<FrameworkElement>(window, control);
                            var rect = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                            Assert(rect.Right <= window.ActualWidth - 12 && rect.Left > 0,
                                "tool controls stay within the window: " + control + ", " + context, false);
                        }
                        var page = Element<ScrollViewer>(window, "ToolsPanel");
                        page.ScrollToTop(); Pump();
                        Render(window, renderDirectory, $"tools-{key}-{language}{(narrow ? "-narrow" : "")}.png");
                    }
                    Assert(ToolPreferenceSnapshot(settings.Settings) == before,
                        "visiting all tool subpages preserves existing preferences: " + context);
                    Assert(Element<CheckBox>(window, "RulerCaptureAllScreensCheck").IsChecked == true
                        && Element<CheckBox>(window, "RulerContextMenuEnabledCheck").IsChecked == false,
                        "moved ruler preferences load their existing values: " + context);
                    foreach (string other in new[] { "es", "en", "es", language })
                    {
                        // Use the same language selector as the user, including
                        // its localization event that rebuilds the search index.
                        SelectTag(Element<ComboBox>(window, "InterfaceLanguageCombo"), other); Pump();
                        string actual = TrayDescriptionText(Element<TextBlock>(window, "TrayPinDescription"));
                        string expected = LocalizationService.Translate(other, TrayDescriptionKey);
                        Assert(Regex.Replace(actual, @"\s+", " ").Trim() == Regex.Replace(expected, @"\s+", " ").Trim(),
                            "the complete tray description follows repeated language changes: " + other, false);
                    }
                    settings.Save();
                    if (!narrow)
                    {
                        Call(window, "SelectSettingsTab", "general");
                        Element<Grid>(window, "TrayPinRow").BringIntoView(); Pump();
                        Render(window, renderDirectory, "tray-description-" + language + ".png");
                    }
                    if (!narrow) CheckPickerPreferenceSynchronization(window, settings, path, language);
                    CheckToolSearchNavigation(window, language);
                    Call(window, "SelectSettingsTab", "ruler");
                    Call(window, "ChangeToolShortcut_Click", window, new RoutedEventArgs()); Pump();
                    Assert(Element<RadioButton>(window, "HotkeysTab").IsChecked == true
                        && Element<StackPanel>(window, "CaptureToolsPanel").Children.OfType<Border>().Any(card => Equals(card.Tag, "_standaloneRuler")),
                        "the tool shortcut action reaches its existing keyboard settings: " + context);
                    Call(window, "SelectSettingsTab", "picker");
                    Call(window, "ToolSounds_Click", window, new RoutedEventArgs()); Pump();
                    Assert(Element<RadioButton>(window, "SoundsTab").IsChecked == true,
                        "the sound action opens the existing Sounds page: " + context);
                    Console.WriteLine("PASS: tool layout, routes, search and tray localization in " + context + ".");
                }
                finally { window.Close(); Pump(); }
            }
        }
        finally
        {
            UiScale.Set(originalScale);
            Theme.SetMode(AppThemeMode.System);
            LocalizationService.ApplyCurrentCulture(originalLanguage);
        }
    }

    private static void CheckPickerPreferenceSynchronization(SettingsWindow config, SettingsService settings, string path, string language)
    {
        using var colorSettings = new SettingsService(path);
        colorSettings.Load();
        var type = typeof(SettingsWindow).Assembly.GetType("CyberSnap.UI.ColorDetailWindow")!;
        var color = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object?[] { (byte)30, (byte)90, (byte)140, null, colorSettings }, null)!;
        int publications = 0;
        void Published() => publications++;
        SettingsService.AutoCopyPreferencesChanged += Published;
        try
        {
            color.Show(); Pump();
            Call(config, "SelectSettingsTab", "picker"); Pump();
            var format = Element<ComboBox>(config, "ColorCopyFormatCombo");
            var hash = Element<CheckBox>(config, "ColorIncludeHashCheck");
            var originalDelivery = (settings.Settings.ColorDetailAutoCopy, settings.Settings.ShowColorDetailWindow,
                settings.Settings.OcrAutoCopyToClipboard, settings.Settings.ScanAutoCopyToClipboard);
            Assert(format.SelectedIndex == 1 && !hash.IsEnabled && hash.IsChecked == false,
                "Picker loads the existing favorite format and preserves its inactive HEX preference: " + language);
            int before = publications;
            format.SelectedIndex = 0; Pump();
            Assert(hash.IsEnabled && (int)Field(color, "_format") == 0 && publications == before + 1,
                "changing format in Tools updates an open Picker exactly once: " + language);
            hash.IsChecked = true; Pump();
            Assert((bool)Field(color, "_includeHash") && ((string)Call(color, "FavoriteText")!).StartsWith('#'),
                "Tools updates the Picker's active HEX output immediately: " + language);
            ((ComboBox)Field(color, "_formatCombo")).SelectedIndex = 2; Pump();
            Assert(format.SelectedIndex == 2 && !hash.IsEnabled,
                "changing format in the Picker updates Tools without activation: " + language);
            var hashToggle = VisualDescendants(color).OfType<CheckBox>().Single(check => AutomationProperties.GetName(check)
                == LocalizationService.Translate("Include # in HEX"));
            hashToggle.IsChecked = false; Pump();
            Assert(hash.IsChecked == false && !settings.Settings.ColorDetailIncludeHash,
                "Picker HEX edits update the matching Tools preference: " + language);
            Assert(originalDelivery == (settings.Settings.ColorDetailAutoCopy, settings.Settings.ShowColorDetailWindow,
                settings.Settings.OcrAutoCopyToClipboard, settings.Settings.ScanAutoCopyToClipboard),
                "format edits preserve every tool's delivery choices: " + language);
            settings.FlushPendingWrites(); colorSettings.FlushPendingWrites();
            using (var reloaded = new SettingsService(path))
            {
                reloaded.Load();
                Assert(reloaded.Settings.ColorDetailCopyFormat == ColorDetailCopyFormat.Hsl && !reloaded.Settings.ColorDetailIncludeHash,
                    "Picker format preferences survive reloading settings: " + language);
            }
            before = publications;
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                format.SelectedIndex = 0; Pump();
                Assert(format.SelectedIndex == 2 && settings.Settings.ColorDetailCopyFormat == ColorDetailCopyFormat.Hsl
                    && publications == before && Element<TextBlock>(config, "ToolsPreferenceStatusText").Visibility == Visibility.Visible,
                    "failed tool saves restore the UI and do not publish an unsaved format: " + language);
            }
            settings.FlushPendingWrites();
            Call(config, "SelectSettingsTab", "zoom"); Pump();
            Element<ComboBox>(config, "ZoomInitialLevelCombo").SelectedIndex = 6;
            Element<CheckBox>(config, "ZoomSmoothImageCheck").IsChecked = false;
            Element<CheckBox>(config, "ZoomAnimateCheck").IsChecked = false;
            Assert(Element<CheckBox>(config, "ZoomShowHelpBarCheck").IsChecked == true, "Tools loads the enabled help-bar default: " + language);
            Element<CheckBox>(config, "ZoomShowHelpBarCheck").IsChecked = false;
            using (var zoomReload = new SettingsService(path))
            {
                zoomReload.Load();
                Assert(zoomReload.Settings.ZoomInitialLevel == 2.75 && !zoomReload.Settings.ZoomSmoothImage
                    && !zoomReload.Settings.ZoomAnimate && !zoomReload.Settings.ZoomShowHelpBar, "Zoom controls persist their actual selections: " + language);
            }
            Element<CheckBox>(config, "RulerCaptureAllScreensCheck").IsChecked = false;
            Call(config, "SelectSettingsTab", "pin"); Pump();
            settings.Settings.PinInitialScale = 1.7;
            Call(config, "ApplyPinControls");
            Assert(Element<ComboBox>(config, "PinInitialScaleCombo").SelectedItem is ComboBoxItem { Tag: double customScale }
                && Math.Abs(customScale - 1.7) < .001, "Pin retains a custom imported initial scale: " + language);
            var pinScale = Element<ComboBox>(config, "PinInitialScaleCombo");
            pinScale.SelectedItem = pinScale.Items.OfType<ComboBoxItem>().Single(item => (double)item.Tag == 1.5);
            Element<CheckBox>(config, "PinSmoothImageCheck").IsChecked = false;
            Element<CheckBox>(config, "PinAlwaysOnTopCheck").IsChecked = false;
            Element<CheckBox>(config, "PinIncludeInCapturesCheck").IsChecked = true;
            using (var pinReload = new SettingsService(path))
            {
                pinReload.Load();
                Assert(pinReload.Settings.PinInitialScale == 1.5 && !pinReload.Settings.PinSmoothImage
                    && !pinReload.Settings.PinAlwaysOnTop && pinReload.Settings.PinIncludeInCaptures,
                    "all Pin controls persist their selections from Tools: " + language);
            }
            Element<CheckBox>(config, "RulerContextMenuEnabledCheck").IsChecked = true;
            Element<CheckBox>(config, "SaveStandaloneToHistoryCheck").IsChecked = true;
            using var finalReload = new SettingsService(path);
            finalReload.Load();
            Assert(!finalReload.Settings.RulerCaptureAllScreens && finalReload.Settings.RulerContextMenuEnabled
                && finalReload.Settings.SaveStandaloneToHistory,
                "moved ruler and shared-history controls persist from Tools: " + language);
        }
        finally
        {
            SettingsService.AutoCopyPreferencesChanged -= Published;
            color.Close(); Pump();
        }
    }

    private static void CheckToolSearchNavigation(SettingsWindow window, string language)
    {
        foreach (var (label, panel) in new[] { ("Favorite format", "PickerToolPanel"), ("Show the QR & Barcode result window", "QrToolPanel"), ("Capture all screens", "RulerToolPanel"), ("OCR language", "OcrToolPanel"), ("Initial zoom level", "ZoomToolPanel"), ("Show help bar", "ZoomToolPanel"), ("Include pinned images in captures", "PinToolPanel"), ("Save tool results to Gallery", "ToolHistoryRow") })
        {
            Call(window, "SelectSettingsTab", "capture");
            Element<TextBox>(window, "SettingsSearchBox").Text = LocalizationService.Translate(label);
            ((DispatcherTimer)Field(window, "_searchDebounceTimer")).Stop();
            Call(window, "ApplySettingsSearch"); Pump();
            var link = VisualDescendants(Element<StackPanel>(window, "SearchResultsStack")).OfType<Button>()
                .FirstOrDefault(button => Equals(button.ToolTip, LocalizationService.Translate("Open this tool's settings")));
            Assert(link is not null, "search finds tool controls even when their subpage was hidden: " + label + ", " + language);
            link!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert(Element<FrameworkElement>(window, panel).IsVisible
                && Element<RadioButton>(window, "ToolsTab").IsChecked == true
                && ((System.Collections.ICollection)Field(window, "_movedElements")).Count == 0,
                "search restores the live control and navigates to the correct tool: " + label + ", " + language
                    + $", breadcrumb={link.Content}, actual={Field(window, "_selectedToolKey")}, moved={((System.Collections.ICollection)Field(window, "_movedElements")).Count}");
        }
        Element<TextBox>(window, "SettingsSearchBox").Text = LocalizationService.Translate("Keyboard shortcut");
        ((DispatcherTimer)Field(window, "_searchDebounceTimer")).Stop();
        Call(window, "ApplySettingsSearch"); Pump();
        var shortcut = VisualDescendants(Element<StackPanel>(window, "SearchResultsStack")).OfType<Button>()
            .Single(button => Equals(button.Content, LocalizationService.Translate("Change shortcut")));
        shortcut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Assert(Element<RadioButton>(window, "HotkeysTab").IsChecked == true && !(bool)Field(window, "_isSearching"),
            "the tool shortcut action also leaves an active search and opens Keyboard: " + language);
        Call(window, "SelectSettingsTab", "picker");
        Element<TextBox>(window, "SettingsSearchBox").Text = LocalizationService.Translate("Favorite format");
        Call(window, "ChangeToolShortcut_Click", window, new RoutedEventArgs());
        Call(window, "ApplySettingsSearch"); Pump();
        Assert(Element<RadioButton>(window, "HotkeysTab").IsChecked == true && !(bool)Field(window, "_isSearching")
            && Element<TextBox>(window, "SettingsSearchBox").Text.Length == 0,
            "a pending search cannot override navigation from the tool shortcut action: " + language);
    }

    private static string ToolPreferenceSnapshot(AppSettings settings) => string.Join('|',
        settings.OcrLanguageTag, settings.OcrDefaultTranslateFrom, settings.OcrDefaultTranslateTo,
        settings.OcrAutoCopyToClipboard, settings.OcrShowResultWindow, settings.TranslationModel,
        settings.ColorDetailAutoCopy, settings.ShowColorDetailWindow, settings.ColorDetailCopyFormat, settings.ColorDetailIncludeHash,
        settings.ScanAutoCopyToClipboard, settings.ScanShowResultWindow, settings.RulerCaptureAllScreens,
        settings.RulerContextMenuEnabled, settings.SaveStandaloneToHistory, settings.ZoomInitialLevel, settings.ZoomSmoothImage, settings.ZoomAnimate, settings.ZoomShowHelpBar, settings.PinInitialScale, settings.PinSmoothImage, settings.PinAlwaysOnTop, settings.PinIncludeInCaptures);

    private static string TrayDescriptionText(TextBlock text)
        => string.Concat(text.Inlines.Select(inline => inline switch
        {
            Run run => run.Text,
            InlineUIContainer { Child: StackPanel group } => string.Concat(group.Children.OfType<TextBlock>().Select(label => label.Text)) + " (^)",
            _ => string.Empty
        }));
}
