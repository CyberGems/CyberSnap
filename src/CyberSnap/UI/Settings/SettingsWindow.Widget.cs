using CyberSnap.Helpers;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    private void OnWidgetPreferencesChanged(WidgetPreferences preferences)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnWidgetPreferencesChanged(preferences));
            return;
        }
        preferences.ApplyTo(_settingsService.Settings);
        bool previousSuppression = _suppressGeneralPreferenceChange;
        _suppressGeneralPreferenceChange = true;
        try
        {
            WidgetAlwaysOnTopCheck.IsChecked = preferences.AlwaysOnTop;
            WidgetDockEdgeCombo.SelectedIndex = (int)preferences.DockEdge;
            SelectWidgetHoverDelay(preferences.HoverDelayMs);
            PopulateWidgetMonitors();
        }
        finally { _suppressGeneralPreferenceChange = previousSuppression; }
    }
}
