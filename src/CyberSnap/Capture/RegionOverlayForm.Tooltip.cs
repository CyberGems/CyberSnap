using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    private void ShowToolbarTooltip()
    {
        if (_toolbarContextMenu != null && _toolbarContextMenu.Visible)
        {
            HideToolbarTooltip();
            return;
        }

        if (_isMouseDownOnCaptureBtn)
        {
            HideToolbarTooltip();
            return;
        }

        if (_hoveredAltCaptureBtn && _altCapturePopupOpen)
        {
            if (_tooltipButton == 999)
                return;

            _tooltipButton = 999;
            _toolbarToolTip ??= new WindowsToolTip();

            var settings = Services.SettingsService.LoadStatic();
            var defaultMode = settings?.DefaultCaptureMode ?? CaptureMode.Rectangle;
            var altToolId = (defaultMode == CaptureMode.Center) ? "rect" : "center";
            var altTool = ToolDef.AllTools.FirstOrDefault(t => t.Id == altToolId);
            if (altTool != null)
            {
                var label = LocalizationService.Translate(altTool.Label);
                var hotkey = settings?.GetToolHotkey(altTool.Id) ?? (0u, 0u);
                if (hotkey.key != 0)
                    label += $"  ({HotkeyFormatter.Format(hotkey.mod, hotkey.key)})";

                var altAnchorScreen = new Rectangle(
                    _virtualBounds.X + _altCaptureButtonRect.X,
                    _virtualBounds.Y + _altCaptureButtonRect.Y,
                    _altCaptureButtonRect.Width,
                    _altCaptureButtonRect.Height);
                _toolbarToolTip.ShowNear(this, label, altAnchorScreen, IsBottomDock);
                _tooltipVisible = true;
                _tooltipShowTime = DateTime.UtcNow;
            }
            return;
        }

        if (!IsToolbarInteractive() || _hoveredButton < 0 || _hoveredButton >= _toolbarLabels.Length)
        {
            HideToolbarTooltip();
            return;
        }

        if (_colorPickerOpen && _hoveredButton == ColorButtonIndex)
        {
            HideToolbarTooltip();
            return;
        }

        if (_tooltipButton == _hoveredButton)
            return;

        _tooltipButton = _hoveredButton;
        _toolbarToolTip ??= new WindowsToolTip();

        var text = GetToolbarTooltipText(_hoveredButton);
        if (string.IsNullOrWhiteSpace(text))
        {
            HideToolbarTooltip();
            return;
        }

        var anchor = _toolbarButtons[_hoveredButton];
        var anchorScreen = new Rectangle(
            _virtualBounds.X + anchor.X,
            _virtualBounds.Y + anchor.Y,
            anchor.Width,
            anchor.Height);
        _toolbarToolTip.ShowNear(this, text, anchorScreen, IsBottomDock);
        _tooltipVisible = true;
        _tooltipShowTime = DateTime.UtcNow;
    }

    private string? GetToolbarTooltipText(int button)
    {
        if (button < 0 || button >= _toolbarLabels.Length)
            return null;

        var text = _toolbarLabels[button];
        var settings = Services.SettingsService.LoadStatic();
        var isSpanish = settings != null && string.Equals(settings.InterfaceLanguage, "es", StringComparison.OrdinalIgnoreCase);

        if (button < _mainBarTools.Length)
        {
            var tool = _mainBarTools[button];
            var hotkey = settings?.GetToolHotkey(tool.Id) ?? (0u, 0u);
            if (hotkey.key != 0)
                text += $"  ({HotkeyFormatter.Format(hotkey.mod, hotkey.key)})";

            if (button == _mergedCaptureButtonIndex)
            {
                var defaultMode = settings?.DefaultCaptureMode ?? CaptureMode.Rectangle;
                string suffix;
                if (defaultMode == CaptureMode.Center)
                {
                    suffix = isSpanish
                        ? "\nMantén presionado para ver la herramienta Selección de área"
                        : "\nHold to show Area Capture tool";
                }
                else
                {
                    suffix = isSpanish
                        ? "\nMantén presionado para ver la herramienta Desde el centro"
                        : "\nHold to show From Center tool";
                }
                text += suffix;
            }
        }
        else if (button >= CloseButtonIndex + 1 && button < BtnCount)
        {
            int flyoutIdx = button - (CloseButtonIndex + 1);
            if (flyoutIdx >= 0 && flyoutIdx < _flyoutTools.Length)
            {
                var tool = _flyoutTools[flyoutIdx];
                var hotkey = settings?.GetToolHotkey(tool.Id) ?? (0u, 0u);
                if (hotkey.key != 0)
                    text += $"  ({HotkeyFormatter.Format(hotkey.mod, hotkey.key)})";
            }
        }

        // Append right-click to hide hint for hideable tools
        bool isHideable = (button < _mainBarTools.Length) || (button >= CloseButtonIndex + 1 && button < BtnCount);
        if (isHideable)
        {
            text += isSpanish ? "\nClick derecho para ocultar" : "\nRight-click to hide";
        }

        return text;
    }

    private void HideToolbarTooltip()
    {
        _tooltipButton = -1;
        _tooltipVisible = false;
        _tooltipDismissed = true;
        _tooltipShowTime = DateTime.MinValue;
        try { _toolbarToolTip?.Hide(); } catch { }
    }

    private bool IsToolbarInteractive()
        => !_isSelecting && _toolbarForm is { IsDisposed: false, Visible: true };
}
