using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckWidgetPreferences(string temp, string? renderDirectory)
    {
        foreach (string language in new[] { "en", "es" })
        {
            string path = Path.Combine(temp, "widget-preferences-" + language + ".json");
            using var widgetSettings = new SettingsService(path);
            widgetSettings.Settings.InterfaceLanguage = language;
            widgetSettings.Settings.DisableAnimations = true;
            widgetSettings.Settings.WidgetDockPositionOffset = 0.23;
            AutoCopyPreferences.SetKindEnabled(widgetSettings.Settings, AutoCopyKind.Image, false);
            AutoCopyPreferences.SetKindEnabled(widgetSettings.Settings, AutoCopyKind.Video, true);
            AutoCopyPreferences.SetKindEnabled(widgetSettings.Settings, AutoCopyKind.Ocr, true);
            widgetSettings.Save();
            widgetSettings.FlushPendingWrites();
            using var configSettings = new SettingsService(path);
            using var history = new HistoryService();
            using var search = new ImageSearchIndexService();
            Console.WriteLine("Checking widget placement/activation synchronization in " + language + ".");
            var config = new SettingsWindow(configSettings, history, search);
            var widget = new CaptureWidgetWindow(widgetSettings) { IsHitTestVisible = false };
            int publications = 0;
            void OnPublished(WidgetPreferences _) => publications++;
            SettingsService.WidgetPreferencesChanged += OnPublished;
            WidgetPreferences expected = WidgetPreferences.FromSettings(widgetSettings.Settings);
            try
            {
                config.Show();
                Call(config, "SelectSettingsTab", "widget");
                widget.Show();
                Pump();
                using var more = (Forms.ContextMenuStrip)Call(widget, "BuildMoreMenu")!;
                var options = (Forms.ToolStripMenuItem)more.Items[0];
                Forms.ToolStripMenuItem Find(string key) => options.DropDownItems.OfType<Forms.ToolStripMenuItem>()
                    .Single(item => item.Text == LocalizationService.Translate(language, key));
                var position = Find("Position");
                var delay = Find("Activation delay");
                var topmost = Find("Always on top");
                var screen = options.DropDownItems.OfType<Forms.ToolStripMenuItem>()
                    .SingleOrDefault(item => item.Text == LocalizationService.Translate(language, "Screen"));
                int screenCount = Forms.Screen.AllScreens.Length;
                string[] edgeLabels = ["Top", "Bottom", "Left", "Right"];
                int[] delays = [0, 100, 250, 500, 1000];

                foreach (var edge in Enum.GetValues<CaptureDockSide>())
                {
                    var value = edge;
                    Step(expected with { DockEdge = value }, () => position.DropDownItems[(int)value].PerformClick(), "widget edge");
                    var next = (CaptureDockSide)(((int)value + 1) % 4);
                    Step(expected with { DockEdge = next }, () => Element<ComboBox>(config, "WidgetDockEdgeCombo").SelectedIndex = (int)next, "Settings edge");
                }
                for (int i = 0; i < delays.Length; i++)
                {
                    int index = i;
                    Step(expected with { HoverDelayMs = delays[index] }, () => delay.DropDownItems[index].PerformClick(), "widget delay");
                    int next = (i + 1) % delays.Length;
                    Step(expected with { HoverDelayMs = delays[next] }, () => Element<ComboBox>(config, "WidgetHoverDelayCombo").SelectedIndex = next, "Settings delay");
                }
                for (int i = 0; i < 2; i++)
                {
                    Step(expected with { AlwaysOnTop = !expected.AlwaysOnTop }, () => topmost.PerformClick(), "widget topmost");
                    bool selected = !expected.AlwaysOnTop;
                    Step(expected with { AlwaysOnTop = selected }, () => Element<CheckBox>(config, "WidgetAlwaysOnTopCheck").IsChecked = selected, "Settings topmost");
                    selected = !expected.AlwaysOnTop;
                    Step(expected with { AlwaysOnTop = selected }, () => Element<CheckBox>(config, "WidgetAlwaysOnTopCheck").IsChecked = selected, "Settings topmost reverse");
                }
                if (screen is not null)
                {
                    Assert(screen.DropDownItems[0].Text == LocalizationService.Translate(language, "Follow active monitor"),
                        "the widget exposes the same automatic monitor option as Settings", false);
                    foreach (int monitor in Enumerable.Range(-1, screenCount + 1))
                    {
                        int value = monitor;
                        int itemIndex = value == -1 ? 0 : value + 2;
                        Step(expected with { MonitorIndex = value }, () => screen.DropDownItems[itemIndex].PerformClick(), "widget monitor");
                        int next = value == -1 ? 0 : -1;
                        Step(expected with { MonitorIndex = next }, () => Element<ComboBox>(config, "WidgetMonitorCombo").SelectedItem =
                            Element<ComboBox>(config, "WidgetMonitorCombo").Items.Cast<ComboBoxItem>().Single(item => (int)item.Tag == next), "Settings monitor");
                    }
                }
                Step(expected with { PositionOffset = 0.5 }, () => position.DropDownItems[^1].PerformClick(), "widget reset position");
                Step(expected with { PositionOffset = 0.74 }, () => FinishDrag(0.74, captureLost: false), "widget drag completion");
                Step(expected with { PositionOffset = 0.5 }, () => Call(config, "WidgetResetPositionButton_Click", config, new RoutedEventArgs()), "Settings reset position");
                Step(expected with { PositionOffset = 0.78 }, () => FinishDrag(0.78, captureLost: true), "widget drag capture loss");
                Step(expected with { MonitorIndex = -1 }, () =>
                {
                    widgetSettings.Settings.WidgetMonitorIndex = 0;
                    SetField(widget, "_configuredMonitorIndex", 0);
                    SetField(widget, "_configuredMonitorDevice", "removed-test-display");
                    Call(widget, "ClampMonitorIndexToAvailableScreens");
                }, "selected display disappeared while its index remained valid");
                Step(expected with { MonitorIndex = -1 }, () =>
                {
                    widgetSettings.Settings.WidgetMonitorIndex = int.MaxValue;
                    Call(widget, "ClampMonitorIndexToAvailableScreens");
                }, "unavailable monitor fallback");

                // Keep a distinctly non-default combination for the separate-process restart.
                Step(expected with { DockEdge = CaptureDockSide.Right }, () => position.DropDownItems[(int)CaptureDockSide.Right].PerformClick(), "restart edge");
                Step(expected with { HoverDelayMs = 1000 }, () => delay.DropDownItems[4].PerformClick(), "restart delay");
                if (expected.AlwaysOnTop)
                    Step(expected with { AlwaysOnTop = false }, () => topmost.PerformClick(), "restart topmost");
                if (screen is not null)
                    Step(expected with { MonitorIndex = screenCount - 1 }, () => screen.DropDownItems[screenCount + 1].PerformClick(), "restart monitor");
                Render(config, renderDirectory, "widget-preferences-settings-" + language + ".png");
                Console.WriteLine("PASS: widget/Settings placement, monitor, delay, topmost, reset and drag synchronize in both directions in " + language + ".");

                void FinishDrag(double offset, bool captureLost)
                {
                    widgetSettings.Settings.WidgetDockPositionOffset = offset;
                    SetField(widget, "_isDragging", true);
                    if (captureLost) Call(widget, "OnLostMouseCapture", new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount));
                    else Call(widget, "OnMouseLeftButtonUp", new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left));
                }

                void Step(WidgetPreferences next, Action action, string source)
                {
                    var copy = CopyFlags(widgetSettings.Settings);
                    var windows = DeliveryWindowFlags(widgetSettings.Settings);
                    var destinations = OtherDestinations(widgetSettings.Settings);
                    int before = publications;
                    action();
                    Pump();
                    expected = next;
                    string context = language + " " + source;
                    Assert(publications == before + 1, "widget preference edits publish once without feedback loops: " + context, false);
                    Assert(WidgetPreferences.FromSettings(widgetSettings.Settings) == expected
                        && WidgetPreferences.FromSettings(configSettings.Settings) == expected,
                        "widget preference edits preserve the other widget choices: " + context, false);
                    Assert(Element<ComboBox>(config, "WidgetDockEdgeCombo").SelectedIndex == (int)expected.DockEdge
                        && (string)((ComboBoxItem)Element<ComboBox>(config, "WidgetHoverDelayCombo").SelectedItem).Tag == expected.HoverDelayMs.ToString()
                        && Element<CheckBox>(config, "WidgetAlwaysOnTopCheck").IsChecked == expected.AlwaysOnTop,
                        "Settings controls immediately reflect widget choices: " + context, false);
                    if (screen is not null)
                    {
                        Assert((int)((ComboBoxItem)Element<ComboBox>(config, "WidgetMonitorCombo").SelectedItem).Tag == expected.MonitorIndex,
                            "Settings monitor selection stays synchronized: " + context, false);
                        var selectedMonitor = (ComboBoxItem)Element<ComboBox>(config, "WidgetMonitorCombo").SelectedItem;
                        Assert((string)selectedMonitor.Content == screen.DropDownItems[expected.MonitorIndex == -1 ? 0 : expected.MonitorIndex + 2].Text,
                            "Settings and widget monitor labels use the same language: " + context, false);
                    }
                    Assert(widget.Topmost == expected.AlwaysOnTop
                        && GetField<DispatcherTimer>(widget, "_hoverDelayTimer").Interval.TotalMilliseconds == expected.HoverDelayMs,
                        "the live widget applies topmost and the activation timer: " + context, false);
                    var halo = (Thickness)typeof(CaptureWidgetWindow).GetMethod("ShadowHalo", BindingFlags.Static | BindingFlags.NonPublic)!
                        .Invoke(null, [expected.DockEdge])!;
                    var rootScale = Element<FrameworkElement>(widget, "RootGrid").LayoutTransform.Value;
                    double correctionX = rootScale.M11 / UiScale.Current;
                    double correctionY = rootScale.M22 / UiScale.Current;
                    halo = new Thickness(halo.Left * correctionX, halo.Top * correctionY, halo.Right * correctionX, halo.Bottom * correctionY);
                    Assert(Element<FrameworkElement>(widget, "RootGrid").Margin == halo,
                        "the widget layout applies the selected dock edge: " + context, false);
                    if (expected.MonitorIndex >= 0)
                    {
                        var monitors = (Forms.Screen[])typeof(CaptureWidgetWindow).Assembly.GetType("CyberSnap.UI.PopupWindowHelper")!
                            .GetMethod("GetSortedScreens", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, null)!;
                        Assert(Forms.Screen.FromHandle(new WindowInteropHelper(widget).Handle).DeviceName == monitors[expected.MonitorIndex].DeviceName,
                            "the live widget moves to the selected physical monitor: " + context, false);
                    }
                    for (int index = 0; index < edgeLabels.Length; index++)
                        Assert((position.DropDownItems[index].Tag is true) == (index == (int)expected.DockEdge)
                            && (position.DropDownItems[index].Image is not null) == (index == (int)expected.DockEdge),
                            "an existing widget menu reflects external edge changes: " + context, false);
                    for (int index = 0; index < delays.Length; index++)
                        Assert((delay.DropDownItems[index].Tag is true) == (delays[index] == expected.HoverDelayMs)
                            && (delay.DropDownItems[index].Image is not null) == (delays[index] == expected.HoverDelayMs),
                            "an existing widget menu reflects external delay changes: " + context, false);
                    if (screen is not null)
                        foreach (int monitor in Enumerable.Range(-1, screenCount + 1))
                            Assert((screen.DropDownItems[monitor == -1 ? 0 : monitor + 2].Tag is true) == (monitor == expected.MonitorIndex)
                                && (screen.DropDownItems[monitor == -1 ? 0 : monitor + 2].Image is not null) == (monitor == expected.MonitorIndex),
                                "an existing widget menu reflects external monitor changes: " + context, false);
                    Assert((topmost.Image is not null) == expected.AlwaysOnTop, "the existing menu's topmost checkmark updates: " + context, false);
                    foreach (var service in new[] { widgetSettings, configSettings })
                    {
                        Assert(CopyFlags(service.Settings).SequenceEqual(copy) && DeliveryWindowFlags(service.Settings).SequenceEqual(windows)
                            && OtherDestinations(service.Settings).Equals(destinations),
                            "widget placement/activation does not change capture delivery: " + context, false);
                        service.FlushPendingWrites();
                    }
                    using var reloaded = new SettingsService(path);
                    reloaded.Load();
                    Assert(WidgetPreferences.FromSettings(reloaded.Settings) == expected, "widget choices survive saving and reloading: " + context, false);
                }
            }
            finally
            {
                SettingsService.WidgetPreferencesChanged -= OnPublished;
                widget.Close();
                config.Close();
                widgetSettings.FlushPendingWrites();
                configSettings.FlushPendingWrites();
            }
            CheckWidgetRestartProcess(path, expected);
        }
    }

    private static void CheckWidgetRestartProcess(string path, WidgetPreferences expected)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--widget-restart-check");
        start.ArgumentList.Add(path);
        start.ArgumentList.Add(JsonSerializer.Serialize(expected));
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { AwaitCondition(() => process.HasExited, "Widget restart verification timed out", 60000); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        Assert(process.ExitCode == 0, "a separate process restores widget preferences after restart: " + output + error, false);
        Console.Write(output);
    }

    private static int CheckWidgetRestart(string path, string expectedJson)
    {
        try
        {
            var expected = JsonSerializer.Deserialize<WidgetPreferences>(expectedJson);
            using var settings = new SettingsService(path);
            settings.Load();
            Assert(WidgetPreferences.FromSettings(settings.Settings) == expected, "fresh process restores all persisted widget values", false);
            using var configSettings = new SettingsService(path);
            using var history = new HistoryService();
            using var search = new ImageSearchIndexService();
            var config = new SettingsWindow(configSettings, history, search);
            var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
            try
            {
                config.Show();
                widget.Show();
                Pump();
                Assert(WidgetPreferences.FromSettings(configSettings.Settings) == expected && widget.Topmost == expected.AlwaysOnTop,
                    "fresh windows restore the saved values and topmost state", false);
                Assert(Element<ComboBox>(config, "WidgetDockEdgeCombo").SelectedIndex == (int)expected.DockEdge
                    && (string)((ComboBoxItem)Element<ComboBox>(config, "WidgetHoverDelayCombo").SelectedItem).Tag == expected.HoverDelayMs.ToString()
                    && Element<CheckBox>(config, "WidgetAlwaysOnTopCheck").IsChecked == expected.AlwaysOnTop,
                    "fresh Settings controls restore saved widget choices", false);
                if (Element<ComboBox>(config, "WidgetMonitorCombo").IsEnabled)
                    Assert((int)((ComboBoxItem)Element<ComboBox>(config, "WidgetMonitorCombo").SelectedItem).Tag == expected.MonitorIndex,
                        "fresh Settings restores the saved monitor", false);
                Console.WriteLine("PASS: a fresh process reopens Widget and Settings with their persisted preferences.");
            }
            finally { widget.Close(); config.Close(); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Application.Current.Shutdown(); }
    }
}
