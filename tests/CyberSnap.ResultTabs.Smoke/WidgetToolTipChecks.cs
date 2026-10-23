using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.Models;
using System.Windows.Input;
using CaptureMode = CyberSnap.Models.CaptureMode;

internal static partial class Program
{
    private static void CheckWidgetToolTips(SettingsService settings, string? renders = null)
    {
        var originalPreferences = WidgetPreferences.FromSettings(settings.Settings);
        string originalLanguage = settings.Settings.InterfaceLanguage;
        var originalMode = settings.Settings.DefaultCaptureMode;
        var popupType = typeof(ToolTip).Assembly.GetType("System.Windows.Controls.PopupControlService")!;
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var service = popupType.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var show = popupType.GetMethod("ShowToolTip", instanceFlags)!;
        var dismiss = popupType.GetMethod("DismissToolTips", instanceFlags)!;
        var current = popupType.GetProperty("CurrentToolTip", instanceFlags)!;
        InputManager.Current.PreProcessInput += IgnoreToolTipFixtureMouseInput;
        try
        {
            foreach (string language in new[] { "en", "es" })
            foreach (bool topmost in new[] { true, false })
            {
                settings.Settings.InterfaceLanguage = language;
                settings.Settings.WidgetAlwaysOnTop = topmost;
                var widget = new CaptureWidgetWindow(settings) { IsHitTestVisible = false };
                try
                {
                    widget.Show();
                    GetField<DispatcherTimer>(widget, "_activeMonitorTimer").Stop();
                    SetField(widget, "_contextMenuOpen", true);
                    SetField(widget, "_isExpanded", true);
                    widget.PositionWindow(); Pump();
                    var owners = VisualDescendants(widget).OfType<Button>()
                        .Where(button => button.ToolTip is not null).ToArray();
                    Assert(owners.Length == 12, "all nine tools, capture and header buttons are covered", false);
                    foreach (var owner in owners)
                    {
                        // Exercise WPF's real popup service, including the shared two-line template.
                        show.Invoke(service, new object[] { owner, false }); Pump();
                        var tip = (ToolTip)current.GetValue(service)!;
                        var widgetHwnd = new WindowInteropHelper(widget).Handle;
                        var tipHwnd = ((HwndSource)PresentationSource.FromVisual(tip)).Handle;
                        Assert(tip.IsOpen && tipHwnd != IntPtr.Zero, "WPF opens a native widget tooltip", false);
                        Assert(Equals(tip.Content, (owner.ToolTip as ToolTip)?.Content ?? owner.ToolTip),
                            "the tooltip preserves its localized content", false);
                        if (tip.Content is ActionToolTipContent)
                        {
                            CheckActionToolTip(tip);
                            Assert(ToolTipService.GetShowDuration(owner) == 10000,
                                "two-line help allows ten seconds for reading", false);
                            if (owner.Name == "PinToolButton")
                            {
                                CheckPinToolTipText(tip, language);
                                if (topmost) Render(tip, renders, "widget-pin-tooltip-" + language + ".png");
                            }
                        }
                        else
                            Assert(Equals(tip.Content, language == "en" ? "About CyberSnap" : "Abrir Acerca de CyberSnap"),
                                "About retains the agreed single-line tooltip", false);
                        Assert(IsWidgetToolTipAbove(tipHwnd, widgetHwnd), "the tooltip initially appears above its widget", false);
                        for (int tick = 0; tick < 3; tick++)
                        {
                            // Same path used by the five-second topmost watchdog and settings sync.
                            widget.AssertWidgetTopmost(); Pump();
                            Assert(tip.IsOpen, $"the tooltip stays open during topmost reassertion: {owner.Name}, {language}", false);
                            Assert(IsWidgetToolTipAbove(tipHwnd, widgetHwnd),
                                $"the tooltip remains above the widget after topmost reassertion: {owner.Name}, {language}, tracked={ReferenceEquals(Field(widget, "_openToolTip"), tip)}", false);
                        }
                        settings.Settings.WidgetAlwaysOnTop = !topmost;
                        widget.SyncTopmostFromSettings(); Pump();
                        Assert(tip.IsOpen && IsWidgetToolTipAbove(tipHwnd, widgetHwnd),
                            "changing always-on-top keeps the open tooltip readable", false);
                        settings.Settings.WidgetAlwaysOnTop = topmost;
                        widget.SyncTopmostFromSettings(); Pump();
                        Assert(tip.IsOpen && IsWidgetToolTipAbove(tipHwnd, widgetHwnd),
                            "restoring always-on-top keeps the open tooltip readable", false);
                        CloseFixtureToolTip(tip, () => dismiss.Invoke(service, null));
                        Assert(!tip.IsOpen, "the tooltip closes normally after reading", false);
                    }
                    settings.Settings.DefaultCaptureMode = CaptureMode.Center;
                    widget.RefreshLayout();
                    var center = (ActionToolTipContent)((ToolTip)Element<Button>(widget, "CaptureButton").ToolTip).Content;
                    Assert(center.Title == (language == "en" ? "Area capture (from center)" : "Captura de área (desde el centro)"),
                        "capture help follows the configured center mode", false);
                    settings.Settings.DefaultCaptureMode = originalMode;
                    settings.Settings.InterfaceLanguage = language == "en" ? "es" : "en";
                    widget.RefreshLocalization();
                    CheckPinToolTipText((ToolTip)Element<Button>(widget, "PinToolButton").ToolTip, settings.Settings.InterfaceLanguage);
                }
                finally
                {
                    dismiss.Invoke(service, null);
                    widget.Close(); Pump();
                }
            }
        }
        finally
        {
            InputManager.Current.PreProcessInput -= IgnoreToolTipFixtureMouseInput;
            dismiss.Invoke(service, null);
            originalPreferences.ApplyTo(settings.Settings);
            settings.Settings.InterfaceLanguage = originalLanguage;
            settings.Settings.DefaultCaptureMode = originalMode;
        }
    }

    private static void CheckActionToolTip(ToolTip tip)
    {
        var content = (ActionToolTipContent)tip.Content;
        Assert(System.Windows.Automation.AutomationProperties.GetName(tip) == content.Title &&
            System.Windows.Automation.AutomationProperties.GetHelpText(tip) == content.Description,
            "assistive technology receives the localized title and explanation", false);
        var lines = VisualDescendants(tip).OfType<TextBlock>()
            .Where(text => text.Text == content.Title || text.Text == content.Description).ToArray();
        Assert(lines.Length == 2 && lines[0].Text == content.Title && lines[1].Text == content.Description,
            "the popup renders the localized title and description as separate text blocks", false);
        Assert(lines[0].FontWeight == FontWeights.SemiBold && lines[1].TextWrapping == TextWrapping.Wrap,
            "the title is emphasized and the description wraps without truncation", false);
        Assert(lines[1].TranslatePoint(new Point(), tip).Y > lines[0].TranslatePoint(new Point(), tip).Y,
            "the explanation appears below the title", false);
        Assert(tip.ActualWidth <= 343 && tip.ActualWidth > 20 && tip.ActualHeight > 30,
            "the tooltip stays compact with room for both lines", false);
        var hwnd = ((HwndSource)PresentationSource.FromVisual(tip)).Handle;
        Assert(GetWindowRect(hwnd, out var bounds) && System.Windows.Forms.Screen.FromHandle(hwnd).WorkingArea.Contains(bounds.Rectangle),
            "the complete tooltip stays inside the monitor's work area", false);
    }

    private static void CheckPinToolTipText(ToolTip tip, string language)
    {
        var content = (ActionToolTipContent)tip.Content;
        Assert(content.Title == (language == "en" ? "Pin to screen" : "Fijar en pantalla") &&
            content.Description == (language == "en"
                ? "Capture or choose an image to keep on top of other windows."
                : "Captura o elige una imagen para mantenerla por encima de otras ventanas."),
            "Pin uses the approved copy, including the Spanish grammar correction", false);
    }

    private static void CheckTrayToolTips(string? renders)
    {
        string originalLanguage = LocalizationService.CurrentLanguageSetting;
        var themeField = typeof(Theme).GetField("_forcedMode", BindingFlags.Static | BindingFlags.NonPublic)!;
        var originalTheme = (AppThemeMode)themeField.GetValue(null)!;
        var popupType = typeof(ToolTip).Assembly.GetType("System.Windows.Controls.PopupControlService")!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var service = popupType.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var show = popupType.GetMethod("ShowToolTip", flags)!;
        var dismiss = popupType.GetMethod("DismissToolTips", flags)!;
        InputManager.Current.PreProcessInput += IgnoreToolTipFixtureMouseInput;
        try
        {
            foreach (var theme in new[] { AppThemeMode.Light, AppThemeMode.Dark })
            foreach (string language in new[] { "en", "es" })
            {
                Theme.SetMode(theme);
                Theme.ApplyTo(Application.Current.Resources);
                // Match the active UI language even if a window explicitly changed
                // it after the global language preference was last applied.
                LocalizationService.ApplyCurrentCulture(language == "en" ? "es" : "en");
                LocalizationService.ApplyTo(new Border(), language);
                var screen = WidgetScreens()[0];
                var menu = new TrayContextMenuWindow(null!, new System.Drawing.Point(screen.WorkingArea.Right - 2, screen.WorkingArea.Bottom - 2))
                    { IsHitTestVisible = false };
                menu.Deactivated -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), menu,
                    typeof(TrayContextMenuWindow).GetMethod("Window_Deactivated", flags)!);
                try
                {
                    SetField(menu, "_isCompact", false);
                    Call(menu, "ApplyCompactMode", false);
                    SetField(menu, "_showRecording", true);
                    SetField(menu, "_showTools", true);
                    SetField(menu, "_showGallery", true);
                    Call(menu, "ApplySectionVisibility", false);
                    menu.Show(); Pump();
                    var owners = VisualDescendants(menu).OfType<Button>()
                        .Where(button => button.ToolTip is ToolTip { Content: ActionToolTipContent }).ToArray();
                    Assert(owners.Length == 16, "tray capture modes, tools, Gallery, Editor and panel options receive useful two-line help", false);
                    foreach (var owner in owners)
                    {
                        show.Invoke(service, new object[] { owner, false }); Pump();
                        var tip = (ToolTip)owner.ToolTip;
                        Assert(tip.IsOpen, $"the tray opens its standard WPF tooltip: {owner.Name}, visible={owner.IsVisible}, enabled={owner.IsEnabled}", false);
                        CheckActionToolTip(tip);
                        Assert(IsWidgetToolTipAbove(((HwndSource)PresentationSource.FromVisual(tip)).Handle, new WindowInteropHelper(menu).Handle),
                            "the tray tooltip stays above the panel", false);
                        if (owner.Name == "PinBtn")
                        {
                            CheckPinToolTipText(tip, language);
                            Render(tip, renders, "tray-pin-tooltip-" + language + "-" + theme + ".png");
                        }
                        CloseFixtureToolTip(tip, () =>
                        {
                            Call(menu, "DismissActiveTooltip");
                            dismiss.Invoke(service, null);
                        });
                        Assert(!tip.IsOpen, "moving away dismisses the tray tooltip", false);
                    }
                    var about = (ToolTip)Element<Button>(menu, "AboutBtn").ToolTip;
                    Assert(Equals(about.Content, language == "en" ? "About CyberSnap" : "Abrir Acerca de CyberSnap"),
                        "tray About also retains the agreed single-line title", false);
                    Call(menu, "LoadLocalizedLabels", CaptureMode.Center);
                    var center = (ActionToolTipContent)((ToolTip)Element<Button>(menu, "AreaCaptureBtn").ToolTip).Content;
                    Assert(center.Title == (language == "en" ? "Area capture (from center)" : "Captura de área (desde el centro)"),
                        "tray capture help follows the configured center mode", false);
                }
                finally
                {
                    dismiss.Invoke(service, null);
                    Call(menu, "DismissActiveTooltip");
                    menu.Close(); Pump();
                }
            }
        }
        finally
        {
            InputManager.Current.PreProcessInput -= IgnoreToolTipFixtureMouseInput;
            Theme.SetMode(originalTheme);
            Theme.ApplyTo(Application.Current.Resources);
            LocalizationService.ApplyCurrentCulture(originalLanguage);
        }
    }

    private static void IgnoreToolTipFixtureMouseInput(object sender, PreProcessInputEventArgs e)
    {
        // These checks force WPF popups directly. Physical pointer movement on the
        // shared desktop must not dismiss them midway through a Z-order assertion.
        if (e.StagingItem.Input is MouseEventArgs || e.StagingItem.Input.Device is MouseDevice) e.Cancel();
    }

    private static void CloseFixtureToolTip(ToolTip tip, Action close)
    {
        bool closed = false;
        RoutedEventHandler handler = (_, _) => closed = true;
        tip.Closed += handler;
        try
        {
            close();
            // IsOpen changes before the native popup finishes closing. Wait for
            // Closed before bypassing hover delays to force the next tooltip.
            WaitForWidgetAnimation(() => closed);
        }
        finally { tip.Closed -= handler; }
    }

    private static bool IsWidgetToolTipAbove(IntPtr tip, IntPtr widget)
    {
        for (var above = WidgetToolTipPreviousWindow(widget, 3 /* GW_HWNDPREV */); above != IntPtr.Zero;
             above = WidgetToolTipPreviousWindow(above, 3))
            if (above == tip) return true;
        return false;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern IntPtr WidgetToolTipPreviousWindow(IntPtr hwnd, uint command);
}
