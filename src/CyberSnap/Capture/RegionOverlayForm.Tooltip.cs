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

        // Menu activator (▼ chevron) tooltip
        if (_hoveredMenuActivator)
        {
            if (_tooltipButton == 998)
                return;

            _tooltipButton = 998;
            _toolbarToolTip ??= new WindowsToolTip();

            var isSpanish = string.Equals(
                Services.SettingsService.LoadStatic()?.InterfaceLanguage ?? "en",
                "es", StringComparison.OrdinalIgnoreCase);
            var activatorText = isSpanish
                ? "Más opciones\nMostrar/ocultar banners de ayuda"
                : "More options\nToggle help banners";

            var activatorAnchor = new Rectangle(
                _virtualBounds.X + _menuActivatorRect.X,
                _virtualBounds.Y + _menuActivatorRect.Y,
                _menuActivatorRect.Width,
                _menuActivatorRect.Height);
            _toolbarToolTip.ShowNear(this, activatorText, activatorAnchor, IsBottomDock);
            _tooltipVisible = true;
            _tooltipShowTime = DateTime.UtcNow;
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

        // Cancel button: spell out what it cancels (the whole capture, discarded) and surface the Esc shortcut.
        if (button == CloseButtonIndex)
        {
            return isSpanish
                ? "Cancelar captura  (Esc)\nDescarta la selección y cierra sin guardar"
                : "Cancel capture  (Esc)\nDiscard the selection and close without saving";
        }

        if (button == StrokeWidthButtonIndex)
        {
            return string.Format(LocalizationService.Translate("Width: {0} points"), (int)_strokeWidth);
        }

        if (button < _mainBarTools.Length)
        {
            var tool = _mainBarTools[button];
            // OCR tool gets a more descriptive label in the tooltip
            if (tool.Id == "ocr")
                text = isSpanish ? "Extraer texto (OCR)" : "Extract text (OCR)";
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

    private void ShowConfirmTooltip()
    {
        if (!_isConfirmingSelection || _hoveredConfirmButton < 0 || (_confirmContextMenu != null && _confirmContextMenu.Visible))
        {
            HideToolbarTooltip();
            return;
        }

        _tooltipButton = 800 + _hoveredConfirmButton;
        _toolbarToolTip ??= new WindowsToolTip();

        var settings = Services.SettingsService.LoadStatic();
        var isSpanish = settings != null && string.Equals(settings.InterfaceLanguage, "es", StringComparison.OrdinalIgnoreCase);

        string text = _hoveredConfirmButton switch
        {
            0 => isSpanish
                ? "Confirmar captura  (Enter)\nGuarda o procesa la región seleccionada"
                : "Confirm capture  (Enter)\nSave or process the selected region",
            1 => isSpanish
                ? "Reintentar área  (Clic/Arrastrar)\nDescarta el recorte actual y vuelve a seleccionar"
                : "Retry area  (Click/Drag)\nDiscard the current crop and select again",
            2 => isSpanish
                ? "Cancelar captura completa  (Esc)\nCierra la herramienta de captura descartándolo todo"
                : "Cancel capture completely  (Esc)\nClose the capture tool and discard everything",
            _ => ""
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            HideToolbarTooltip();
            return;
        }

        var rects = GetConfirmButtonRects();
        var anchor = _hoveredConfirmButton switch
        {
            0 => rects.confirm,
            1 => rects.cancel,
            _ => rects.close
        };

        var anchorScreen = new Rectangle(
            _virtualBounds.X + anchor.X,
            _virtualBounds.Y + anchor.Y,
            anchor.Width,
            anchor.Height);

        _toolbarToolTip.ShowNear(this, text, anchorScreen, IsBottomDock);
        _tooltipVisible = true;
        _tooltipShowTime = DateTime.UtcNow;
    }
}
