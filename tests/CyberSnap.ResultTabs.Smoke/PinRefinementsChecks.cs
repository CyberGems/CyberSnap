using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using CyberSnap.Capture;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;
using Point = System.Drawing.Point;

internal static partial class Program
{
    private static void CheckPinMenuPlacement(string? renders)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (var screen in Forms.Screen.AllScreens)
            {
                LocalizationService.ApplyCurrentCulture(language);
                var monitor = MonitorFromPoint(new Point(screen.Bounds.X + screen.Bounds.Width / 2,
                    screen.Bounds.Y + screen.Bounds.Height / 2), 2);
                var info = new TrayMonitorInfo { Size = Marshal.SizeOf<TrayMonitorInfo>() };
                Assert(GetMonitorInfo(monitor, ref info),
                    "the monitor placement fixture resolves native physical working bounds");
                var work = info.Work.Rectangle;
                using var pin = new PinImageForm(new Bitmap(320, 200),
                    new Point(work.Left + work.Width / 2, work.Top + work.Height / 2), new AppSettings());
                pin.Show(); Forms.Application.DoEvents();
                foreach (bool right in new[] { false, true })
                foreach (bool bottom in new[] { false, true })
                {
                    pin.Location = new Point(right ? work.Right - pin.Width : work.Left,
                        bottom ? work.Bottom - pin.Height : work.Top);
                    Forms.Application.DoEvents();
                    var anchor = new Point(right ? pin.ClientSize.Width - 2 : 2, bottom ? pin.ClientSize.Height - 2 : 2);
                    Call(pin, "OpenMenu", anchor);
                    var menu = ((HashSet<Forms.ContextMenuStrip>)Field(pin, "_openMenus")).Single();
                    // Read the final synchronous Show/Opened placement before
                    // pumping unrelated desktop focus changes that can close it.
                    Assert(GetWindowRect(menu.Handle, out var rootRect) && work.Contains(rootRect.Rectangle),
                        $"the real root menu stays on its originating monitor at every edge: {language}, {screen.DeviceName}, right={right}, bottom={bottom}, bounds={menu.Bounds}, work={work}");
                    var opacity = menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.HasDropDownItems);
                    opacity.ShowDropDown();
                    Assert(GetWindowRect(opacity.DropDown.Handle, out var childRect) && work.Contains(childRect.Rectangle),
                        "the opacity submenu also stays on the same monitor, including mixed DPI boundaries");
                    if (renders is not null && right && bottom)
                    {
                        using var snapshot = new Bitmap(menu.Width, menu.Height);
                        menu.DrawToBitmap(snapshot, new Rectangle(Point.Empty, snapshot.Size));
                        snapshot.Save(Path.Combine(renders, $"pin-edge-menu-{language}-{GetDpiForWindow(menu.Handle)}.png"));
                    }
                    menu.Close(); Forms.Application.DoEvents(); Pump();
                }
            }
        }
        finally { LocalizationService.ApplyCurrentCulture(originalLanguage); }
    }

    private static void CheckPinSettingsNavigation(CyberSnap.App app, SettingsService settings, string? renders)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        string originalSetting = settings.Settings.InterfaceLanguage;
        try
        {
            foreach (string language in new[] { "en", "es" })
            {
                settings.Settings.InterfaceLanguage = language;
                LocalizationService.ApplyCurrentCulture(language);
                using var history = new HistoryService();
                using var search = new ImageSearchIndexService();
                // Exercise the production creation path, rather than calling a tab selector directly.
                CallZoomApp(app, "ShowSettingsWindow", history, search, "pin"); Pump();
                var window = (SettingsWindow)ZoomAppField(app, "_settingsWindow")!;
                try
                {
                    void AssertPinSection(string context)
                    {
                        Assert(Element<RadioButton>(window, "ToolsTab").IsChecked == true
                            && Element<RadioButton>(window, "ToolPinTab").IsChecked == true
                            && Element<StackPanel>(window, "PinToolPanel").Visibility == Visibility.Visible,
                            "Pin settings opens Tools / Pin: " + context + ", " + language);
                    }
                    AssertPinSection("new Settings window");
                    Call(window, "SelectSettingsTab", "general");
                    Element<TextBox>(window, "SettingsSearchBox").Text = "zoom"; Pump();
                    // Open the command on an actual Pin created by App to cover its event wiring.
                    app.PinImage(new Bitmap(320, 200));
                    var pins = (List<PinImageForm>)ZoomAppField(app, "_pinnedImages")!;
                    var pin = pins.Last();
                    try
                    {
                        var menu = OpenPinContextMenu(pin);
                        var options = menu.Items.OfType<Forms.ToolStripMenuItem>()
                            .Single(item => item.Text == LocalizationService.Translate("Pin settings"));
                        Assert(options.Text == (language == "es" ? "Ajustes de Fijar…" : "Pin settings…"),
                            "the settings command uses the requested localized label");
                        options.PerformClick(); Forms.Application.DoEvents(); Pump();
                        Assert(ReferenceEquals(window, ZoomAppField(app, "_settingsWindow")),
                            "Pin reuses an existing Settings window");
                        AssertPinSection("existing Settings window with an active search");
                        Assert(string.IsNullOrEmpty(Element<TextBox>(window, "SettingsSearchBox").Text),
                            "direct navigation clears search so Pin preferences are visible");
                        var bounds = pin.Bounds;
                        var handle = pin.Handle;
                        var shadowToggle = Element<CheckBox>(window, "PinShowShadowCheck");
                        var borderToggle = Element<CheckBox>(window, "PinShowBorderCheck");
                        Assert(shadowToggle.IsChecked == true && borderToggle.IsChecked == true,
                            "both localized Pin appearance toggles load enabled by default");
                        shadowToggle.IsChecked = false; borderToggle.IsChecked = false; Pump();
                        Assert(!(bool)Field(pin, "_showShadow") && !(bool)Field(pin, "_showBorder")
                            && pin.Handle == handle && pin.Bounds == bounds,
                            "changing appearance updates an existing Pin without recreating or resizing it");
                        shadowToggle.IsChecked = true; borderToggle.IsChecked = true; Pump();
                        Assert((bool)Field(pin, "_showShadow") && (bool)Field(pin, "_showBorder"),
                            "both appearance settings can be restored immediately on an existing Pin");
                        Render(window, renders, "pin-settings-route-" + language + ".png");
                    }
                    finally { pin.Close(); }
                }
                finally { window.Close(); Pump(); }
            }
        }
        finally
        {
            settings.Settings.InterfaceLanguage = originalSetting;
            LocalizationService.ApplyCurrentCulture(originalLanguage);
        }
    }
}
