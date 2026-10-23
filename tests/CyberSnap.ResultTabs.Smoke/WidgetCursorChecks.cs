using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckWidgetCursorPreferences(string temp)
    {
        var app = (CyberSnap.App)Application.Current;
        foreach (string language in new[] { "en", "es" })
        {
            using var service = new SettingsService(Path.Combine(temp, "widget-cursors-" + language + ".json"));
            service.Settings.InterfaceLanguage = language;
            service.Settings.DisableAnimations = true;
            service.Save(); service.FlushPendingWrites();
            using var configService = new SettingsService(Path.Combine(temp, "widget-cursors-" + language + ".json"));
            configService.Load();
            using var history = new HistoryService();
            using var search = new ImageSearchIndexService();
            var config = new SettingsWindow(configService, history, search) { IsHitTestVisible = false };
            var widget = new CaptureWidgetWindow(service) { IsHitTestVisible = false };
            SetZoomAppField(app, "_widgetWindow", widget);
            try
            {
                config.Show(); widget.Show(); Pump();
                var kinds = new[] { AutoCopyKind.Image, AutoCopyKind.Video, AutoCopyKind.Gif };
                string[] boxes = { "ShowCursorCheck", "VideoShowCursorCheck", "GifShowCursorCheck" };
                bool[] Flags(AppSettings settings) => new[] { settings.ShowCursor, settings.VideoShowCursor, settings.GifShowCursor };
                bool[] Controls() => boxes.Select(name => Element<CheckBox>(config, name).IsChecked == true).ToArray();
                for (int mask = 0; mask < 8; mask++)
                {
                    service.Settings.ShowCursor = (mask & 1) != 0;
                    service.Settings.VideoShowCursor = (mask & 2) != 0;
                    service.Settings.GifShowCursor = (mask & 4) != 0;
                    service.Save(); Pump();
                    Assert(Controls().SequenceEqual(Flags(service.Settings)), "Settings reflects each independent cursor combination: " + language, false);
                    using var menu = (Forms.ContextMenuStrip)Call(widget, "BuildMoreMenu")!;
                    SetField(widget, "_contextMenu", menu);
                    var cursors = kinds.Select(kind => WidgetModeMenu(menu, language, kind).DropDownItems.OfType<Forms.ToolStripMenuItem>()
                        .Single(item => item.Text == LocalizationService.Translate(language, "Capture cursor"))).ToArray();
                    Assert(cursors.Select(item => item.Image is not null).SequenceEqual(Flags(service.Settings)),
                        "Image, MP4 and GIF menus read their own saved cursor preference", false);
                    var copy = CopyFlags(service.Settings);
                    var outputs = OtherDestinations(service.Settings);
                    for (int index = 0; index < 3; index++)
                    {
                        var expected = Flags(service.Settings); expected[index] = !expected[index];
                        cursors[index].PerformClick(); Pump();
                        Assert(Flags(service.Settings).SequenceEqual(expected) && Controls().SequenceEqual(expected)
                            && cursors.Select(item => item.Image is not null).SequenceEqual(expected),
                            "a widget cursor check changes only its format and synchronizes Settings and open menus", false);
                        Assert(CopyFlags(service.Settings).SequenceEqual(copy) && OtherDestinations(service.Settings).Equals(outputs),
                            "cursor checks preserve all output and clipboard preferences", false);
                        expected[index] = !expected[index];
                        Element<CheckBox>(config, boxes[index]).IsChecked = expected[index]; Pump();
                        Assert(Flags(service.Settings).SequenceEqual(expected)
                            && cursors.Select(item => item.Image is not null).SequenceEqual(expected),
                            "Settings cursor controls update the open widget menus without changing other formats", false);
                    }
                    configService.FlushPendingWrites(); service.FlushPendingWrites();
                    using var reload = new SettingsService(Path.Combine(temp, "widget-cursors-" + language + ".json")); reload.Load();
                    Assert(Flags(reload.Settings).SequenceEqual(Flags(service.Settings)), "independent cursor preferences survive reload", false);
                    SetField(widget, "_contextMenu", null!);
                }
                Assert(System.Windows.Automation.AutomationProperties.GetName(Element<Button>(widget, "PinToolButton"))
                    == LocalizationService.Translate(language, "Pin to screen"), "the widget Pin action has a localized accessible name: " + language);
                Element<Button>(widget, "PinToolButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var elapsed = Stopwatch.StartNew();
                while (ZoomAppField(app, "_pinChooser") is not PinSourceWindow { IsVisible: true } && elapsed.ElapsedMilliseconds < 3000)
                {
                    Pump(); Thread.Sleep(10);
                }
                Assert(ZoomAppField(app, "_pinChooser") is PinSourceWindow { IsVisible: true },
                    "the widget Pin action opens the integrated source chooser: " + language);
            }
            finally
            {
                SetField(widget, "_contextMenu", null!);
                CallZoomApp(app, "ClosePinnedImages");
                SetZoomAppField(app, "_widgetWindow", null!);
                widget.Close(); config.Close(); Pump();
            }
        }
    }
}
