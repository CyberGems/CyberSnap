using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    /// <summary>
    /// Vertical annotation/capture columns: tip beside the button so it never covers neighbors.
    /// Horizontal docks keep above/below.
    /// </summary>
    private ToolTipPlacement GetToolbarToolTipPlacement()
    {
        if (ShowAnnotationChrome)
        {
            return _annotationFrameDockSide == CaptureDockSide.Right
                ? ToolTipPlacement.Left
                : ToolTipPlacement.Right;
        }

        if (IsVerticalDock)
        {
            return IsRightDock ? ToolTipPlacement.Left : ToolTipPlacement.Right;
        }

        return IsBottomDock ? ToolTipPlacement.Above : ToolTipPlacement.Below;
    }

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

        var placement = GetToolbarToolTipPlacement();

        if (_hoveredAltSlotIndex >= 0 && _altCapturePopupOpen && _hoveredAltSlotIndex < _altPopupSlots.Count)
        {
            // One tooltip id per slot so switching between multi-alt slots refreshes the text.
            int tipId = 900 + _hoveredAltSlotIndex;
            if (_tooltipButton == tipId)
                return;

            _tooltipButton = tipId;
            _toolbarToolTip ??= new WindowsToolTip();

            var settings = Services.SettingsService.LoadStatic();
            var altToolId = _altPopupSlots[_hoveredAltSlotIndex].ToolId;
            var altTool = ToolDef.AllTools.FirstOrDefault(t => t.Id == altToolId);
            if (altTool != null)
            {
                var label = BuildToolTooltip(altTool, settings, includeHideHint: false);
                var slot = _altPopupSlots[_hoveredAltSlotIndex].Container;
                var altAnchorScreen = new Rectangle(
                    _virtualBounds.X + slot.X,
                    _virtualBounds.Y + slot.Y,
                    slot.Width,
                    slot.Height);
                _toolbarToolTip.ShowNear(this, label, altAnchorScreen, placement);
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
            var brandLocal = !_logoRect.IsEmpty && !_brandRect.IsEmpty
                ? Rectangle.Union(_logoRect, new Rectangle(_logoRect.X, _logoRect.Y, Math.Min(_brandRect.Width, Helpers.UiChrome.ScaleInt(85)), _logoRect.Height))
                : (!_logoRect.IsEmpty ? _logoRect : _brandRect);
            if (brandLocal.IsEmpty)
                brandLocal = _logoRect;
            var brandAnchor = new Rectangle(
                _virtualBounds.X + brandLocal.X,
                _virtualBounds.Y + brandLocal.Y,
                Math.Max(1, brandLocal.Width),
                Math.Max(1, brandLocal.Height));
            _toolbarToolTip.ShowNear(this, brandText, brandAnchor, placement);
            _tooltipVisible = true;
            _tooltipShowTime = DateTime.UtcNow;
            return;
        }

        // Empty branding area → Drag toolbar hint
        if (_hoveredBrandDragArea)
        {
            if (_tooltipButton == 996)
                return;

            _tooltipButton = 996;
            _toolbarToolTip ??= new WindowsToolTip();
            var dragText = LocalizationService.Translate("Drag to move toolbar")
                + "\n" + LocalizationService.Translate("Click and hold to drag the toolbar");
            var dragAnchor = new Rectangle(
                _virtualBounds.X + _brandRect.X,
                _virtualBounds.Y + _brandRect.Y,
                Math.Max(1, _brandRect.Width),
                Math.Max(1, _brandRect.Height));
            _toolbarToolTip.ShowNear(this, dragText, dragAnchor, placement);
            _tooltipVisible = true;
            _tooltipShowTime = DateTime.UtcNow;
            return;
        }

        // Menu activator (⋮ more options) — capture bar only
        if (_hoveredMenuActivator && !_menuActivatorRect.IsEmpty)
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
            _toolbarToolTip.ShowNear(this, activatorText, activatorAnchor, placement);
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
        _toolbarToolTip.ShowNear(this, text, anchorScreen, placement);
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
            {
                // Annotation column: keep tips compact (title + hotkey / hold hint) so
                // side-placed bubbles stay readable without drowning the bar.
                var text = BuildToolTooltip(
                    _flyoutTools[flyoutIdx],
                    settings,
                    includeHideHint: !ShowAnnotationChrome,
                    includeUsageHint: !ShowAnnotationChrome);
                if (IsAnnotationMergeButton(button)
                    && _annotationMergeAltsByButton.TryGetValue(button, out var alts)
                    && alts.Length > 0)
                {
                    text += "\n" + LocalizationService.Translate("Hold to switch tool");
                }
                return text;
            }
        }

        // Fallback: plain label
        return _toolbarLabels[button];
    }

    private static string BuildToolTooltip(
        ToolDef tool,
        AppSettings? settings,
        bool includeHideHint,
        bool includeUsageHint = true)
    {
        var title = tool.Id == "ocr"
            ? LocalizationService.Translate("Extract text (OCR)")
            : LocalizationService.Translate(tool.Label);

        var hotkey = settings?.GetToolHotkey(tool.Id) ?? (0u, 0u);
        if (hotkey.key != 0)
            title += $"  ({HotkeyFormatter.Format(hotkey.mod, hotkey.key)})";

        var text = title;
        if (includeUsageHint)
        {
            var usage = GetToolUsageHint(tool);
            if (!string.IsNullOrEmpty(usage))
                text += "\n" + usage;
        }

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
        if (!_isConfirmingSelection || _hoveredConfirmButton < 0
            || (_confirmContextMenu != null && _confirmContextMenu.Visible)
            || (_toolbarContextMenu != null && _toolbarContextMenu.Visible))
        {
            HideToolbarTooltip();
            return;
        }

        if (_hoveredConfirmButton >= _confirmChromeKinds.Length)
        {
            HideToolbarTooltip();
            return;
        }

        _tooltipButton = 800 + _hoveredConfirmButton;
        _toolbarToolTip ??= new WindowsToolTip();

        var kind = _confirmChromeKinds[_hoveredConfirmButton];
        bool isPrimary = _hoveredConfirmButton == IndexOfPrimaryConfirmAction();
        string hotkey = ConfirmChromeHotkeyHint(kind);
        // Image toggles the modes strip — never imply a click captures (Done / Enter / I do).
        if (kind == ConfirmChromeKind.ModeImage)
            hotkey = "";

        // Hint beside the primary pill: Done shows Enter; hover pills show their own hotkey.
        string hint = isPrimary && string.IsNullOrEmpty(hotkey)
            ? "  (Enter)"
            : (string.IsNullOrEmpty(hotkey) ? "" : "  (" + hotkey + ")");

        string title = kind == ConfirmChromeKind.ModeImage
            ? LocalizationService.Translate("Image capture mode")
            : ConfirmChromeTitle(kind);

        string text = kind switch
        {
            ConfirmChromeKind.Retry => title + "  (R)",
            _ => title + hint
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            HideToolbarTooltip();
            return;
        }

        LayoutConfirmChromeRects();
        var anchor = _confirmChromeRects[_hoveredConfirmButton];

        var anchorScreen = new Rectangle(
            _virtualBounds.X + anchor.X,
            _virtualBounds.Y + anchor.Y,
            anchor.Width,
            anchor.Height);

        // Confirm dock is horizontal under the frame — keep tips above the pills.
        // singleLine: title + hotkey hint stay on one row (Preview toggle, Done, etc.).
        _toolbarToolTip.ShowNear(this, text, anchorScreen, ToolTipPlacement.Above, singleLine: true);
        _tooltipVisible = true;
        _tooltipShowTime = DateTime.UtcNow;
    }

    private void ShowConfirmOptionsTooltip()
    {
        if (!_isConfirmingSelection || !_hoveredConfirmOptionsPill || _confirmOptionsPillRect.IsEmpty
            || (_confirmContextMenu != null && _confirmContextMenu.Visible)
            || (_toolbarContextMenu != null && _toolbarContextMenu.Visible))
        {
            HideToolbarTooltip();
            return;
        }

        if (_tooltipButton == 997)
            return;

        _tooltipButton = 997;
        _toolbarToolTip ??= new WindowsToolTip();
        var text = LocalizationService.Translate("More options")
            + "\n" + LocalizationService.Translate("Hidden tools, preferences, and quick start guide");
        var anchorScreen = new Rectangle(
            _virtualBounds.X + _confirmOptionsPillRect.X,
            _virtualBounds.Y + _confirmOptionsPillRect.Y,
            _confirmOptionsPillRect.Width,
            _confirmOptionsPillRect.Height);
        _toolbarToolTip.ShowNear(this, text, anchorScreen, ToolTipPlacement.Below);
        _tooltipVisible = true;
        _tooltipShowTime = DateTime.UtcNow;
    }

    private static string BuildCopyConfirmTooltip(string primaryHint)
    {
        string text = LocalizationService.Translate("Copy to clipboard") + primaryHint;
        if (IsImageAutoCopyEnabled())
        {
            text += "\n"
                + LocalizationService.Translate("Auto-copy is on — image captures already go to the clipboard")
                + "\n"
                + LocalizationService.Translate("Click to copy again");
        }
        return text;
    }
}
