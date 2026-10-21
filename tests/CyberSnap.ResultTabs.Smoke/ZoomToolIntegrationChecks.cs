using System.Drawing;
using System.IO;
using CyberSnap.Capture;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Forms = System.Windows.Forms;
using MediaPoint = System.Windows.Point;

internal static partial class Program
{
    private static void CheckZoomToolIntegration(string temp, string? renderDirectory)
    {
        foreach (bool active in new[] { false, true })
        {
            var icon = FluentIcons.GetGeometry("zoom", active)!;
            Assert(icon is not null && !icon.FillContains(new MediaPoint(7, 7))
                && icon.FillContains(new MediaPoint(9.5, 9.5)) && icon.FillContains(new MediaPoint(3.5, 9.5)),
                "Zoom has a hollow lens and a visible plus in both icon states, active=" + active);
        }

        string path = Path.Combine(temp, "zoom-toolbar-migration.json");
        File.WriteAllText(path, "{\"EnabledTools\":[\"rect\",\"ocr\",\"picker\",\"ruler\"],\"ZoomInitialLevel\":2.5}");
        using (var settings = new SettingsService(path))
        {
            settings.Load();
            Assert(settings.Settings.EnabledTools!.SequenceEqual(new[] { "rect", "ocr", "picker", "ruler", "zoom" })
                && settings.Settings.ZoomInitialLevel == 2.5,
                "existing custom capture bars gain Zoom once without re-enabling other hidden tools");
            settings.Settings.EnabledTools!.Remove("zoom");
            settings.Save(); settings.FlushPendingWrites();
        }
        using (var settings = new SettingsService(path))
        {
            settings.Load();
            Assert(!settings.Settings.EnabledTools!.Contains("zoom"), "hiding Zoom remains respected after reloading an upgraded toolbar");
        }
        using (var settings = new SettingsService(Path.Combine(temp, "zoom-toolbar-fresh.json")))
        {
            settings.Settings.EnabledTools = new() { "rect", "ocr" };
            settings.Save(); settings.FlushPendingWrites();
            settings.Load();
            Assert(!settings.Settings.EnabledTools!.Contains("zoom"), "a fresh configuration can hide Zoom without migration re-enabling it");
        }

        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (var dock in Enum.GetValues<CaptureDockSide>())
            {
                LocalizationService.ApplyCurrentCulture(language);
                using var screenshot = new Bitmap(1200, 800);
                using var overlay = new RegionOverlayForm(screenshot, new Rectangle(0, 0, 1200, 800),
                    windowDetectionMode: WindowDetectionMode.Off) { CaptureDockSide = dock };
                overlay.SetEnabledTools(null);
                overlay.Show();
                Forms.Application.DoEvents();
                var tools = (ToolDef[])Field(overlay, "_mainBarTools");
                int index = Array.FindIndex(tools, tool => tool.Id == "zoom");
                var buttons = (Rectangle[])Field(overlay, "_toolbarButtons");
                Assert(index >= 0 && tools[index].Mode == CaptureMode.Zoom && buttons[index].Width > 0,
                    "Zoom is a visible capture-bar tool: " + dock + ", " + language);
                var toolbar = (Rectangle)Field(overlay, "_toolbarRect");
                Assert(toolbar.Contains(buttons[index]), "the Zoom button stays inside the capture bar: " + dock, false);
                if (renderDirectory is not null && dock == CaptureDockSide.Top)
                {
                    Directory.CreateDirectory(renderDirectory);
                    using var image = new Bitmap(toolbar.Width + 16, toolbar.Height + 16);
                    using (var graphics = Graphics.FromImage(image))
                    {
                        graphics.Clear(Color.FromArgb(13, 15, 23));
                        graphics.TranslateTransform(8 - toolbar.X, 8 - toolbar.Y);
                        overlay.PaintToolbarTo(graphics);
                    }
                    image.Save(Path.Combine(renderDirectory, "capture-bar-zoom-" + language + ".png"));
                }
                int launches = 0;
                overlay.StandaloneZoomRequested += () => { launches++; overlay.Hide(); overlay.Close(); };
                var button = buttons[index];
                var center = new Point(button.X + button.Width / 2, button.Y + button.Height / 2);
                Assert((int)Call(overlay, "GetToolbarButtonAt", center)! == index,
                    "the visible Zoom button is clickable: overlay=" + overlay.Visible
                    + ", toolbar=" + (Field(overlay, "_toolbarForm") as Forms.Form)?.Visible);
                Call(overlay, "OnMouseDown", new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1,
                    center.X, center.Y, 0));
                Assert(launches == 1 && !overlay.Visible,
                    "clicking the Zoom capture-bar button hands off once and closes the old overlay: " + dock + ", " + language
                    + ", launches=" + launches + ", mode=" + overlay.CurrentMode);
            }
        }
        finally { LocalizationService.ApplyCurrentCulture(originalLanguage); }
    }
}
