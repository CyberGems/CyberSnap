using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI;

internal static partial class Program
{
    private static void CheckWidgetToolTips(SettingsService settings)
    {
        var originalPreferences = WidgetPreferences.FromSettings(settings.Settings);
        string originalLanguage = settings.Settings.InterfaceLanguage;
        var popupType = typeof(ToolTip).Assembly.GetType("System.Windows.Controls.PopupControlService")!;
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var service = popupType.GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var show = popupType.GetMethod("ShowToolTip", instanceFlags)!;
        var dismiss = popupType.GetMethod("DismissToolTips", instanceFlags)!;
        var current = popupType.GetProperty("CurrentToolTip", instanceFlags)!;
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
                        .Where(button => button.ToolTip is string).ToArray();
                    Assert(owners.Length == 12, "all nine tools, capture and header buttons are covered", false);
                    foreach (var owner in owners)
                    {
                        // Exercise WPF's real automatic tooltip for the existing string content.
                        show.Invoke(service, new object[] { owner, false }); Pump();
                        var tip = (ToolTip)current.GetValue(service)!;
                        var widgetHwnd = new WindowInteropHelper(widget).Handle;
                        var tipHwnd = ((HwndSource)PresentationSource.FromVisual(tip)).Handle;
                        Assert(tip.IsOpen && tipHwnd != IntPtr.Zero, "WPF opens a native widget tooltip", false);
                        Assert(Equals(tip.Content, owner.ToolTip), "the tooltip preserves its existing localized text", false);
                        Assert(IsWidgetToolTipAbove(tipHwnd, widgetHwnd), "the tooltip initially appears above its widget", false);
                        for (int tick = 0; tick < 3; tick++)
                        {
                            // Same path used by the five-second topmost watchdog and settings sync.
                            widget.AssertWidgetTopmost(); Pump();
                            Assert(tip.IsOpen && IsWidgetToolTipAbove(tipHwnd, widgetHwnd),
                                $"the tooltip remains above the widget after topmost reassertion: {owner.ToolTip}, {language}, topmost={topmost}", false);
                        }
                        settings.Settings.WidgetAlwaysOnTop = !topmost;
                        widget.SyncTopmostFromSettings(); Pump();
                        Assert(tip.IsOpen && IsWidgetToolTipAbove(tipHwnd, widgetHwnd),
                            "changing always-on-top keeps the open tooltip readable", false);
                        settings.Settings.WidgetAlwaysOnTop = topmost;
                        widget.SyncTopmostFromSettings(); Pump();
                        Assert(tip.IsOpen && IsWidgetToolTipAbove(tipHwnd, widgetHwnd),
                            "restoring always-on-top keeps the open tooltip readable", false);
                        dismiss.Invoke(service, null); Pump();
                        Assert(!tip.IsOpen, "the tooltip closes normally after reading", false);
                    }
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
            dismiss.Invoke(service, null);
            originalPreferences.ApplyTo(settings.Settings);
            settings.Settings.InterfaceLanguage = originalLanguage;
        }
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
