using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;

internal static partial class Program
{
    private static readonly AutoCopyKind[] CopyKinds = Enum.GetValues<AutoCopyKind>();

    private static bool[] CopyFlags(AppSettings settings) =>
    [
        settings.ImageAutoCopyToClipboard, settings.OcrAutoCopyToClipboard,
        settings.VideoAutoCopyToClipboard, settings.GifAutoCopyToClipboard,
        settings.ScanAutoCopyToClipboard, settings.ColorDetailAutoCopy
    ];

    private static AppSettings CopySettings(int mask) => new()
    {
        ImageAutoCopyToClipboard = (mask & 1) != 0,
        OcrAutoCopyToClipboard = (mask & 2) != 0,
        VideoAutoCopyToClipboard = (mask & 4) != 0,
        GifAutoCopyToClipboard = (mask & 8) != 0,
        ScanAutoCopyToClipboard = (mask & 16) != 0,
        ColorDetailAutoCopy = (mask & 32) != 0,
        OcrShowResultWindow = false,
        ScanShowResultWindow = false,
        ShowColorDetailWindow = false
    };

    private static void AssertOnlyCopyChanged(bool[] before, AppSettings settings, AutoCopyKind kind, bool enabled, string message)
    {
        var expected = before.ToArray();
        expected[Array.IndexOf(CopyKinds, kind)] = enabled;
        Assert(expected.SequenceEqual(CopyFlags(settings)), message, announce: false);
        Assert(CopyKinds.Select(k => AutoCopyPreferences.ShouldCopy(settings, k)).SequenceEqual(expected),
            "capture routing reads the same independent preferences", announce: false);
    }

    private static bool[] WindowFlags(AppSettings settings) =>
        [settings.OcrShowResultWindow, settings.ScanShowResultWindow, settings.ShowColorDetailWindow];

    private static void CheckIndependentAutoCopy(string temp)
    {
        Assert(CopyFlags(new AppSettings()).SequenceEqual(new[] { true, false, false, false, false, true }),
            "fresh settings have explicit defaults for all six result types");

        for (int mask = 0; mask < 64; mask++)
        {
            foreach (var kind in CopyKinds)
            foreach (bool enabled in new[] { false, true })
            {
                var settings = CopySettings(mask);
                var before = CopyFlags(settings);
                AutoCopyPreferences.SetKindEnabled(settings, kind, enabled);
                AssertOnlyCopyChanged(before, settings, kind, enabled, "direct preference changes only its own result type");
                Assert(WindowFlags(settings).All(value => !value), "raw preference changes do not change window flags", false);

                settings = CopySettings(mask);
                ResultDelivery.SetCopyEnabled(settings, kind, enabled);
                AssertOnlyCopyChanged(before, settings, kind, enabled, "delivery toggle changes only its own result type");
                var expectedWindows = new[] { false, false, false };
                int windowIndex = kind switch { AutoCopyKind.Ocr => 0, AutoCopyKind.Scan => 1, AutoCopyKind.Color => 2, _ => -1 };
                if (!enabled && windowIndex >= 0) expectedWindows[windowIndex] = true;
                Assert(WindowFlags(settings).SequenceEqual(expectedWindows), "delivery safety changes only its own window", false);

                if (windowIndex >= 0)
                {
                    settings = CopySettings(mask);
                    ResultDelivery.SetShowWindow(settings, kind, enabled);
                    AssertOnlyCopyChanged(before, settings, kind, !enabled || before[Array.IndexOf(CopyKinds, kind)],
                        "window preference enables only its own auto-copy when hidden");
                    expectedWindows = [false, false, false];
                    expectedWindows[windowIndex] = enabled;
                    Assert(WindowFlags(settings).SequenceEqual(expectedWindows), "window preference leaves other windows unchanged", false);
                }
            }

            foreach (var preset in AfterCaptureOutcomePresets.Selectable)
            {
                var settings = CopySettings(mask);
                var before = CopyFlags(settings);
                var state = AfterCaptureOutcomePresets.GetState(preset);
                AfterCaptureOutcomeModel.ApplyToSettings(state, settings);
                AssertOnlyCopyChanged(before, settings, AutoCopyKind.Image, state.Clipboard, "image preset leaves other types unchanged");
                Assert(AfterCaptureOutcomeModel.FromSettings(settings).Clipboard == state.Clipboard,
                    "image outcome reads the image clipboard preference", false);
            }

            foreach (bool enabled in new[] { false, true })
            {
                var settings = CopySettings(mask);
                var before = CopyFlags(settings);
                AfterCapturePreferences.ApplyToSettings(new AfterCaptureViewPreference(0, enabled), settings);
                AssertOnlyCopyChanged(before, settings, AutoCopyKind.Image, enabled, "image destination editor leaves other types unchanged");
                Assert(AfterCapturePreferences.FromSettings(settings).Copy == enabled, "image destination reads its own copy flag", false);

                foreach (var media in Enum.GetValues<RecordingOutcomeKind>())
                {
                    settings = CopySettings(mask);
                    var state = RecordingOutcomeModel.FromSettings(settings, media) with { Clipboard = enabled };
                    RecordingOutcomeModel.ApplyToSettings(state, settings, media);
                    var kind = media == RecordingOutcomeKind.Video ? AutoCopyKind.Video : AutoCopyKind.Gif;
                    AssertOnlyCopyChanged(before, settings, kind, enabled, "recording outcome changes only its own format");
                    Assert(RecordingOutcomeModel.FromSettings(settings, media).Clipboard == enabled,
                        "recording outcome reads its own copy flag", false);
                }
            }

            string path = Path.Combine(temp, "copy-" + mask + ".json");
            using var service = new SettingsService(path);
            // Public settings are mutated exactly as the result/settings controls do.
            foreach (var kind in CopyKinds)
                AutoCopyPreferences.SetKindEnabled(service.Settings, kind, (mask & (1 << Array.IndexOf(CopyKinds, kind))) != 0);
            service.Save();
            service.FlushPendingWrites();
            using var reloaded = new SettingsService(path);
            reloaded.Load();
            Assert(CopyFlags(reloaded.Settings).SequenceEqual(CopyFlags(service.Settings)),
                "all six independent preferences survive save and reload", false);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var copyProperties = document.RootElement.EnumerateObject()
                .Where(p => p.Name.Contains("AutoCopy", StringComparison.Ordinal))
                .Select(p => p.Name).Order().ToArray();
            Assert(copyProperties.SequenceEqual(new[] { "ColorDetailAutoCopy", "GifAutoCopyToClipboard", "ImageAutoCopyToClipboard",
                "OcrAutoCopyToClipboard", "ScanAutoCopyToClipboard", "VideoAutoCopyToClipboard" }.Order()),
                "settings persist exactly the six direct clipboard preferences", false);
        }
        Console.WriteLine("PASS: all 64 auto-copy combinations, delivery guards, image presets, recording formats, and persistence.");
    }

    private static void CheckWidget(SettingsService settings, string? outputDirectory)
    {
        settings.Settings.DisableAnimations = true;
        foreach (string language in new[] { "en", "es" })
        foreach (double scale in new[] { 1.0, 1.4 })
        {
            settings.Settings.InterfaceLanguage = language;
            settings.Settings.UiScale = scale;
            UiScale.Set(scale);
            settings.Save();
            var before = CopyFlags(settings.Settings);
            // Keep physical hover on the shared desktop from collapsing a render.
            var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
            try
            {
                widget.Show();
                widget.GetType().GetField("_isExpanded", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .SetValue(widget, true);
                widget.PositionWindow();
                ((System.Windows.Threading.DispatcherTimer)Field(widget, "_collapseTimer")).Stop();
                ((System.Windows.Threading.DispatcherTimer)Field(widget, "_hoverDelayTimer")).Stop();
                Pump();
                widget.UpdateLayout();
                var panel = Element<Border>(widget, "MainPanelBorder");
                var toggle = Element<ToggleButton>(widget, "CaptureCursorToggle");
                Assert(VisualDescendants(panel).OfType<ToggleButton>().SequenceEqual(new[] { toggle }),
                    "widget exposes only its cursor toggle", false);
                var toggleBounds = toggle.TransformToAncestor(panel).TransformBounds(new Rect(toggle.RenderSize));
                Assert(toggleBounds.Bottom <= panel.ActualHeight && toggleBounds.Right <= panel.ActualWidth,
                    "cursor toggle fits the reduced widget panel", false);
                Assert(CopyFlags(settings.Settings).SequenceEqual(before), "opening the widget preserves all copy preferences", false);
                if (scale == 1.0)
                    Render(widget, outputDirectory, "widget-independent-" + language + ".png");
                widget.CollapseImmediately();
                Pump();
                Assert(widget.ActualHeight > 0 && widget.ActualWidth > 0, "widget still collapses to its edge peek", false);
            }
            finally { widget.Close(); }
        }
        Console.WriteLine("PASS: reduced widget, remaining cursor toggle, expansion/collapse, EN/ES, and UI scales 1.0/1.4.");
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }
}
