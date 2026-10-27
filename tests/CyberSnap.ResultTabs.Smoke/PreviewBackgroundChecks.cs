using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
                    CheckPreviewControlsLayout(preview, language, renders);
                    CheckPreviewEffectStates(preview, language, renders);
                    Thread.Sleep(220); Pump();
                    Render(preview, renders, "preview-checkerboard-" + language + ".png");
                    settings.FlushPendingWrites(); reloaded.Load();
                    Assert(reloaded.Settings.PreviewShowCheckerboard, "reenabling the checkerboard survives a disk reload");
                    CheckPreviewCustomBackground(preview, settings, settingsPath, language, renders);
                    CheckPreviewPickerLayout(preview, language, renders);
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

    private static void CheckPreviewCustomBackground(CapturePreviewDialog preview, SettingsService settings,
        string settingsPath, string language, string? renders)
    {
        var button = Element<Button>(preview, "PreviewBackgroundColorBtn");
        var background = Element<Border>(preview, "CheckerboardHost");
        var source = Element<Image>(preview, "PreviewImage").Source;
        Color CurrentColor() => ((SolidColorBrush)background.Background).Color;
        object OpenPicker()
        {
            // A real click activates the owner before opening its popup. A
            // synthetic Click event needs that activation explicitly.
            Assert(preview.Activate() || preview.IsActive, "Preview can activate before opening its color picker");
            Pump();
            bool wasActive = preview.IsActive;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert(Field(preview, "_previewBackgroundColorPopup") is Popup { IsOpen: true },
                $"the preview swatch opens the existing color picker (active before={wasActive}, after={preview.IsActive})");
            return Field(preview, "_previewBackgroundColorPicker");
        }
        void PressKey(Key key)
        {
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(preview),
                Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            Call(preview, "Window_PreviewKeyDown", preview, args); Pump();
            Assert(args.Handled && preview.IsVisible, "picker keys close the flyout while keeping Preview open");
        }
        Assert(AutomationProperties.GetName(button) == LocalizationService.Translate(language, "Solid background and margin")
            && (string)button.ToolTip == LocalizationService.Translate(language, "Solid background and margin"),
            $"the custom background swatch has localized help and an accessible name in {language}");
        Theme.SetMode(AppThemeMode.Light); preview.ApplyTheme(); Pump();
        var picker = OpenPicker();
        var hex = (TextBox)Field(picker, "_boxHex");
        hex.Text = "000000"; Pump();
        Assert(CurrentColor() == Colors.Black && !settings.Settings.PreviewShowCheckerboard
            && settings.Settings.PreviewBackgroundLightColorArgb == unchecked((int)0xFF000000),
            "choosing black switches from checkerboard to an opaque solid and preserves a valid zero RGB value");
        hex.Text = "12"; Pump();
        Assert(CurrentColor() == Colors.Black, "an incomplete HEX value preserves the current background");
        hex.Text = "GGGGGG"; Pump();
        Assert(CurrentColor() == Colors.Black, "an invalid HEX value preserves the current background");
        hex.Text = "12AB34"; Pump();
        var light = Color.FromRgb(0x12, 0xAB, 0x34);
        Assert(CurrentColor() == light && (Color)Property(picker, "SelectedColor") == light,
            "valid HEX input immediately updates the background and the selected picker color");
        Render((FrameworkElement)picker, renders, "preview-color-picker-" + language + ".png");
        PressKey(Key.Enter);
        Assert(Field(preview, "_previewBackgroundColorPopup") is null && CurrentColor() == light,
            "Enter accepts the custom color without committing or closing the capture");
        Call(preview, "ActivateSessionAt", 1); Pump();
        Assert(CurrentColor() == light, "custom background colors apply to every preview tab");
        Call(preview, "ActivateSessionAt", 0); Pump();
        Assert(ReferenceEquals(source, Element<Image>(preview, "PreviewImage").Source),
            "choosing a custom background does not replace the source image");

        Theme.SetMode(AppThemeMode.Dark); preview.ApplyTheme(); Pump();
        Assert(CurrentColor() == Color.FromRgb(51, 51, 55), "a light-theme override leaves the dark default intact");
        picker = OpenPicker();
        ((TextBox)Field(picker, "_boxHex")).Text = "2B183F"; Pump();
        var dark = Color.FromRgb(0x2B, 0x18, 0x3F);
        PressKey(Key.Escape);
        Assert(CurrentColor() == dark && Field(preview, "_previewBackgroundColorPopup") is null,
            "Escape dismisses the picker while keeping the live color choice");
        Theme.SetMode(AppThemeMode.Grayscale); preview.ApplyTheme(); Pump();
        Assert(CurrentColor() == dark, "both dark themes share the chosen custom color");
        settings.FlushPendingWrites();
        using var reloaded = new SettingsService(settingsPath);
        reloaded.Load();
        Assert(reloaded.Settings.PreviewBackgroundDarkColorArgb == settings.Settings.PreviewBackgroundDarkColorArgb
            && reloaded.Settings.PreviewBackgroundLightColorArgb == settings.Settings.PreviewBackgroundLightColorArgb,
            "both custom colors survive an actual disk reload");
        using (var reopened = new PreviewLifetime(new CapturePreviewDialog(new System.Drawing.Bitmap(20, 20), reloaded)))
            Assert(((SolidColorBrush)Element<Border>(reopened.Window, "CheckerboardHost").Background).Color == dark,
                "new preview sessions restore the theme's custom background color");

        picker = OpenPicker();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Assert(Field(preview, "_previewBackgroundColorPopup") is null, "clicking the swatch again closes its picker without reopening it");
        picker = OpenPicker();
        var reset = VisualDescendants((DependencyObject)picker).OfType<Button>()
            .Single(b => VisualDescendants(b).OfType<TextBlock>().Any(text => string.Equals(text.Text,
                LocalizationService.Translate(language, "Theme default"), StringComparison.OrdinalIgnoreCase)));
        reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
        Assert(settings.Settings.PreviewBackgroundDarkColorArgb is null
            && settings.Settings.PreviewBackgroundLightColorArgb is not null
            && CurrentColor() == Color.FromRgb(51, 51, 55)
            && (Color)Property(picker, "SelectedColor") == CurrentColor(),
            "reset restores the current theme's solid default in both the picker and Preview without clearing the other theme");
        Call(picker, "AcceptFromKeyboard"); Pump();
        picker = OpenPicker();
        var outside = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        preview.RaiseEvent(outside); Pump();
        Assert(Field(preview, "_previewBackgroundColorPopup") is null && !outside.Handled,
            "clicking outside dismisses the flyout and preserves the original action");
        picker = OpenPicker();
        Theme.SetMode(AppThemeMode.Light); preview.ApplyTheme(); Pump();
        Assert(Field(preview, "_previewBackgroundColorPopup") is null && CurrentColor() == light,
            "changing theme dismisses the picker and restores that theme's independent color");
        Assert(preview.EffectiveBitmap.GetPixel(0, 0).A == 0
            && preview.EffectiveBitmap.GetPixel(600, 400).A == 120,
            "custom colors never flatten transparent or translucent image pixels");
        Call(preview, "SetZoomOverlayVisibility", true); Pump();
        Render(preview, renders, "preview-custom-background-" + language + ".png");
    }
}
