using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Forms = System.Windows.Forms;

namespace CyberSnap.UI;

public partial class CaptureWidgetWindow
{
    private readonly Dictionary<Forms.ToolStripMenuItem, (Func<AppSettings, bool> Get, bool Toggle)> _widgetMenuOptions = new();

    private void SaveWidgetPreferences(Action<AppSettings> change)
    {
        _settingsService.RefreshWidgetPreferences();
        _settingsService.RefreshResultDeliveryPreferences();
        change(_settings);
        _settingsService.Save();
        SettingsService.PublishWidgetPreferences(_settings);
    }

    private Forms.ToolStripMenuItem BuildWidgetChoice(string label, Func<AppSettings, bool> selected, Action<AppSettings> change)
    {
        var item = WindowsMenuRenderer.Item(label, active: selected(_settings));
        BindWidgetOption(item, selected, toggle: false);
        item.Click += (_, _) => SaveWidgetPreferences(change);
        return item;
    }

    private Forms.ToolStripMenuItem BuildWidgetAlwaysOnTopItem()
    {
        var item = WindowsMenuRenderer.Item("Always on top");
        BindWidgetOption(item, settings => settings.WidgetAlwaysOnTop, toggle: true);
        item.Click += (_, _) => SaveWidgetPreferences(settings => settings.WidgetAlwaysOnTop = !settings.WidgetAlwaysOnTop);
        return item;
    }

    private void BindWidgetOption(Forms.ToolStripMenuItem item, Func<AppSettings, bool> get, bool toggle)
    {
        _widgetMenuOptions.Add(item, (get, toggle));
        item.Disposed += (_, _) => _widgetMenuOptions.Remove(item);
        RefreshWidgetOption(item, get(_settings), toggle);
    }

    private static void RefreshWidgetOption(Forms.ToolStripMenuItem item, bool selected, bool toggle)
    {
        if (!toggle) item.Tag = selected;
        var oldImage = item.Image;
        item.Image = selected ? FluentIcons.RenderBitmap("check",
            System.Drawing.Color.FromArgb(255, UiChrome.SurfaceTextPrimary.R,
                UiChrome.SurfaceTextPrimary.G, UiChrome.SurfaceTextPrimary.B), 20, true) : null;
        item.ImageScaling = Forms.ToolStripItemImageScaling.None;
        oldImage?.Dispose();
        item.Invalidate();
    }

    private void OnWidgetPreferencesChanged(WidgetPreferences preferences)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnWidgetPreferencesChanged(preferences));
            return;
        }
        preferences.ApplyTo(_settings);
        RememberConfiguredMonitor(PopupWindowHelper.GetSortedScreens());
        SyncTopmostFromSettings();
        _hoverDelayTimer.Interval = TimeSpan.FromMilliseconds(preferences.HoverDelayMs);
        if (IsLoaded) RefreshLayout();
        foreach (var (item, binding) in _widgetMenuOptions.ToArray())
            if (!item.IsDisposed)
                RefreshWidgetOption(item, binding.Get(_settings), binding.Toggle);
    }
}
