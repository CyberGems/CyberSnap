using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Bitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;

internal static partial class Program
{
    private static void CheckPreviewMargin(string temp, string? renders)
    {
        using var source = OutlineFixture();
        var marginColor = DrawingColor.FromArgb(36, 104, 172);
        foreach (int outline in new[] { 0, 3 })
        foreach (int margin in new[] { 1, 16, CaptureFrameRenderer.MaxMargin })
        {
            using var framed = CaptureFrameRenderer.Render(source, outline, DrawingColor.Magenta, margin, marginColor);
            int padding = margin + outline;
            Assert(framed.Width == source.Width + 2 * padding && framed.Height == source.Height + 2 * padding
                && framed.GetPixel(0, 0).ToArgb() == marginColor.ToArgb()
                && framed.GetPixel(framed.Width - 1, framed.Height - 1).ToArgb() == marginColor.ToArgb()
                && framed.GetPixel(margin, margin).ToArgb() == (outline > 0 ? DrawingColor.Magenta.ToArgb() : 0),
                $"a {margin}px margin surrounds the {outline}px outline and expands all four edges");
            bool preserved = true;
            for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                var before = source.GetPixel(x, y);
                var after = framed.GetPixel(x + padding, y + padding);
                preserved &= before.A == 0 ? after.A == 0 : before.ToArgb() == after.ToArgb();
            }
            Assert(preserved, "the margin preserves every visible capture pixel and its original alpha");
        }
        Assert(!CaptureFrameRenderer.TryGetSize(int.MaxValue, 1, 32, 256, out _)
            && !CaptureFrameRenderer.TryGetSize(8192, 1, 0, 1, out _)
            && !CaptureFrameRenderer.TryGetSize(7000, 7000, 0, 100, out _),
            "combined frame validation rejects overflow, dimension and pixel limits");
        var sessionType = typeof(CapturePreviewDialog).Assembly.GetType("CyberSnap.UI.PreviewSession")!;
        using (var large = new Bitmap(8190, 1))
        {
            var session = Activator.CreateInstance(sessionType, [(Bitmap)large.Clone(), null, true, CaptureKind.Screenshot, 1])!;
            try
            {
                Call(session, "SetMargin", true, 1, marginColor.ToArgb());
                var before = (Bitmap)Property(session, "EffectiveBitmap");
                Call(session, "MarkCopied");
                bool rejected = false;
                try { Call(session, "SetMargin", true, 2, marginColor.ToArgb()); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
                Assert(rejected && ReferenceEquals(before, Property(session, "EffectiveBitmap"))
                    && (int)Property(session, "MarginSize") == 1 && (bool)Property(session, "ClipboardAlreadyCopied"),
                    "a rejected margin preserves the last valid image, options and copy revision");
            }
            finally { ((IDisposable)session).Dispose(); }
        }
        using (var wide = new Bitmap(4090, 2))
        {
            var session = Activator.CreateInstance(sessionType, [(Bitmap)wide.Clone(), null, false, CaptureKind.Screenshot, 1])!;
            try
            {
                Call(session, "SetMargin", true, 10, marginColor.ToArgb());
                var before = Property(session, "EffectiveBitmap");
                bool rejected = false;
                try { Call(session, "SetScaleFactor", 2); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
                Assert(!(bool)Call(session, "CanScaleFactor", 2)! && rejected
                    && ReferenceEquals(before, Property(session, "EffectiveBitmap")) && (int)Property(session, "ScaleFactor") == 1,
                    "scaling checks the margin before allocation and preserves the image when the combined output is too large");
            }
            finally { ((IDisposable)session).Dispose(); }
        }
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        try
        {
            foreach (string language in new[] { "en", "es" })
            {
                string path = Path.Combine(temp, "margin-" + language + ".json");
                using var settings = new SettingsService(path);
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.CapturePreviewTimeoutSeconds = 0;
                settings.Settings.DisableAnimations = true; settings.Settings.UiScale = 1;
                settings.Settings.PreviewBackgroundDarkColorArgb = marginColor.ToArgb();
                settings.Settings.PreviewBackgroundLightColorArgb = DrawingColor.LightGray.ToArgb();
                settings.Save(); settings.FlushPendingWrites();
                Theme.SetMode(AppThemeMode.Dark);
                var preview = new CapturePreviewDialog((Bitmap)source.Clone(), settings, clipboardAlreadyCopied: true);
                try
                {
                    preview.Show(); Pump();
                    var session = Field(preview, "_active");
                    Assert(!(bool)Property(session, "MarginEnabled") && preview.EffectiveBitmap.Width == source.Width,
                        "existing settings open captures without adding a margin");
                    var button = Element<Button>(preview, "PreviewBackgroundColorBtn");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    var picker = (FrameworkElement)Field(preview, "_previewBackgroundColorPicker");
                    var toggle = VisualDescendants(picker).OfType<CheckBox>().Single();
                    var slider = VisualDescendants(picker).OfType<Slider>().Single();
                    var number = VisualDescendants(picker).OfType<TextBox>().Single(box =>
                        AutomationProperties.GetName(box) == LocalizationService.Translate(language, "Margin size (px)"));
                    Assert(AutomationProperties.GetName(toggle) == LocalizationService.Translate(language, "Capture margin")
                        && VisualDescendants(picker).OfType<TextBlock>().Any(text => text.Text == LocalizationService.Translate(language, "Capture margin")),
                        $"the margin has a visible localized label and accessible numeric input in {language}");
                    toggle.IsChecked = true; slider.Value = 12; Pump();
                    Assert(preview.EffectiveBitmap.Width == 104 && number.Text == "12"
                        && preview.EffectiveBitmap.GetPixel(0, 0).ToArgb() == marginColor.ToArgb()
                        && !(bool)Property(session, "ClipboardAlreadyCopied"),
                        "the margin controls render the solid background color and invalidate the eager copy");
                    number.Text = ""; number.Text = "999"; Pump();
                    Assert(preview.EffectiveBitmap.Width == 104, "partial and out-of-range numeric input preserve the last valid margin");
                    number.Text = "24"; Pump();
                    Assert(slider.Value == 24 && preview.EffectiveBitmap.Width == 128, "numeric entry updates the slider and output exactly");
                    number.Text = "12";
                    ((TextBox)Field(picker, "_boxHex")).Text = "12AB34"; Pump();
                    var green = DrawingColor.FromArgb(0x12, 0xAB, 0x34);
                    Assert(preview.EffectiveBitmap.GetPixel(0, 0).ToArgb() == green.ToArgb(), "editing the solid color updates the actual margin pixels");
                    Call(preview, "ClosePreviewBackgroundColorPicker", false);
                    Call(preview, "ApplyOutlineSettings", true, 3, DrawingColor.Magenta.ToArgb()); Pump();
                    Assert(preview.EffectiveBitmap.Width == 110 && preview.EffectiveBitmap.Height == 80
                        && preview.EffectiveBitmap.GetPixel(12, 12).ToArgb() == DrawingColor.Magenta.ToArgb()
                        && preview.EffectiveBitmap.GetPixel(15, 15).A == 0,
                        "the margin stays outside the outline and keeps source transparency");
                    string saved = Path.Combine(temp, "margin-copy-" + language + ".png");
                    CaptureOutputService.SavePng(preview.EffectiveBitmap, saved);
                    SetProperty(session, "SavedFilePath", saved); Call(session, "MarkCopied");
                    Call(preview, "AddSession", (Bitmap)source.Clone(), null!, false, CaptureKind.Screenshot); Pump();
                    var second = Field(preview, "_active");
                    Assert((bool)Property(second, "MarginEnabled") && (int)Property(second, "MarginSize") == 12,
                        "new tabs inherit the remembered margin options");
                    Call(preview, "ApplyMarginSettings", true, 24);
                    Call(second, "MarkCopied");
                    Call(preview, "SetPreviewBackgroundColor", Colors.Blue, true); Pump();
                    Assert(preview.EffectiveBitmap.Width == 134 && !(bool)Property(second, "ClipboardAlreadyCopied"),
                        "changing the shared margin color invalidates the active tab's copy");
                    Call(preview, "ActivateSessionAt", 0); Pump();
                    Assert(preview.EffectiveBitmap.Width == 110 && preview.EffectiveBitmap.GetPixel(0, 0).ToArgb() == DrawingColor.Blue.ToArgb()
                        && !(bool)Property(session, "ClipboardAlreadyCopied") && Property(session, "CurrentSavedFilePath") is null,
                        "tabs retain separate margin sizes while shared color changes invalidate stale copies and files in every tab");
                    Theme.SetMode(AppThemeMode.Light); preview.ApplyTheme(); Pump();
                    Assert(preview.EffectiveBitmap.GetPixel(0, 0).ToArgb() == DrawingColor.LightGray.ToArgb(),
                        "the printed margin follows the light theme's selected solid color");
                    var beforeChecker = preview.EffectiveBitmap;
                    Element<System.Windows.Controls.Primitives.ToggleButton>(preview, "PreviewBackgroundBtn").IsChecked = true; Pump();
                    Assert(ReferenceEquals(beforeChecker, preview.EffectiveBitmap), "the checkerboard switch does not remove or change printed margin pixels");
                    Call(preview, "ApplyScaleFactor", 4); Pump();
                    Assert(preview.EffectiveBitmap.Width == 350 && preview.EffectiveBitmap.Height == 230,
                        "scaling affects the source while margin and outline stay in final-image pixels");
                    using (var bytes = new MemoryStream())
                    {
                        CaptureOutputService.WritePng(preview.EffectiveBitmap, bytes); bytes.Position = 0;
                        using var copied = new Bitmap(bytes);
                        Assert(copied.Width == 350 && copied.GetPixel(0, 0).ToArgb() == DrawingColor.LightGray.ToArgb()
                            && copied.GetPixel(12, 12).ToArgb() == DrawingColor.Magenta.ToArgb(),
                            "the PNG payload used by Copy includes both margin and outline");
                    }
                    Call(preview, "SetZoomOverlayVisibility", true); Pump();
                    Render(preview, renders, "preview-margin-" + language + ".png");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    picker = (FrameworkElement)Field(preview, "_previewBackgroundColorPicker");
                    Render(picker, renders, "margin-picker-" + language + ".png");
                    Call(preview, "ClosePreviewBackgroundColorPicker", false);
                    Call(preview, "ApplyMarginSettings", false, 12); Pump();
                    Assert(preview.EffectiveBitmap.Width == 326 && preview.EffectiveBitmap.GetPixel(0, 0).ToArgb() == DrawingColor.Magenta.ToArgb(),
                        "disabling the margin retains the scaled capture and its outline");
                    Call(preview, "ApplyMarginSettings", true, 20);
                    settings.FlushPendingWrites();
                    using var reload = new SettingsService(path); reload.Load();
                    Assert(reload.Settings.PreviewMarginEnabled && reload.Settings.PreviewMarginSize == 20,
                        "the enabled margin and its size survive a disk reload");
                    using (var reopened = new PreviewLifetime(new CapturePreviewDialog((Bitmap)source.Clone(), reload)))
                        Assert(reopened.Window.EffectiveBitmap.Width == 126, "a new preview restores the saved margin and outline defaults");
                }
                finally { preview.CloseFromReplace(); Pump(); }
            }
        }
        finally { LocalizationService.ApplyCurrentCulture(originalLanguage); Theme.SetMode(AppThemeMode.Dark); UiScale.Set(1); }
    }
}
