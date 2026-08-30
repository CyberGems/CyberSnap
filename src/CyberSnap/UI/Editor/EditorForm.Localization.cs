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
        {
            button.Text = LocalizationService.Translate(labelKey);
            button.RecalculateWidth();
            button.Invalidate();
        }

        if (_toggleFrameSwitch is not null)
            _toggleFrameSwitch.LabelText = LocalizationService.Translate("Border");

        if (_closeButton is not null)
            _closeButton.AccessibleName = LocalizationService.Translate("Close");
        if (_minimizeButton is not null)
            _minimizeButton.AccessibleName = LocalizationService.Translate("Minimize");
        if (_donateButton is not null)
            _donateButton.AccessibleName = LocalizationService.Translate("Donate");
        if (_menuButton is not null)
            _menuButton.AccessibleName = LocalizationService.Translate("Menu");
        UpdateWindowStateButton();

        if (_brandPanel is not null)
        {
            _brandPanel.Width = CalculateBrandWidth();
            _brandPanel.Invalidate();
        }
        UpdateCaptureCaption();
        UpdateLiveStatusText();

        if (_menuButton is not null)
        {
            _menuButton.HoverOverride = false;
            _menuButton.PressedOverride = false;
        }
        _burgerMenu?.Dispose();
        _burgerMenu = null;
        _canvasMenu?.Dispose();
        _canvasMenu = null;
        _imageMenu?.Dispose();
        _imageMenu = null;
    }
}
