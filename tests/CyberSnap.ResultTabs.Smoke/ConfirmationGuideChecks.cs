using System.Drawing;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static string[] GuideTexts(object guide, string field, string property) =>
        ((Array)Field(guide, field)).Cast<object>().Select(item => (string)item.GetType().GetProperty(property)!.GetValue(item)!).ToArray();

    private static void CheckConfirmationGuide(SettingsService settings, string? renders = null, bool show = false)
    {
        var previous = System.Text.Json.JsonSerializer.Serialize(settings.Settings);
        double previousScale = UiScale.Current;
        try
        {
            Theme.SetMode(AppThemeMode.Dark); UiScale.Set(1.1);
            foreach (string language in new[] { "en", "es" })
            foreach (var purpose in Enum.GetValues<QuickStartGuide.ConfirmationPurpose>())
            foreach (bool hidden in new[] { false, true })
            {
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.EnabledTools = ToolDef.DefaultEnabledIds();
                settings.Settings.ShowAnnotationToolbar = !hidden;
                settings.Settings.HasSeenQuickStartGuide = true;
                UseTemporaryConfirmationSettings(settings);
                LocalizationService.ApplyCurrentCulture(language);
                using var source = new Bitmap(1000, 800);
                using var overlay = ConfirmationFixture(source, settings,
                    purpose == QuickStartGuide.ConfirmationPurpose.Pin ? CaptureMode.Pin : CaptureMode.Rectangle,
                    purpose == QuickStartGuide.ConfirmationPurpose.Zoom, show);
                var context = (QuickStartGuide.ConfirmationContext)Call(overlay, "GetConfirmationGuideContext")!;
                Assert(context.Purpose == purpose && context.CanAnnotate && context.AnnotationToolbarVisible == !hidden
                    && context.HasPreview == (purpose != QuickStartGuide.ConfirmationPurpose.Pin),
                    "confirmation guide context follows the capture purpose and visible tools: " + purpose + ", " + hidden);
                using var menu = (Forms.ContextMenuStrip)Call(overlay, "BuildConfirmationOptionsMenu")!;
                Assert(ConfirmOption(menu, "confirmation-guide").Text == (language == "es" ? "Anotar y confirmar" : "Annotate and confirm"),
                    "More names the confirmation guide in the current language");
                using var model = new QuickStartGuide();
                SetField(model, "_guideMode", QuickStartGuide.GuideMode.Confirmation);
                SetField(model, "_confirmationContext", context);
                Call(model, "LoadStrings");
                CheckConfirmationGuideContent(model, context, language);
                CheckConfirmationGuideSizing(context, language);
                if (!show) continue;

                var selected = overlay.ConfirmRect;
                bool firstRunDismissed = false;
                overlay.QuickStartGuideDismissed += () => firstRunDismissed = true;
                ConfirmOption(menu, "confirmation-guide").PerformClick();
                Forms.Application.DoEvents(); Pump();
                var guide = (QuickStartGuide)Field(overlay, "_quickStartGuide");
                Assert(guide.Visible && (QuickStartGuide.GuideMode)Field(guide, "_guideMode") == QuickStartGuide.GuideMode.Confirmation,
                    "More opens the contextual guide even while annotations are hidden");
                CheckConfirmationGuideContent(guide, context, language);
                if (!hidden)
                    CheckConfirmationGuideMonitors(overlay, context, language, renders);
                Call(overlay, "HandleEscapeKey");
                Assert(Field(overlay, "_quickStartGuide") is null && overlay.ConfirmRect == selected
                    && (bool)Field(overlay, "_isConfirmingSelection") && !firstRunDismissed,
                    "Escape dismisses help without canceling the selection or marking the capture introduction as seen");
                if (!hidden)
                {
                    Call(overlay, "ShowQuickStartGuide");
                    guide = (QuickStartGuide)Field(overlay, "_quickStartGuide");
                    Assert((QuickStartGuide.ConfirmationContext)Field(guide, "_confirmationContext") == context,
                        "the annotation toolbar logo opens the same contextual guide as More");
                    Call(overlay, "ExitConfirmMode", true);
                    Assert(Field(overlay, "_quickStartGuide") is null, "retry closes the confirmation guide before returning to selection");
                    if (purpose == QuickStartGuide.ConfirmationPurpose.Capture)
                    {
                        Call(overlay, "ShowQuickStartGuide");
                        guide = (QuickStartGuide)Field(overlay, "_quickStartGuide");
                        Assert((QuickStartGuide.GuideMode)Field(guide, "_guideMode") == QuickStartGuide.GuideMode.Capture,
                            "the selection stage retains its original capture introduction");
                        Call(overlay, "HandleEscapeKey");
                        Assert(firstRunDismissed, "only the capture introduction raises its first-run dismissal event");
                    }
                }
            }

            // OCR has no annotation tools, so its guide must never tell users to draw.
            settings.Settings.ShowAnnotationToolbar = true;
            UseTemporaryConfirmationSettings(settings);
            using var ocrSource = new Bitmap(1000, 800);
            using var ocr = ConfirmationFixture(ocrSource, settings, CaptureMode.Ocr, false, false);
            var ocrContext = (QuickStartGuide.ConfirmationContext)Call(ocr, "GetConfirmationGuideContext")!;
            using var ocrGuide = new QuickStartGuide();
            SetField(ocrGuide, "_guideMode", QuickStartGuide.GuideMode.Confirmation);
            SetField(ocrGuide, "_confirmationContext", ocrContext);
            Call(ocrGuide, "LoadStrings");
            Assert(!ocrContext.CanAnnotate && GuideTexts(ocrGuide, "_steps", "Text").Length == 2
                && (string)Field(ocrGuide, "_title") == LocalizationService.Translate("Adjust and confirm"),
                "OCR receives selection help without unavailable annotations");
        }
        finally
        {
            typeof(SettingsService).GetProperty("Settings")!.SetValue(settings, System.Text.Json.JsonSerializer.Deserialize<AppSettings>(previous)!);
            settings.Save(); settings.FlushPendingWrites();
            UiScale.Set(previousScale); Theme.SetMode(AppThemeMode.Dark);
            LocalizationService.ApplyCurrentCulture(settings.Settings.InterfaceLanguage);
        }
    }

    private static void CheckConfirmationGuideContent(QuickStartGuide guide, QuickStartGuide.ConfirmationContext context, string language)
    {
        Assert((string)Field(guide, "_title") == (language == "es" ? "Anotar y confirmar" : "Annotate and confirm"),
            "the guide has its own localized title");
        var steps = GuideTexts(guide, "_steps", "Text");
        var tips = GuideTexts(guide, "_tips", "Text");
        var keys = GuideTexts(guide, "_shortcuts", "Key");
        var labels = GuideTexts(guide, "_shortcuts", "Label");
        Assert(steps.Length == 3 && steps[0].Contains(language == "es" ? "dimensiones" : "dimensions")
            && steps[2].Contains(language == "es" ? "se conservan" : "kept"),
            "confirmation help explains crop adjustment and hiding tools without losing artwork");
        Assert(keys.Contains("Enter") && keys.Contains("Esc") && keys.Contains("R") && keys.Contains("Tab / Shift+Tab")
            && keys.Contains(LocalizationService.Translate("Space"))
            && keys.Contains("Ctrl+Z / Ctrl+Y") == context.AnnotationToolbarVisible
            && !keys.Contains("F1–F12") && !keys.Contains("Ctrl+S") && !keys.Contains("Ctrl+C"),
            "help lists only confirmation controls, with editing shortcuts omitted while tools are hidden");
        Assert(tips[0].Contains(LocalizationService.Translate("Preview opens only if enabled.")) == context.HasPreview,
            "Preview is described only for capture flows that offer it");
        bool pin = context.Purpose == QuickStartGuide.ConfirmationPurpose.Pin;
        bool zoom = context.Purpose == QuickStartGuide.ConfirmationPurpose.Zoom;
        Assert(labels[0] == LocalizationService.Translate(pin ? "Pin to screen" : "Confirm capture")
            && labels[1] == LocalizationService.Translate(zoom ? "Return to Zoom" : "Cancel capture")
            && tips[1].Contains("Zoom") == zoom,
            "confirmation and cancellation instructions match capture, Pin, or the retained Zoom");
    }

    private static void CheckConfirmationGuideMonitors(RegionOverlayForm owner,
        QuickStartGuide.ConfirmationContext context, string language, string? renders)
    {
        foreach (var screen in Forms.Screen.AllScreens)
        {
            using var guide = new QuickStartGuide();
            var work = screen.WorkingArea;
            var anchor = new Rectangle(work.Left + work.Width / 2, work.Bottom - 50, 30, 30);
            guide.ShowNear(owner, anchor, QuickStartGuide.TailDirection.Down, QuickStartGuide.GuideMode.Confirmation, context);
            Forms.Application.DoEvents(); Pump();
            Console.WriteLine($"Confirmation guide: {context.Purpose}, {language}, monitor={screen.DeviceName}, DPI={guide.DeviceDpi}, size={guide.Size}, work={work.Size}");
            Assert(work.Contains(guide.Bounds), "the complete confirmation guide fits the opening monitor");
            if (renders is not null)
            {
                System.IO.Directory.CreateDirectory(renders);
                using var picture = new Bitmap(guide.Width, guide.Height);
                guide.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
                picture.Save(System.IO.Path.Combine(renders, $"guide-{context.Purpose}-{language}-{guide.DeviceDpi}.png"));
            }
        }
    }

    private static void CheckConfirmationGuideSizing(QuickStartGuide.ConfirmationContext context, string language)
    {
        double previousScale = UiScale.Current;
        try
        {
            foreach (double scale in new[] { 1.0, 1.4 })
            {
                UiScale.Set(scale);
                using var guide = new QuickStartGuide();
                SetField(guide, "_guideMode", QuickStartGuide.GuideMode.Confirmation);
                SetField(guide, "_confirmationContext", context);
                Call(guide, "LoadStrings");
                foreach (int dpi in new[] { 96, 144 })
                foreach (var work in new[] { new Size(1920, 1080), new Size(1600, 900), new Size(1366, 768) })
                {
                    using var surface = new Bitmap(1, 1);
                    surface.SetResolution(dpi, dpi);
                    using var graphics = Graphics.FromImage(surface);
                    int width = (int)Call(guide, "MeasureConfirmationLayout", graphics, work, true)!;
                    Assert(width < work.Width && (int)Field(guide, "_bodyHeight") + 16 < work.Height,
                        $"confirmation help fits at {dpi} DPI, UI={scale}, {work}: {context.Purpose}, {language}, tools={context.AnnotationToolbarVisible}, measured={width}x{Field(guide, "_bodyHeight")}");
                }
            }
        }
        finally { UiScale.Set(previousScale); }
    }
}
