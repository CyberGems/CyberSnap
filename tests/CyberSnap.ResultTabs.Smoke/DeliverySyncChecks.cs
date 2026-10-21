using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.UI.Controls;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckDeliverySynchronization(string temp, string? renderDirectory)
    {
        foreach (string language in new[] { "en", "es" })
        {
            string path = Path.Combine(temp, "delivery-sync-" + language + ".json");
            using var widgetSettings = new SettingsService(path);
            var initial = widgetSettings.Settings;
            initial.InterfaceLanguage = language;
            initial.DisableAnimations = true;
            initial.UiScale = 1.0;
            initial.SaveToFile = initial.SaveVideoToFile = initial.SaveGifToFile = true;
            initial.ColorDetailCopyFormat = ColorDetailCopyFormat.Rgb;
            initial.ColorDetailIncludeHash = false;
            foreach (var kind in CopyKinds)
            {
                AutoCopyPreferences.SetKindEnabled(initial, kind, false);
                SetDeliveryWindow(initial, kind, true);
            }
            widgetSettings.Save();
            widgetSettings.FlushPendingWrites();
            using var configSettings = new SettingsService(path);
            using var resultSettings = new SettingsService(path);
            using var colorSettings = new SettingsService(path);
            using var history = new HistoryService();
            using var search = new ImageSearchIndexService();
            Console.WriteLine("Checking live delivery synchronization in " + language + ": opening Settings.");
            var config = new SettingsWindow(configSettings, history, search);
            resultSettings.Load();
            colorSettings.Load();
            var widget = new CaptureWidgetWindow(widgetSettings) { IsHitTestVisible = false };
            var ocr = OcrResultWindow.ShowResult("delivery sync OCR", resultSettings);
            var qr = QrResultWindow.ShowResult("delivery sync QR", ZXing.BarcodeFormat.QR_CODE, resultSettings);
            var colorType = typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.ColorDetailWindow")!;
            var color = (Window)Activator.CreateInstance(colorType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object?[] { (byte)30, (byte)90, (byte)140, null, colorSettings }, null)!;
            int publications = 0;
            void OnPublished() => publications++;
            SettingsService.AutoCopyPreferencesChanged += OnPublished;
            try
            {
                Console.WriteLine("Checking live delivery synchronization: showing the windows.");
                config.Show();
                widget.Show();
                color.Show();
                Pump();
                // Retain the same native menu graph to verify external changes refresh
                // its checkmarks too, rather than rebuilding it for each assertion.
                using var menu = (Forms.ContextMenuStrip)Call(widget, "BuildMoreMenu")!;
                SetField(widget, "_contextMenu", menu);
                foreach (var kind in CopyKinds)
                foreach (int option in new[] { 0, 1 })
                {
                    Change(kind, option, "widget", () => ClickWidgetDelivery(widget, language, kind, option));
                    Change(kind, option, "settings", () => ChangeConfig(kind, option));
                    Change(kind, option, "settings", () => ChangeConfig(kind, option));
                    Change(kind, option, "settings", () => ChangeConfig(kind, option));
                    if (kind is AutoCopyKind.Ocr or AutoCopyKind.Scan or AutoCopyKind.Color)
                    {
                        Change(kind, option, "result window", () => ChangeWindow(kind, option));
                        Change(kind, option, "result window", () => ChangeWindow(kind, option));
                    }
                }
                // Color format edits must not restore old copy/window preferences.
                Change(AutoCopyKind.Color, 1, "settings", () => ChangeConfig(AutoCopyKind.Color, 1));
                bool colorCopy = widgetSettings.Settings.ColorDetailAutoCopy;
                bool colorShow = widgetSettings.Settings.ShowColorDetailWindow;
                ((ComboBox)Field(color, "_formatCombo")).SelectedIndex = 2;
                Pump();
                Assert(widgetSettings.Settings.ColorDetailAutoCopy == colorCopy
                    && widgetSettings.Settings.ShowColorDetailWindow == colorShow,
                    "editing the color format preserves synchronized delivery choices", false);
                var lastOutput = widgetSettings.Settings;
                lastOutput.SaveToFile = lastOutput.AutoShareAfterCapture = false;
                lastOutput.AfterCapture = AfterCaptureAction.None;
                lastOutput.OpenEditorAfterCapture = lastOutput.OpenInSystemViewerAfterCapture = false;
                lastOutput.ImageAutoCopyToClipboard = lastOutput.ScrollAutoCopyToClipboard = true;
                lastOutput.ShowCapturePreview = lastOutput.ShowScrollCapturePreview = false;
                widgetSettings.Save();
                SettingsService.PublishAutoCopyPreferences(lastOutput);
                Change(AutoCopyKind.Image, 0, "settings last-output fallback", () => ChangeConfig(AutoCopyKind.Image, 0));
                Change(AutoCopyKind.Scroll, 0, "settings last-output fallback", () => ChangeConfig(AutoCopyKind.Scroll, 0));
                Call(config, "SelectSettingsTab", "picker");
                Pump();
                Element<FrameworkElement>(config, "ShowColorDetailWindowRow").BringIntoView();
                Pump();
                Render(config, renderDirectory, "delivery-settings-" + language + ".png");
                Console.WriteLine("PASS: widget, Settings and result windows synchronize both ways, preserve other modes, and persist in " + language + ".");

                void Change(AutoCopyKind kind, int option, string source, Action action)
                {
                    var expectedCopy = CopyFlags(widgetSettings.Settings);
                    var expectedShow = DeliveryWindowFlags(widgetSettings.Settings);
                    var destinations = OtherDestinations(widgetSettings.Settings);
                    int own = Array.IndexOf(CopyKinds, kind);
                    if (option == 0)
                    {
                        expectedCopy[own] = !expectedCopy[own];
                        if (!expectedCopy[own] && kind is AutoCopyKind.Ocr or AutoCopyKind.Scan or AutoCopyKind.Color)
                            expectedShow[own] = true;
                    }
                    else
                    {
                        expectedShow[own] = !expectedShow[own];
                        if (!expectedShow[own] && kind is AutoCopyKind.Ocr or AutoCopyKind.Scan or AutoCopyKind.Color)
                            expectedCopy[own] = true;
                    }
                    if (kind is AutoCopyKind.Image or AutoCopyKind.Scroll)
                    {
                        var outcome = AfterCaptureOutcomeModel.Normalize(AfterCaptureOutcomeModel.FromSettings(widgetSettings.Settings,
                            kind == AutoCopyKind.Scroll ? CaptureKind.ScrollCapture : CaptureKind.Screenshot) with
                            { Clipboard = expectedCopy[own], Preview = expectedShow[own] });
                        expectedShow[own] = outcome.Preview;
                    }
                    int before = publications;
                    action();
                    Pump();
                    string context = language + " " + source + " " + kind + " " + option;
                    Assert(publications == before + 1, "delivery edits publish once without feedback loops: " + context, false);
                    foreach (var service in new[] { widgetSettings, configSettings, resultSettings, colorSettings })
                    {
                        Call(service, "RefreshResultDeliveryPreferences");
                        Assert(CopyFlags(service.Settings).SequenceEqual(expectedCopy)
                            && DeliveryWindowFlags(service.Settings).SequenceEqual(expectedShow),
                            "delivery services preserve all other modes: " + context, false);
                        Assert(OtherDestinations(service.Settings).Equals(destinations),
                            "delivery changes preserve save/notification/editor/viewer/share: " + context, false);
                        service.FlushPendingWrites();
                    }
                    foreach (var mode in CopyKinds)
                    {
                        int index = Array.IndexOf(CopyKinds, mode);
                        var modeMenu = WidgetModeMenu(menu, language, mode);
                        Assert((modeMenu.DropDownItems[0].Image is not null) == expectedCopy[index]
                            && (modeMenu.DropDownItems[1].Image is not null) == expectedShow[index],
                            "existing widget menu reflects external changes: " + context, false);
                        var pair = ConfigPair(mode);
                        Assert(pair.Copy == expectedCopy[index] && pair.ShowWindow == expectedShow[index],
                            "Settings reflects every mode's delivery choices: " + context, false);
                    }
                    Assert(Element<CheckBox>(ocr, "AutoCopyCheck").IsChecked == expectedCopy[1]
                        && Element<CheckBox>(ocr, "ShowWindowCheck").IsChecked == expectedShow[1]
                        && Element<CheckBox>(qr, "AutoCopyCheck").IsChecked == expectedCopy[4]
                        && Element<CheckBox>(qr, "ShowWindowCheck").IsChecked == expectedShow[4]
                        && GetField<bool>(color, "_autoCopy") == expectedCopy[5]
                        && GetField<bool>(color, "_showWindowPref") == expectedShow[5],
                        "open OCR/QR/color footers immediately reflect delivery changes: " + context, false);
                    foreach (var optionPair in new[] { ("Auto-copy", expectedCopy[5]), ("Show this window", expectedShow[5]) })
                        Assert(VisualDescendants(color).OfType<CheckBox>().Single(toggle => AutomationProperties.GetName(toggle)
                            == LocalizationService.Translate(language, optionPair.Item1)).IsChecked == optionPair.Item2,
                            "color footer toggles reflect changes without needing activation: " + context, false);
                    using var reloaded = new SettingsService(path);
                    reloaded.Load();
                    Assert(CopyFlags(reloaded.Settings).SequenceEqual(expectedCopy)
                        && DeliveryWindowFlags(reloaded.Settings).SequenceEqual(expectedShow),
                        "bidirectional delivery choices survive reload: " + context, false);
                }

                ResultDelivery.Plan ConfigPair(AutoCopyKind kind)
                {
                    bool Check(string name) => Element<CheckBox>(config, name).IsChecked == true;
                    return kind switch
                    {
                        AutoCopyKind.Image => new(Check("AutoCopyImagesCheck"), Element<AfterCaptureOutcomeEditor>(config, "AfterCaptureOutcomeEditor").State.Preview),
                        AutoCopyKind.Scroll => new(Check("AutoCopyScrollCheck"), Check("ShowScrollCapturePreviewCheck")),
                        AutoCopyKind.Ocr => new(Check("AutoCopyOcrCheck"), Check("OcrShowResultWindowCheck")),
                        AutoCopyKind.Scan => new(Check("AutoCopyScanCheck"), Check("ScanShowResultWindowCheck")),
                        AutoCopyKind.Color => new(Check("AutoCopyColorCheck"), Check("ShowColorDetailWindowCheck")),
                        _ => RecordingPair(kind)
                    };
                }

                ResultDelivery.Plan RecordingPair(AutoCopyKind kind)
                {
                    var editor = Element<RecordingOutcomeEditor>(config, kind == AutoCopyKind.Video ? "VideoOutcomeEditor" : "GifOutcomeEditor");
                    return new(editor.State.Clipboard, editor.State.OpenTrimmer);
                }

                void ChangeConfig(AutoCopyKind kind, int option)
                {
                    bool current = option == 0 ? ConfigPair(kind).Copy : ConfigPair(kind).ShowWindow;
                    if (kind is AutoCopyKind.Video or AutoCopyKind.Gif)
                        Call(Element<RecordingOutcomeEditor>(config, kind == AutoCopyKind.Video ? "VideoOutcomeEditor" : "GifOutcomeEditor"),
                            current ? "RemovePill" : "AddPill", option == 0 ? RecordingOutcomePillKind.Clipboard : RecordingOutcomePillKind.Trimmer);
                    else if (kind == AutoCopyKind.Image && option == 1)
                        Call(Element<AfterCaptureOutcomeEditor>(config, "AfterCaptureOutcomeEditor"), current ? "RemovePill" : "AddPill", AfterCapturePillKind.Preview);
                    else
                    {
                        string name = kind switch
                        {
                            AutoCopyKind.Image => "AutoCopyImagesCheck",
                            AutoCopyKind.Scroll => option == 0 ? "AutoCopyScrollCheck" : "ShowScrollCapturePreviewCheck",
                            AutoCopyKind.Ocr => option == 0 ? "AutoCopyOcrCheck" : "OcrShowResultWindowCheck",
                            AutoCopyKind.Scan => option == 0 ? "AutoCopyScanCheck" : "ScanShowResultWindowCheck",
                            _ => option == 0 ? "AutoCopyColorCheck" : "ShowColorDetailWindowCheck"
                        };
                        Element<CheckBox>(config, name).IsChecked = !current;
                    }
                }

                void ChangeWindow(AutoCopyKind kind, int option)
                {
                    var window = kind == AutoCopyKind.Ocr ? ocr : kind == AutoCopyKind.Scan ? qr : color;
                    var check = kind == AutoCopyKind.Color
                        ? VisualDescendants(color).OfType<CheckBox>().Single(toggle => AutomationProperties.GetName(toggle)
                            == LocalizationService.Translate(language, option == 0 ? "Auto-copy" : "Show this window"))
                        : Element<CheckBox>(window, option == 0 ? "AutoCopyCheck" : "ShowWindowCheck");
                    check.IsChecked = check.IsChecked != true;
                }
            }
            finally
            {
                SettingsService.AutoCopyPreferencesChanged -= OnPublished;
                Call(Field(ocr, "_tabs"), "CloseActive");
                Call(Field(qr, "_tabs"), "CloseActive");
                color.Close();
                widget.Close();
                config.Close();
            }
        }
    }
}
