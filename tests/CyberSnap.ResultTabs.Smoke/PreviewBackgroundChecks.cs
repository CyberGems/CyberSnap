using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;

internal static partial class Program
{
    private static void CheckPreviewBackground(string temp, string? renders)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        double originalScale = UiScale.Current;
        Assert(SettingsService.TryDeserialize("{}", out var defaults) && defaults.PreviewShowCheckerboard,
            "existing preferences retain the checkerboard by default");
        try
        {
            foreach (string language in new[] { "en", "es" })
            {
                string settingsPath = Path.Combine(temp, "preview-background-" + language + ".json");
                using var settings = new SettingsService(settingsPath);
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.CapturePreviewTimeoutSeconds = 0;
                settings.Settings.UiScale = 1;
                settings.Settings.DisableAnimations = true;
                settings.Save();
                settings.FlushPendingWrites();
                LocalizationService.ApplyCurrentCulture(language);
                using var pixels = new System.Drawing.Bitmap(1200, 800);
                using (var graphics = System.Drawing.Graphics.FromImage(pixels))
                using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(120, 25, 100, 200)))
                    graphics.FillRectangle(fill, 100, 100, 1000, 600);
                pixels.SetPixel(600, 400, System.Drawing.Color.FromArgb(120, 25, 100, 200));
                var preview = new CapturePreviewDialog((System.Drawing.Bitmap)pixels.Clone(), settings);
                try
                {
                    preview.Show(); Pump();
                    var toggle = Element<ToggleButton>(preview, "PreviewBackgroundBtn");
                    var background = Element<Border>(preview, "CheckerboardHost");
                    var image = Element<Image>(preview, "PreviewImage");
                    var viewport = Element<ScrollViewer>(preview, "ZoomViewport");
                    Rect ControlBounds(FrameworkElement control) => control.TransformToAncestor(preview).TransformBounds(new Rect(control.RenderSize));
                    var provider = (IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!;
                    Assert(toggle.IsChecked == true && background.Background is DrawingBrush,
                        $"Preview initially shows the checkerboard in {language}");
                    Assert(AutomationProperties.GetName(toggle) == LocalizationService.Translate(language, "Checkerboard background")
                        && (string)toggle.ToolTip == LocalizationService.Translate(language, "Switch to solid background"),
                        $"the icon-only toggle has localized help and an accessible name in {language}");
                    Call(preview, "SetZoom", 2.0); Pump();
                    viewport.ScrollToHorizontalOffset(100); viewport.ScrollToVerticalOffset(80); Pump();
                    double horizontal = viewport.HorizontalOffset, vertical = viewport.VerticalOffset;
                    var source = image.Source;
                    provider.Toggle(); Pump();
                    Assert(provider.ToggleState == ToggleState.Off && !settings.Settings.PreviewShowCheckerboard,
                        "assistive technology toggles and saves the same preference as the pointer and keyboard");
                    Assert(ReferenceEquals(source, image.Source) && Element<TextBlock>(preview, "ZoomLevelText").Text == "200%"
                        && viewport.HorizontalOffset == horizontal && viewport.VerticalOffset == vertical,
                        "switching backgrounds preserves the image, zoom and pan");
                    Assert((string)toggle.ToolTip == LocalizationService.Translate(language, "Show checkerboard background"),
                        $"the tooltip describes the next background action in {language}");
                    Assert(toggle.Focus(), "the background toggle is reachable by keyboard focus");
                    Pump();
                    SetField(preview, "_zoomPointerInside", false);
                    Call(preview, "UpdateZoomControlsVisibility");
                    Assert(Element<Border>(preview, "ZoomControlsOverlay").IsHitTestVisible,
                        "keyboard focus keeps the controls available without a pointer hover");
                    foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Grayscale, AppThemeMode.Light })
                    {
                        Theme.SetMode(theme); preview.ApplyTheme(); Pump();
                        var expected = theme == AppThemeMode.Light ? Color.FromRgb(234, 237, 245) : Color.FromRgb(51, 51, 55);
                        Assert(background.Background is SolidColorBrush brush && brush.Color == expected
                            && toggle.IsChecked == false,
                            $"solid Preview uses the exact sample color after changing to {theme}");
                        Call(preview, "SetZoomOverlayVisibility", true); Pump();
                        Thread.Sleep(220); Pump();
                        Render(preview, renders, $"preview-solid-{theme}-{language}.png");
                    }
                    Assert(preview.EffectiveBitmap.GetPixel(0, 0).A == 0
                        && preview.EffectiveBitmap.GetPixel(600, 400).ToArgb() == pixels.GetPixel(600, 400).ToArgb(),
                        "solid backgrounds preserve transparent and translucent source pixels");
                    using (var png = new MemoryStream())
                    {
                        CaptureOutputService.WritePng(preview.EffectiveBitmap, png);
                        png.Position = 0;
                        using var exported = new System.Drawing.Bitmap(png);
                        Assert(exported.GetPixel(0, 0).A == 0
                            && exported.GetPixel(600, 400).ToArgb() == pixels.GetPixel(600, 400).ToArgb(),
                            "the PNG output used for saving and clipboard delivery preserves alpha and color");
                    }
                    settings.FlushPendingWrites();
                    using var reloaded = new SettingsService(settingsPath);
                    reloaded.Load();
                    Assert(!reloaded.Settings.PreviewShowCheckerboard, "the solid preference survives an actual disk reload");
                    using (var reopened = new PreviewLifetime(new CapturePreviewDialog(new System.Drawing.Bitmap(20, 20), reloaded)))
                        Assert(Element<Border>(reopened.Window, "CheckerboardHost").Background is SolidColorBrush
                            && Element<ToggleButton>(reopened.Window, "PreviewBackgroundBtn").IsChecked == false,
                            "a new preview session restores the saved background");
                    Call(preview, "AddSession", new System.Drawing.Bitmap(200, 100), null!, false, CaptureKind.ScrollCapture); Pump();
                    Assert(toggle.IsChecked == false && background.Background is SolidColorBrush,
                        "new capture tabs inherit the current background");
                    provider.Toggle(); Pump();
                    Call(preview, "ActivateSessionAt", 0); Pump();
                    Assert(toggle.IsChecked == true && background.Background is DrawingBrush
                        && settings.Settings.PreviewShowCheckerboard, "background changes are shared across capture tabs");
                    foreach (double width in new[] { 580.0, 730.0, 860.0 })
                    {
                        preview.Width = width; preview.UpdateLayout(); Pump();
                        Call(preview, "SetZoomOverlayVisibility", true); Pump();
                        var zoom = ControlBounds(Element<Border>(preview, "ZoomControlsOverlay"));
                        var scale = ControlBounds(Element<Border>(preview, "ScaleControlsOverlay"));
                        Assert(!zoom.IntersectsWith(scale), $"background and zoom controls avoid the scale controls at width {width}");
                    }
                    Thread.Sleep(220); Pump();
                    Render(preview, renders, "preview-checkerboard-" + language + ".png");
                    settings.FlushPendingWrites(); reloaded.Load();
                    Assert(reloaded.Settings.PreviewShowCheckerboard, "reenabling the checkerboard survives a disk reload");
                }
                finally { preview.CloseFromReplace(); Pump(); }
            }
        }
        finally
        {
            LocalizationService.ApplyCurrentCulture(originalLanguage);
            Theme.SetMode(AppThemeMode.Dark);
            UiScale.Set(originalScale);
        }
    }
}
