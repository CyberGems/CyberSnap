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
                var label = BuildToolTooltip(altTool, settings, includeHideHint: false);
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

        // Logo / brand → quick-start guide
        if (_hoveredBrand)
        {
            if (_tooltipButton == 997)
                return;

            _tooltipButton = 997;
            _toolbarToolTip ??= new WindowsToolTip();
            var brandText = LocalizationService.Translate("Quick Start guide")
                + "\n" + LocalizationService.Translate("Click to open the capture guide");
            var brandLocal = Rectangle.Union(
                _logoRect.Width > 0 ? _logoRect : Rectangle.Empty,
                _brandRect.Width > 0 ? _brandRect : Rectangle.Empty);
            if (brandLocal.IsEmpty)
                brandLocal = _logoRect.Width > 0 ? _logoRect : _brandRect;
            var brandAnchor = new Rectangle(
                _virtualBounds.X + brandLocal.X,
                _virtualBounds.Y + brandLocal.Y,
                Math.Max(1, brandLocal.Width),
                Math.Max(1, brandLocal.Height));
            _toolbarToolTip.ShowNear(this, brandText, brandAnchor, IsBottomDock);
            _tooltipVisible = true;
            _tooltipShowTime = DateTime.UtcNow;
            return;
        }

        // Menu activator (▼ chevron)
        if (_hoveredMenuActivator)
        {
            if (_tooltipButton == 998)
                return;

            _tooltipButton = 998;
            _toolbarToolTip ??= new WindowsToolTip();
            var activatorText = LocalizationService.Translate("More options")
                + "\n" + LocalizationService.Translate("Hidden tools, preferences, and quick start guide");
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

        var settings = Services.SettingsService.LoadStatic();

        if (button == CloseButtonIndex)
        {
            return LocalizationService.Translate("Cancel capture")
                + "  (Esc)\n"
                + LocalizationService.Translate("Discard the selection and close without saving");
        }

        if (button == PositionButtonIndex)
        {
            return LocalizationService.Translate("Toolbar Position")
                + "\n"
                + LocalizationService.Translate("Move the bar to the opposite edge, or drag to position freely");
        }

        if (button == StrokeWidthButtonIndex)
        {
            return string.Format(LocalizationService.Translate("Width: {0} points"), (int)_strokeWidth)
                + "\n"
                + LocalizationService.Translate("Click to cycle stroke width");
        }

        if (button == ColorButtonIndex)
        {
            return LocalizationService.Translate("Active drawing and text color")
                + "\n"
                + LocalizationService.Translate("Click to open the color palette");
        }

        if (button < _mainBarTools.Length)
        {
            var tool = _mainBarTools[button];
            var text = BuildToolTooltip(tool, settings, includeHideHint: true);

            if (button == _mergedCaptureButtonIndex)
            {
                var defaultMode = settings?.DefaultCaptureMode ?? CaptureMode.Rectangle;
                text += "\n" + (defaultMode == CaptureMode.Center
                    ? LocalizationService.Translate("Hold to show Area Capture tool")
                    : LocalizationService.Translate("Hold to show From Center tool"));
            }

            return text;
        }

        if (button >= CloseButtonIndex + 1 && button < BtnCount)
        {
            int flyoutIdx = button - (CloseButtonIndex + 1);
            if (flyoutIdx >= 0 && flyoutIdx < _flyoutTools.Length)
                return BuildToolTooltip(_flyoutTools[flyoutIdx], settings, includeHideHint: true);
        }

        // Fallback: plain label
        return _toolbarLabels[button];
    }

    private static string BuildToolTooltip(ToolDef tool, AppSettings? settings, bool includeHideHint)
    {
        var title = tool.Id == "ocr"
            ? LocalizationService.Translate("Extract text (OCR)")
            : LocalizationService.Translate(tool.Label);

        var hotkey = settings?.GetToolHotkey(tool.Id) ?? (0u, 0u);
        if (hotkey.key != 0)
            title += $"  ({HotkeyFormatter.Format(hotkey.mod, hotkey.key)})";

        var usage = GetToolUsageHint(tool);
        var text = string.IsNullOrEmpty(usage) ? title : title + "\n" + usage;

        if (includeHideHint)
            text += "\n" + LocalizationService.Translate("Right-click to hide");

        return text;
    }

    /// <summary>One-line “how to use” hint for tooltips (reuses capture-banner phrasing).</summary>
    private static string GetToolUsageHint(ToolDef tool)
    {
        if (tool.Mode is not { } m)
            return "";

        return m switch
        {
            CaptureMode.Rectangle => LocalizationService.Translate("Click & drag to capture"),
            CaptureMode.Center => LocalizationService.Translate("Click for centered capture"),
            CaptureMode.Ocr => LocalizationService.Translate("Select text area to recognize"),
            CaptureMode.Scan => LocalizationService.Translate("Select QR or barcode to scan"),
            CaptureMode.ScrollCapture => LocalizationService.Translate("Select scrolling area"),
            CaptureMode.Ruler => LocalizationService.Translate("Click & drag to measure"),
            CaptureMode.ColorPicker => LocalizationService.Translate("Click a pixel to pick its color"),
            CaptureMode.Record or CaptureMode.RecordGif => LocalizationService.Translate("Click & drag to select area"),
            CaptureMode.Move => string.Format(
                LocalizationService.Translate("Click to select · Drag to move · Double-click {0} to select all"),
                LocalizationService.Translate("Pick")),
            CaptureMode.Eraser => LocalizationService.Translate("Click or drag to erase objects"),
            CaptureMode.Highlight => LocalizationService.Translate("Click & drag to highlight"),
            CaptureMode.Text => LocalizationService.Translate("Click to place text"),
            CaptureMode.Arrow => LocalizationService.Translate("Click & drag to draw arrow"),
            CaptureMode.Line => LocalizationService.Translate("Click & drag to draw line"),
            CaptureMode.Draw => LocalizationService.Translate("Click & drag to draw"),
            CaptureMode.CurvedArrow => LocalizationService.Translate("Click & drag to draw curved arrow"),
            CaptureMode.CircleShape => LocalizationService.Translate("Click & drag to draw circle"),
            CaptureMode.RectShape => LocalizationService.Translate("Click & drag to draw rectangle"),
            CaptureMode.StepNumber => LocalizationService.Translate("Click to place step number"),
            CaptureMode.Magnifier => LocalizationService.Translate("Click to place magnifier"),
            CaptureMode.Blur => LocalizationService.Translate("Click & drag to blur"),
            CaptureMode.Emoji => LocalizationService.Translate("Click to pick emoji"),
            _ => ""
        };
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

        string text = _hoveredConfirmButton switch
        {
            0 => LocalizationService.Translate("Confirm capture")
                + "  (Enter · "
                + LocalizationService.Translate("double-click")
                + ")\n"
                + LocalizationService.Translate("Save or process the selected region"),
            1 => LocalizationService.Translate("Retry area")
                + "  (R)\n"
                + LocalizationService.Translate("Discard the current crop and select again"),
            2 => LocalizationService.Translate("Cancel capture completely")
                + "  (Esc)\n"
                + LocalizationService.Translate("Close the capture tool and discard everything"),
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
