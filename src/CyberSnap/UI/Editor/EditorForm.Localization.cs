using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI.Editor;

public sealed partial class EditorForm
{
    public void RefreshLocalization()
    {
        if (InvokeRequired)
        {
            Invoke(RefreshLocalization);
            return;
        }

        if (IsDisposed)
            return;

        var lang = SettingsService.LoadStatic()?.InterfaceLanguage ?? "en";
        LocalizationService.ApplyCurrentCulture(lang);

        Text = WindowTitles.Taskbar(WindowTitles.Editor, lang);

        foreach (var (tool, keys) in _toolButtonLabels)
        {
            if (!_toolButtons.TryGetValue(tool, out var button))
                continue;
            button.Text = LocalizationService.Translate(keys.displayKey ?? keys.labelKey);
        }

        foreach (var (button, labelKey) in _localizedCommandButtons)
            button.Text = LocalizationService.Translate(labelKey);

        if (_toggleFrameSwitch is not null)
            _toggleFrameSwitch.LabelText = LocalizationService.Translate("Border");

        if (_closeButton is not null)
            _closeButton.AccessibleName = LocalizationService.Translate("Close");
        if (_minimizeButton is not null)
            _minimizeButton.AccessibleName = LocalizationService.Translate("Minimize");
        if (_menuButton is not null)
            _menuButton.AccessibleName = LocalizationService.Translate("Menu");
        UpdateWindowStateButton();

        _brandPanel?.Invalidate();
        UpdateCaptureCaption();
        UpdateLiveStatusText();

        _burgerMenu?.Dispose();
        _burgerMenu = null;
        _canvasMenu?.Dispose();
        _canvasMenu = null;
        _imageMenu?.Dispose();
        _imageMenu = null;
    }
}
