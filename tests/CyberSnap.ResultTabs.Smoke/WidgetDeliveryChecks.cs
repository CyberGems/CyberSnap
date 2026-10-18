using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static readonly Dictionary<AutoCopyKind, string> WidgetModeLabels = new()
    {
        [AutoCopyKind.Image] = "Image capture", [AutoCopyKind.Scroll] = "Scrolling capture",
        [AutoCopyKind.Video] = "Video (MP4)", [AutoCopyKind.Gif] = "GIF", [AutoCopyKind.Ocr] = "OCR",
        [AutoCopyKind.Color] = "Color picker", [AutoCopyKind.Scan] = "Scan QR / Barcode"
    };

    private static bool[] DeliveryWindowFlags(AppSettings settings) =>
    [
        settings.ShowCapturePreview, settings.OcrShowResultWindow, settings.OpenVideoTrimmerAfterCapture,
        settings.OpenGifTrimmerAfterCapture, settings.ScanShowResultWindow, settings.ShowColorDetailWindow,
        settings.ShowScrollCapturePreview
    ];

    private static void SetDeliveryWindow(AppSettings settings, AutoCopyKind kind, bool show)
    {
        switch (kind)
        {
            case AutoCopyKind.Image: settings.ShowCapturePreview = show; break;
            case AutoCopyKind.Scroll: settings.ShowScrollCapturePreview = show; break;
            case AutoCopyKind.Video: settings.OpenVideoTrimmerAfterCapture = show; break;
            case AutoCopyKind.Gif: settings.OpenGifTrimmerAfterCapture = show; break;
            case AutoCopyKind.Ocr: settings.OcrShowResultWindow = show; break;
            case AutoCopyKind.Color: settings.ShowColorDetailWindow = show; break;
            case AutoCopyKind.Scan: settings.ScanShowResultWindow = show; break;
        }
    }

    private static object OtherDestinations(AppSettings settings) => (
        settings.SaveToFile, AfterCapturePreferences.FromSettingsDestinationOnly(settings).WindowIndex,
        settings.OpenInSystemViewerAfterCapture, settings.AutoShareAfterCapture, settings.SaveVideoToFile,
        settings.SaveGifToFile, settings.ShowVideoRecordingNotification, settings.ShowGifRecordingNotification);

    private static Forms.ToolStripMenuItem WidgetModeMenu(Forms.ContextMenuStrip menu, string language, AutoCopyKind kind) =>
        menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == LocalizationService.Translate(language, WidgetModeLabels[kind]));

    private static void ClickWidgetDelivery(CaptureWidgetWindow widget, string language, AutoCopyKind kind, int option)
    {
        using var menu = (Forms.ContextMenuStrip)Call(widget, "BuildMoreMenu")!;
        WidgetModeMenu(menu, language, kind).DropDownItems[option].PerformClick();
        Pump();
    }

    private static void CheckWidgetDelivery(CaptureWidgetWindow widget, SettingsService service, string? renderDirectory)
    {
        string language = service.Settings.InterfaceLanguage;
        string original = JsonSerializer.Serialize(service.Settings);
        string path = GetField<string>(service, "_settingsPath");
        int published = 0;
        void OnPublished() => published++;
        SettingsService.AutoCopyPreferencesChanged += OnPublished;
        try
        {
            foreach (var kind in CopyKinds)
            foreach (bool others in new[] { false, true })
            foreach (bool copy in new[] { false, true })
            foreach (bool show in new[] { false, true })
            foreach (int option in new[] { 0, 1 })
            {
                var trial = JsonSerializer.Deserialize<AppSettings>(original)!;
                trial.SaveToFile = true;
                foreach (var otherKind in CopyKinds)
                {
                    AutoCopyPreferences.SetKindEnabled(trial, otherKind, otherKind == kind ? copy : others);
                    SetDeliveryWindow(trial, otherKind, otherKind == kind ? show : others);
                }
                SetProperty(service, "Settings", trial);
                service.Save();
                var beforeCopy = CopyFlags(trial);
                var beforeWindow = DeliveryWindowFlags(trial);
                var destinations = OtherDestinations(trial);
                bool guarded = kind is AutoCopyKind.Ocr or AutoCopyKind.Scan or AutoCopyKind.Color;
                bool shown = show || (guarded && !copy);
                using var menu = (Forms.ContextMenuStrip)Call(widget, "BuildMoreMenu")!;
                var mode = WidgetModeMenu(menu, language, kind);
                Assert((mode.DropDownItems[0].Image is not null) == copy
                    && (mode.DropDownItems[1].Image is not null) == shown,
                    "widget menu reads each mode's own delivery choices", false);
                string windowKey = kind is AutoCopyKind.Image or AutoCopyKind.Scroll ? "Show preview after capture"
                    : kind is AutoCopyKind.Video or AutoCopyKind.Gif ? "Outcome step: open trimmer" : "Show result window";
                Assert(mode.DropDownItems[0].Text == LocalizationService.Translate(language, "Auto-copy")
                    && mode.DropDownItems[1].Text == LocalizationService.Translate(language, windowKey),
                    "each mode has localized clipboard and window options", false);
                int beforePublished = published;
                mode.DropDownItems[option].PerformClick();
                int own = Array.IndexOf(CopyKinds, kind);
                if (option == 0)
                {
                    beforeCopy[own] = !copy;
                    if (guarded && copy) beforeWindow[own] = true;
                }
                else
                {
                    beforeWindow[own] = !shown;
                    if (guarded && shown) beforeCopy[own] = true;
                }
                Assert(CopyFlags(service.Settings).SequenceEqual(beforeCopy)
                    && DeliveryWindowFlags(service.Settings).SequenceEqual(beforeWindow),
                    "widget delivery toggles affect only their own mode and its safety fallback", false);
                Assert(OtherDestinations(service.Settings).Equals(destinations),
                    "widget delivery changes preserve all save, notification, editor, viewer and share choices", false);
                Assert(published == beforePublished + 1, "widget delivery changes publish once to open tools", false);
                service.FlushPendingWrites();
                using var reloaded = new SettingsService(path);
                reloaded.Load();
                Assert(CopyFlags(reloaded.Settings).SequenceEqual(beforeCopy)
                    && DeliveryWindowFlags(reloaded.Settings).SequenceEqual(beforeWindow),
                    "all per-mode widget delivery choices survive save and reload", false);
            }

            CheckWidgetDeliverySync(widget, service, language);
            CheckMixedImagePreviewDelivery(widget, service, language);
            CheckConfirmOverlayPreviewScope();
            Console.WriteLine("PASS: widget delivery menus, all seven modes, independent copy/window combinations, persistence and live synchronization in " + language + ".");
        }
        finally
        {
            SettingsService.AutoCopyPreferencesChanged -= OnPublished;
            SetProperty(service, "Settings", JsonSerializer.Deserialize<AppSettings>(original)!);
            service.Save();
            SettingsService.PublishAutoCopyPreferences(service.Settings);
        }
    }

    private static void CheckWidgetDeliverySync(CaptureWidgetWindow widget, SettingsService service, string language)
    {
        var settings = service.Settings;
        foreach (var kind in CopyKinds)
        {
            AutoCopyPreferences.SetKindEnabled(settings, kind, false);
            SetDeliveryWindow(settings, kind, true);
        }
        service.Save();
        service.FlushPendingWrites();
        using var detached = new SettingsService(GetField<string>(service, "_settingsPath"));
        detached.Load();
        var ocr = OcrResultWindow.ShowResult("widget delivery OCR", detached);
        var qr = QrResultWindow.ShowResult("widget delivery QR", ZXing.BarcodeFormat.QR_CODE, detached);
        var colorType = typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.ColorDetailWindow")!;
        var color = (Window)Activator.CreateInstance(colorType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, new object?[] { (byte)30, (byte)90, (byte)140, null, detached }, null)!;
        Call(color, "RefreshDeliveryPreferences", settings);
        color.Show();
        try
        {
            ClickWidgetDelivery(widget, language, AutoCopyKind.Ocr, 1);
            Assert(Element<CheckBox>(ocr, "AutoCopyCheck").IsChecked == true && Element<CheckBox>(ocr, "ShowWindowCheck").IsChecked == false
                && Element<CheckBox>(qr, "AutoCopyCheck").IsChecked == false && Element<CheckBox>(qr, "ShowWindowCheck").IsChecked == true,
                "widget OCR changes refresh its detached result window without changing QR", false);
            ClickWidgetDelivery(widget, language, AutoCopyKind.Scan, 1);
            Assert(Element<CheckBox>(qr, "AutoCopyCheck").IsChecked == true && Element<CheckBox>(qr, "ShowWindowCheck").IsChecked == false,
                "widget QR window preference enables only QR auto-copy and updates its open footer", false);
            ClickWidgetDelivery(widget, language, AutoCopyKind.Color, 1);
            Assert(GetField<bool>(color, "_autoCopy") && !GetField<bool>(color, "_showWindowPref"),
                "widget color preferences refresh the already open color window", false);
            ClickWidgetDelivery(widget, language, AutoCopyKind.Color, 0);
            Assert(!GetField<bool>(color, "_autoCopy") && GetField<bool>(color, "_showWindowPref")
                && service.Settings.OcrAutoCopyToClipboard && service.Settings.ScanAutoCopyToClipboard,
                "color's last-output guard stays local and cannot alter OCR or QR", false);
            ClickWidgetDelivery(widget, language, AutoCopyKind.Video, 1);
            ClickWidgetDelivery(widget, language, AutoCopyKind.Image, 1);
            Call(detached, "RefreshResultDeliveryPreferences");
            Assert(!detached.Settings.OpenVideoTrimmerAfterCapture && detached.Settings.OpenGifTrimmerAfterCapture
                && !detached.Settings.ShowCapturePreview && detached.Settings.ShowScrollCapturePreview,
                "separate settings services synchronize video/GIF and image/scroll window preferences independently", false);
        }
        finally
        {
            Call(Field(ocr, "_tabs"), "CloseActive");
            Call(Field(qr, "_tabs"), "CloseActive");
            color.Close();
        }
    }

    private static void CheckMixedImagePreviewDelivery(CaptureWidgetWindow widget, SettingsService service, string language)
    {
        var settings = service.Settings;
        settings.SaveToFile = settings.OpenEditorAfterCapture = settings.OpenInSystemViewerAfterCapture = settings.AutoShareAfterCapture = false;
        settings.AfterCapture = AfterCaptureAction.None;
        settings.ImageAutoCopyToClipboard = false;
        settings.ScrollAutoCopyToClipboard = true;
        settings.ShowCapturePreview = true;
        settings.ShowScrollCapturePreview = false;
        settings.CapturePreviewTimeoutSeconds = 0;
        service.Save();
        service.FlushPendingWrites();
        var preview = new CapturePreviewDialog(new System.Drawing.Bitmap(120, 80), service);
        preview.Show();
        try
        {
            Pump();
            string copyLabel = LocalizationService.Translate(language, AfterCaptureOutcomeModel.LabelKey(AfterCapturePillKind.Clipboard));
            string copiedLabel = LocalizationService.Translate(language, AfterCaptureOutcomeModel.DoneLabelKey(AfterCapturePillKind.Clipboard));
            bool HasClipboardPill() => VisualDescendants(Element<Panel>(preview, "AfterCapturePillsPanel")).OfType<TextBlock>()
                .Any(text => text.Text == copyLabel || text.Text == copiedLabel);
            Assert(!HasClipboardPill(), "image preview does not inherit scrolling auto-copy", false);
            Call(preview, "AddSession", new System.Drawing.Bitmap(140, 200), null!, true, CaptureKind.ScrollCapture);
            Pump();
            Assert(HasClipboardPill(), "scrolling preview uses its own clipboard preference", false);
            AwaitCondition(() => VisualDescendants(Element<Panel>(preview, "AfterCapturePillsPanel")).OfType<TextBlock>()
                .Any(text => text.Text == copiedLabel), "Scrolling preview did not finish its clipboard status animation");
            Call(preview, "ActivateSessionAt", 0);
            Pump();
            Assert(!HasClipboardPill(), "switching back restores the ordinary image's delivery state", false);
            ClickWidgetDelivery(widget, language, AutoCopyKind.Image, 0);
            Call(preview, "ActivateSessionAt", 1);
            ClickWidgetDelivery(widget, language, AutoCopyKind.Scroll, 0);
            Assert(!HasClipboardPill() && settings.ShowScrollCapturePreview && settings.ImageAutoCopyToClipboard,
                "disabling scroll's final output brings back only its preview and refreshes its active tab", false);
            ClickWidgetDelivery(widget, language, AutoCopyKind.Scroll, 1);
            Assert(settings.ShowScrollCapturePreview, "an image mode always keeps an output when no other destination is configured", false);
        }
        finally
        {
            while (preview.IsVisible) Call(preview, "DiscardSessionAt", 0);
        }
    }

    private static void CheckConfirmOverlayPreviewScope()
    {
        using var screenshot = new System.Drawing.Bitmap(320, 200);
        using var overlay = new RegionOverlayForm(screenshot, new System.Drawing.Rectangle(0, 0, 320, 200),
            windowDetectionMode: WindowDetectionMode.Off);
        foreach (bool imagePreview in new[] { false, true })
        foreach (bool scrollPreview in new[] { false, true })
        foreach (var mode in new[] { CaptureMode.Rectangle, CaptureMode.ScrollCapture })
        {
            var settings = new AppSettings
            {
                ImageAutoCopyToClipboard = true, ScrollAutoCopyToClipboard = true,
                ShowCapturePreview = imagePreview, ShowScrollCapturePreview = scrollPreview
            };
            SetField(overlay, "_modeBeforeConfirm", mode);
            var expectedKind = mode == CaptureMode.ScrollCapture ? CaptureKind.ScrollCapture : CaptureKind.Screenshot;
            Assert((CaptureKind)Call(overlay, "ConfirmPreviewCaptureKind")! == expectedKind
                && (bool)Call(overlay, "IsConfirmPreviewEnabled", settings)! == (mode == CaptureMode.ScrollCapture ? scrollPreview : imagePreview),
                "confirm overlay reads and targets the preview preference of its selected capture mode", false);
        }
    }
}
