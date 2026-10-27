using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Bitmap = System.Drawing.Bitmap;
using DrawingColor = System.Drawing.Color;

internal static partial class Program
{
    private static Bitmap OutlineFixture()
    {
        var image = new Bitmap(80, 50);
        using var graphics = System.Drawing.Graphics.FromImage(image);
        graphics.Clear(DrawingColor.Transparent);
        using var fill = new System.Drawing.SolidBrush(DrawingColor.FromArgb(120, 25, 100, 200));
        graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
        graphics.FillRectangle(fill, 10, 10, 60, 30);
        return image;
    }

    private static void CheckPreviewOutline(string temp, string? renders)
    {
        using var original = OutlineFixture();
        var borderColor = DrawingColor.FromArgb(210, 30, 90);
        foreach (int thickness in new[] { 1, 2, 7, 32 })
        {
            using var framed = CaptureOutlineRenderer.Render(original, thickness, borderColor);
            bool matches = framed.Width == original.Width + 2 * thickness && framed.Height == original.Height + 2 * thickness;
            for (int y = 0; y < framed.Height; y++)
            for (int x = 0; x < framed.Width; x++)
            {
                bool inside = x >= thickness && x < thickness + original.Width && y >= thickness && y < thickness + original.Height;
                var expected = inside ? original.GetPixel(x - thickness, y - thickness) : borderColor;
                var actual = framed.GetPixel(x, y);
                matches &= expected.A == 0 ? actual.A == 0 : actual.ToArgb() == expected.ToArgb();
            }
            Assert(matches, $"the {thickness}px outline preserves every visible source pixel and alpha");
        }
        Assert(!CaptureOutlineRenderer.TryGetSize(int.MaxValue, 100, 32, out _)
            && !CaptureOutlineRenderer.TryGetSize(8192, 1, 1, out _)
            && !CaptureOutlineRenderer.TryGetSize(8000, 8000, 1, out _),
            "outline size checks reject overflow, dimension and pixel limits before allocating");
        var sessionType = typeof(CapturePreviewDialog).Assembly.GetType("CyberSnap.UI.PreviewSession")!;
        var owned = Activator.CreateInstance(sessionType, [(Bitmap)original.Clone(), null, false, CaptureKind.Screenshot, 1])!;
        Call(owned, "SetScaleFactor", 2);
        Call(owned, "SetOutline", true, 2, borderColor.ToArgb());
        var basePixels = (Bitmap)Property(owned, "Bitmap");
        var scaledPixels = (Bitmap)Property(owned, "ScaledBitmap");
        var transferred = (Bitmap)Property(owned, "EffectiveBitmap");
        Call(owned, "DetachOwnedBitmaps");
        ((IDisposable)owned).Dispose();
        bool Disposed(Bitmap image)
        {
            try { image.GetPixel(0, 0); return false; }
            catch (ArgumentException) { return true; }
        }
        Assert(Disposed(basePixels) && Disposed(scaledPixels) && transferred.Width == 164,
            "committing transfers only the final bitmap and releases intermediate source and scaled images");
        transferred.Dispose();
        using (var large = new Bitmap(8190, 1))
        {
            var session = Activator.CreateInstance(sessionType, [(Bitmap)large.Clone(), null, true, CaptureKind.Screenshot, 1])!;
            try
            {
                Call(session, "SetOutline", true, 1, borderColor.ToArgb());
                var before = (Bitmap)Property(session, "EffectiveBitmap");
                Call(session, "MarkCopied");
                bool rejected = false;
                try { Call(session, "SetOutline", true, 32, borderColor.ToArgb()); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { rejected = true; }
                Assert(rejected && ReferenceEquals(before, Property(session, "EffectiveBitmap"))
                    && before.Width == 8192 && (bool)Property(session, "ClipboardAlreadyCopied"),
                    "a rejected outline preserves the previous pixels and output state");
            }
            finally { ((IDisposable)session).Dispose(); }
        }
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        try
        {
            foreach (string language in new[] { "en", "es" })
            {
                using var settings = new SettingsService(Path.Combine(temp, "outline-" + language + ".json"));
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.CapturePreviewTimeoutSeconds = 0;
                settings.Settings.UiScale = 1;
                settings.Save(); settings.FlushPendingWrites();
                var early = Path.Combine(temp, "outline-early-" + language + ".png");
                CaptureOutputService.SavePng(original, early);
                var preview = new CapturePreviewDialog((Bitmap)original.Clone(), settings, savedFilePath: early, clipboardAlreadyCopied: true);
                try
                {
                    preview.Show(); Pump();
                    var session = Field(preview, "_active");
                    Assert((bool)Property(session, "ClipboardAlreadyCopied") && (string)Property(session, "CurrentSavedFilePath") == early,
                        "an unedited preview reuses its eager copy and saved file");
                    var button = Element<Button>(preview, "PreviewOutlineBtn");
                    Assert(Element<TextBlock>(preview, "PreviewOutlineText").Text == LocalizationService.Translate(language, "Capture outline")
                        && AutomationProperties.GetName(button) == LocalizationService.Translate(language, "Outline options"),
                        $"the outline control is localized and accessible in {language}");
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    var picker = (FrameworkElement)Field(preview, "_previewBackgroundColorPicker");
                    var enabled = VisualDescendants(picker).OfType<CheckBox>().Single();
                    var slider = VisualDescendants(picker).OfType<Slider>().Single();
                    Assert(VisualDescendants(picker).OfType<TextBlock>().Any(t =>
                        t.Text == LocalizationService.Translate(language, "Capture outline") && t.ActualWidth > 0),
                        "the enable switch has a visible localized label as well as an accessible name");
                    enabled.IsChecked = true; slider.Value = 7;
                    ((TextBox)Field(picker, "_boxHex")).Text = "D21E5A"; Pump();
                    Assert(preview.EffectiveBitmap.Width == 94 && preview.EffectiveBitmap.Height == 64
                        && preview.EffectiveBitmap.GetPixel(0, 0).ToArgb() == borderColor.ToArgb()
                        && preview.EffectiveBitmap.GetPixel(7, 7).A == 0
                        && preview.EffectiveBitmap.GetPixel(47, 32).A == 120,
                        "outline controls update real dimensions, color and transparent pixels");
                    Assert(!(bool)Property(session, "ClipboardAlreadyCopied") && Property(session, "CurrentSavedFilePath") is null,
                        "editing invalidates the eager copy and stale clipboard file references");
                    Call(session, "MarkCopied");
                    Assert((bool)Property(session, "ClipboardAlreadyCopied"), "copy state follows the current rendered revision");
                    slider.Value = 3; Pump();
                    Assert(!(bool)Property(session, "ClipboardAlreadyCopied"), "changing thickness requires a fresh automatic copy");
                    string named = Path.Combine(temp, "outline-named-" + language + ".png");
                    CaptureOutputService.SavePng(preview.EffectiveBitmap, named);
                    SetProperty(session, "SavedFilePath", named);
                    Assert((string)Property(session, "CurrentSavedFilePath") == named, "Save as associates the named file with the current output");
                    slider.Value = 5; Pump();
                    Assert(Property(session, "CurrentSavedFilePath") is null, "later edits no longer offer a stale named file to the clipboard");
                    Call(preview, "ClosePreviewBackgroundColorPicker", false);
                    Call(preview, "ApplyScaleFactor", 2); Pump();
                    Assert(preview.EffectiveBitmap.Width == 170 && preview.EffectiveBitmap.Height == 110,
                        "scaling rebuilds from the original and keeps outline thickness in output pixels");
                    using (var stream = new MemoryStream())
                    {
                        CaptureOutputService.WritePng(preview.EffectiveBitmap, stream);
                        stream.Position = 0;
                        using var decoded = new Bitmap(stream);
                        Assert(decoded.Width == 170 && decoded.GetPixel(0, 0).ToArgb() == borderColor.ToArgb(),
                            "the PNG payload used by copying and saving includes the outline");
                    }
                    Call(preview, "AddSession", (Bitmap)original.Clone(), null!, false, CaptureKind.Screenshot); Pump();
                    Call(preview, "ApplyOutlineSettings", true, 9, DrawingColor.Blue.ToArgb());
                    Call(preview, "ActivateSessionAt", 0); Pump();
                    Assert(preview.EffectiveBitmap.Width == 170 && preview.EffectiveBitmap.GetPixel(0, 0).ToArgb() == borderColor.ToArgb(),
                        "existing preview tabs keep independent outline settings");
                    Call(preview, "ApplyOutlineSettings", false, 5, borderColor.ToArgb());
                    Call(preview, "ApplyScaleFactor", 1); Pump();
                    Assert(preview.EffectiveBitmap.Width == original.Width && preview.EffectiveBitmap.GetPixel(0, 0).A == 0
                        && !(bool)Property(session, "ClipboardAlreadyCopied"),
                        "removing edits restores the original and invalidates previously delivered edited versions");
                    Call(preview, "ApplyOutlineSettings", true, 6, borderColor.ToArgb());
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    picker = (FrameworkElement)Field(preview, "_previewBackgroundColorPicker");
                    picker.MaxHeight = 260; Pump();
                    Render(preview, renders, "preview-outline-" + language + ".png");
                    Render(picker, renders, "outline-picker-" + language + ".png");
                    var reset = VisualDescendants(picker).OfType<Button>().Single(b => VisualDescendants(b).OfType<TextBlock>()
                        .Any(t => string.Equals(t.Text, LocalizationService.Translate(language, "Reset"), StringComparison.OrdinalIgnoreCase)));
                    reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
                    Assert(preview.EffectiveBitmap.Width == original.Width && !(bool)Property(session, "OutlineEnabled"),
                        "Reset removes the outline and restores its default settings without altering source pixels");
                    VisualDescendants(picker).OfType<CheckBox>().Single().IsChecked = true;
                    VisualDescendants(picker).OfType<Slider>().Single().Value = 6;
                    ((TextBox)Field(picker, "_boxHex")).Text = "D21E5A"; Pump();
                    Call(preview, "ClosePreviewBackgroundColorPicker", false);
                    settings.FlushPendingWrites();
                    using var reload = new SettingsService(Path.Combine(temp, "outline-" + language + ".json"));
                    reload.Load();
                    Assert(reload.Settings.PreviewOutlineEnabled && reload.Settings.PreviewOutlineThickness == 6
                        && reload.Settings.PreviewOutlineColorArgb == borderColor.ToArgb(), "outline preferences survive a disk reload");
                    CheckPreviewPickerLayout(preview, language, renders, "PreviewOutlineBtn");
                }
                finally { preview.CloseFromReplace(); Pump(); }
            }
        }
        finally { LocalizationService.ApplyCurrentCulture(originalLanguage); }
    }
}
