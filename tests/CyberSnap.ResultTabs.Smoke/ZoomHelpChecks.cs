using System.Drawing;
using System.IO;
using CyberSnap.Models;
using CyberSnap.Services;

internal static partial class Program
{
    private static void CheckZoomHelpBar(string? renders = null)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        try
        {
            foreach (string language in new[] { "en", "es" })
            {
                LocalizationService.ApplyCurrentCulture(language);
                foreach (var size in new[] { new Size(320, 240), new Size(800, 600), new Size(1920, 1080) })
                {
                    using var source = new Bitmap(size.Width, size.Height);
                    using var form = NewZoomForm(source, new AppSettings { ZoomAnimate = false, ZoomShowHelpBar = true });
                    string help = (string)Field(form, "_viewHelp");
                    Assert(help.Split('\n').Length == 2 && help.Contains("Ctrl+C") && help.Contains("Ctrl+S")
                        && help.Contains("C:") && help.Contains("Enter:") && help.Contains("Esc")
                        && help.Contains("↑") && help.Contains("↓") && help.Contains("+ / -"),
                        $"Zoom help documents capture, direct copy/save, zoom controls and exit in two lines: {language}, {size}");
                    Assert(help.Contains(language == "es" ? "copiar vista completa" : "copy full view")
                        && help.Contains(language == "es" ? "guardar vista completa" : "save full view"),
                        "Zoom copy and save hints are translated: " + language);
                    var layout = ((string Text, Rectangle Bounds, int Padding))Call(form, "HudLayout")!;
                    Assert(form.ClientRectangle.Contains(layout.Bounds)
                        && layout.Bounds.Height > 2 * layout.Padding,
                        $"the complete localized Zoom help fits the viewport: {language}, {size}");
                    if (renders is not null && size.Width == 800)
                    {
                        Directory.CreateDirectory(renders);
                        SetField(form, "_rendererUnavailable", true);
                        using var frame = form.RenderView(includeChrome: true);
                        frame.Save(Path.Combine(renders, "zoom-help-" + language + ".png"));
                    }
                }
            }
        }
        finally { LocalizationService.ApplyCurrentCulture(originalLanguage); }
    }
}
