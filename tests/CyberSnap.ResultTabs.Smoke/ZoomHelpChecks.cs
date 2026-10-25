using System.Drawing;
using System.IO;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckZoomHelpBar(string? renders = null)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            {
                Theme.SetMode(theme);
                LocalizationService.ApplyCurrentCulture(language);
                foreach (int dpi in new[] { 96, 144, 192 })
                foreach (double uiScale in new[] { 1.0, 1.4 })
                foreach (var size in new[] { new Size(320, 240), new Size(800, 600), new Size(1920, 1080) })
                {
                    var panelType = typeof(StandaloneZoomForm).Assembly.GetType("CyberSnap.Capture.ZoomHelpPanel")!;
                    using var panel = (IDisposable)Activator.CreateInstance(panelType, size, dpi / 96f * (float)uiScale, "2×")!;
                    var image = (Bitmap)panelType.GetProperty("Image")!.GetValue(panel)!;
                    var items = ((System.Collections.IEnumerable)panelType.GetProperty("Items")!.GetValue(panel)!).Cast<object>().ToArray();
                    var captions = items.Select(item => item.GetType().GetProperty("Hint")!.GetValue(item)!).ToArray();
                    var keys = captions.Select(item => (string)item.GetType().GetProperty("Keys")!.GetValue(item)!).ToArray();
                    var labels = captions.Select(item => (string)item.GetType().GetProperty("Label")!.GetValue(item)!).ToArray();
                    Assert(keys.Length == 6 && new[] { "C", "Enter", "Ctrl+C", "Ctrl+S" }.All(keys.Contains)
                        && keys.Any(key => key.Contains("↑") && key.Contains("↓") && key.Contains("+ / -"))
                        && keys.Any(key => key.Contains("Esc")),
                        $"Zoom help retains every control: {language}, {theme}, DPI={dpi}, UI={uiScale}, {size}", false);
                    Assert(labels.Contains(language == "es" ? "Copiar vista completa" : "Copy full view")
                        && labels.Contains(language == "es" ? "Guardar vista completa" : "Save full view")
                        && labels.Contains(language == "es" ? "Seleccionar y anotar" : "Select and annotate"),
                        "Zoom descriptions are localized: " + language, false);
                    var bounds = new Rectangle(Point.Empty, image.Size);
                    Assert(image.Width < size.Width && image.Height < size.Height && image.GetPixel(0, 0).A == 0,
                        "the rounded help card fits the viewport without stretching or clipping", false);
                    foreach (var item in items)
                    {
                        var keyBounds = (Rectangle)item.GetType().GetProperty("Keys")!.GetValue(item)!;
                        var labelBounds = (Rectangle)item.GetType().GetProperty("Label")!.GetValue(item)!;
                        Assert(bounds.Contains(keyBounds) && bounds.Contains(labelBounds) && !keyBounds.IntersectsWith(labelBounds)
                            && labelBounds.Width > 0 && labelBounds.Height > 0,
                            "each shortcut and description has its own complete, non-overlapping area", false);
                    }
                    if (renders is not null && size.Width == 800 && uiScale == 1.0 && dpi is 96 or 144)
                    {
                        Directory.CreateDirectory(renders);
                        image.Save(Path.Combine(renders, $"zoom-help-{language}-{theme}-{dpi}.png"));
                    }
                }
            }
            Console.WriteLine("PASS: localized Zoom help fits narrow and wide views, light/dark themes, 100/150/200% DPI and both UI scales.");
        }
        finally { Theme.SetMode(AppThemeMode.Dark); LocalizationService.ApplyCurrentCulture(originalLanguage); }
    }

    private static void CheckZoomHelpNative(string? renders)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (var screen in Forms.Screen.AllScreens)
            {
                LocalizationService.ApplyCurrentCulture(language);
                var area = screen.Bounds;
                var source = new Bitmap(area.Width, area.Height);
                using (var graphics = Graphics.FromImage(source)) graphics.Clear(Color.FromArgb(23, 27, 39));
                using var form = new StandaloneZoomForm(source, area,
                    new Point(area.X + area.Width / 2, area.Y + area.Height / 2), new AppSettings { ZoomAnimate = false });
                // A positioned HWND supplies real monitor DPI without starting interactive pointer/focus ownership.
                _ = form.Handle;
                var bounds = (Rectangle)Call(form, "HudLayout")!;
                int dpi = (int)GetDpiForWindow(form.Handle);
                Assert(((ValueTuple<Size, int, double, string>)Field(form, "_helpCacheKey")).Item2 == dpi,
                    "help uses the opening HWND DPI before its first visible frame: " + dpi);
                Assert(form.ClientRectangle.Contains(bounds), "native DPI keeps the complete help card inside its monitor: " + dpi);
                using var gpu = form.RenderView(includeChrome: true);
                Assert(!(bool)Field(form, "_rendererUnavailable"), "the native help uses the accelerated renderer");
                SetField(form, "_rendererUnavailable", true);
                using var cpu = form.RenderView(includeChrome: true);
                int maximumDifference = 0;
                for (int y = bounds.Top; y < bounds.Bottom; y++)
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    var a = gpu.GetPixel(x, y); var b = cpu.GetPixel(x, y);
                    maximumDifference = Math.Max(maximumDifference, Math.Max(Math.Abs(a.R-b.R), Math.Max(Math.Abs(a.G-b.G), Math.Abs(a.B-b.B))));
                }
                Assert(maximumDifference <= 2, $"CPU and GPU draw the same help pixels at DPI {dpi}: max delta={maximumDifference}");
                var panel = Field(form, "_helpPanel");
                using var repeat = form.RenderView(includeChrome: true);
                Assert(ReferenceEquals(panel, Field(form, "_helpPanel")), "unchanged help is cached during pointer panning");
                if (renders is not null)
                {
                    Directory.CreateDirectory(renders);
                    using var crop = gpu.Clone(bounds, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    crop.Save(Path.Combine(renders, $"zoom-help-native-{language}-{dpi}.png"));
                }
                form.Close();
            }
        }
        finally { LocalizationService.ApplyCurrentCulture(originalLanguage); }
    }
}
