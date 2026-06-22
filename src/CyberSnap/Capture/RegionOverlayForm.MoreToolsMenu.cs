using System;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Collections.Generic;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    public event Action<List<string>>? EnabledToolsChanged;

    private ContextMenuStrip? _toolbarContextMenu;
    private static List<string>? _rememberedAnnotationTools;

    private void ShowToolbarContextMenu(int buttonIndex, Point clickLocation)
    {
        ToolDef? tool = null;
        bool isHideable = buttonIndex >= 0 &&
                          buttonIndex != ColorButtonIndex &&
                          buttonIndex != StrokeWidthButtonIndex &&
                          buttonIndex != PositionButtonIndex &&
                          buttonIndex != CloseButtonIndex;

        if (isHideable)
        {
            if (buttonIndex < _mainBarTools.Length)
            {
                tool = _mainBarTools[buttonIndex];
            }
            else if (buttonIndex >= CloseButtonIndex + 1 && buttonIndex < BtnCount)
            {
                int flyoutIdx = buttonIndex - (CloseButtonIndex + 1);
                if (flyoutIdx >= 0 && flyoutIdx < _flyoutTools.Length)
                    tool = _flyoutTools[flyoutIdx];
            }
        }

        var settings = Services.SettingsService.LoadStatic();
        if (settings == null) return;

        var isSpanish = string.Equals(settings.InterfaceLanguage, "es", StringComparison.OrdinalIgnoreCase);
        var currentlyEnabled = settings.EnabledTools ?? ToolDef.DefaultEnabledIds();

        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 200);
        _toolbarContextMenu = menu;
        menu.Closed += (s, e) => {
            _toolbarContextMenu = null;
        };

        // 1. Tip item (only when right-clicking toolbar background/system buttons)
        if (tool == null)
        {
            var tipText = isSpanish
                ? "Haz clic derecho sobre los botones para ocultarlos."
                : "Right-click on the buttons to hide them.";
            var tipItem = WindowsMenuRenderer.Item(tipText, iconId: "lightbulb");
            tipItem.Enabled = false;
            menu.Items.Add(tipItem);
            menu.Items.Add(new ToolStripSeparator());
        }

        // 2. Hide option (only if hideable button with tool clicked)
        if (tool != null)
        {
            var hideText = isSpanish ? $"Ocultar \"{LocalizationService.Translate(tool.Label)}\"" : $"Hide \"{LocalizationService.Translate(tool.Label)}\"";
            var hideItem = WindowsMenuRenderer.Item(hideText, iconId: "trash");
            hideItem.Click += (s, e) => {
                HideTool(tool.Id);
                _toolbarContextMenu?.Close();
            };

            // Don't allow hiding the last capture tool
            if (tool.Group == 0)
            {
                var captureToolsCount = currentlyEnabled.Count(id => ToolDef.AllTools.Any(t => t.Id == id && t.Group == 0));
                if (captureToolsCount <= 1)
                {
                    hideItem.Enabled = false;
                    hideItem.ToolTipText = isSpanish ? "Debe haber al menos una herramienta de captura activa." : "Keep at least one capture tool enabled.";
                }
            }
            menu.Items.Add(hideItem);
            menu.Items.Add(new ToolStripSeparator());
        }

        // Show annotation bar checkable toggle (always visible)
        var annotationToolsCount = currentlyEnabled.Count(id => ToolDef.AllTools.Any(t => t.Id == id && t.Group == 1));
        var showBarText = isSpanish ? "Mostrar barra de anotaciones" : "Show annotation bar";
        var showBarItem = WindowsMenuRenderer.Item(showBarText, iconId: annotationToolsCount > 0 ? "check" : null);
        showBarItem.Click += (s, e) => {
            if (annotationToolsCount > 0)
            {
                HideAllAnnotationTools();
            }
            else
            {
                ShowAllAnnotationTools();
            }
            // Close the menu immediately so it doesn't repopulate with
            // tools whose visibility just changed.
            _toolbarContextMenu?.Close();
        };
        menu.Items.Add(showBarItem);
        menu.Items.Add(new ToolStripSeparator());

        // 3. Show Hidden — rendered as a flat section, NOT a nested submenu. The capture overlay is a
        // single window that spans every monitor, so on a multi-monitor setup with mixed DPI the
        // WinForms ToolStripDropDown places a second-level submenu on the wrong monitor and swallows
        // its first hover (per-monitor-DPI support for ToolStrip is known to be unreliable). Listing
        // the hidden tools inline sidesteps that entire class of bug.
        var allTools = ToolDef.AllTools;
        var hiddenTools = allTools.Where(t => !currentlyEnabled.Contains(t.Id)).ToList();

        // When the entire annotation bar is toggled off, don't flood the menu with every
        // individual annotation tool — the "Show annotation bar" toggle is enough to
        // restore them all. Capture tools hidden one-by-one still appear here.
        if (annotationToolsCount == 0)
            hiddenTools = hiddenTools.Where(t => t.Group != 1).ToList();

        if (hiddenTools.Count == 0)
        {
            var emptyText = isSpanish ? "Mostrar herramientas ocultas" : "Show hidden tools";
            var emptyItem = WindowsMenuRenderer.Item(emptyText, iconId: null);
            emptyItem.Enabled = false;
            menu.Items.Add(emptyItem);
        }
        else
        {
            var headerText = isSpanish ? "Herramientas ocultas:" : "Hidden tools:";
            var header = WindowsMenuRenderer.Item(headerText, iconId: null);
            header.Enabled = false;
            menu.Items.Add(header);

            foreach (var hTool in hiddenTools)
            {
                var iconId = hTool.Id == "scroll" ? "scrollCapture" : hTool.Id;
                var toolItem = WindowsMenuRenderer.Item(LocalizationService.Translate(hTool.Label), iconId: iconId);
                var targetId = hTool.Id;
                toolItem.Click += (s, e) => {
                    ShowTool(targetId);
                    _toolbarContextMenu?.Close();
                };
                menu.Items.Add(toolItem);
            }

            // 4. Show all hidden
            menu.Items.Add(new ToolStripSeparator());
            var showAllText = isSpanish ? "Restaurar herramientas" : "Restore tools";
            var showAllItem = WindowsMenuRenderer.Item(showAllText, iconId: null);
            showAllItem.Click += (s, e) => {
                ShowAllTools();
                _toolbarContextMenu?.Close();
            };
            menu.Items.Add(showAllItem);
        }

        // 5. Close/Cancel menu
        menu.Items.Add(new ToolStripSeparator());
        var closeMenuText = isSpanish ? "Cerrar menú" : "Close menu";
        var closeMenuItem = WindowsMenuRenderer.Item(closeMenuText, iconId: "close");
        closeMenuItem.Click += (s, e) => {
            menu.Close();
        };
        menu.Items.Add(closeMenuItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu, 200);

        var screenPoint = PointToScreen(clickLocation);

        // When triggered from the menu activator (... button), anchor the menu at
        // the right edge so it never covers the button — regardless of where inside
        // the activator the user clicked.
        if (buttonIndex == -1 && _menuActivatorRect.Contains(clickLocation))
            screenPoint = PointToScreen(new Point(_menuActivatorRect.Right, _menuActivatorRect.Top));

        menu.Show(screenPoint);
    }

    private void ShowAllTools()
    {
        var enabled = ToolDef.AllTools.Select(t => t.Id).ToList();
        EnabledToolsChanged?.Invoke(enabled);
        SetEnabledTools(enabled);
        CalcToolbar();
        InvalidateToolbarArea();
    }

    private void HideAllAnnotationTools()
    {
        var settings = Services.SettingsService.LoadStatic();
        if (settings == null) return;
        var enabled = (settings.EnabledTools ?? ToolDef.DefaultEnabledIds()).ToList();
        var annotationIds = ToolDef.AllTools.Where(t => t.Group == 1).Select(t => t.Id).ToList();

        // Remember which annotation tools were actually enabled before hiding
        var currentlyEnabledAnnotations = enabled.Where(id => annotationIds.Contains(id)).ToList();
        if (currentlyEnabledAnnotations.Count > 0)
        {
            _rememberedAnnotationTools = currentlyEnabledAnnotations;
        }

        foreach (var id in annotationIds)
        {
            enabled.Remove(id);
        }
        EnabledToolsChanged?.Invoke(enabled);
        SetEnabledTools(enabled);
        CalcToolbar();
        InvalidateToolbarArea();
    }

    private void ShowAllAnnotationTools()
    {
        var settings = Services.SettingsService.LoadStatic();
        if (settings == null) return;
        var enabled = (settings.EnabledTools ?? ToolDef.DefaultEnabledIds()).ToList();

        List<string> toolsToEnable;
        if (_rememberedAnnotationTools != null && _rememberedAnnotationTools.Count > 0)
        {
            toolsToEnable = _rememberedAnnotationTools;
        }
        else
        {
            toolsToEnable = ToolDef.AllTools.Where(t => t.Group == 1).Select(t => t.Id).ToList();
        }

        foreach (var id in toolsToEnable)
        {
            if (!enabled.Contains(id))
            {
                enabled.Add(id);
            }
        }
        EnabledToolsChanged?.Invoke(enabled);
        SetEnabledTools(enabled);
        CalcToolbar();
        InvalidateToolbarArea();
    }

    private void HideTool(string toolId)
    {
        var settings = Services.SettingsService.LoadStatic();
        if (settings == null) return;
        var enabled = (settings.EnabledTools ?? ToolDef.DefaultEnabledIds()).ToList();
        if (enabled.Contains(toolId))
        {
            enabled.Remove(toolId);
            EnabledToolsChanged?.Invoke(enabled);
            SetEnabledTools(enabled);
            CalcToolbar();
            InvalidateToolbarArea();
        }
    }

    private void ShowTool(string toolId)
    {
        var settings = Services.SettingsService.LoadStatic();
        if (settings == null) return;
        var enabled = (settings.EnabledTools ?? ToolDef.DefaultEnabledIds()).ToList();
        if (!enabled.Contains(toolId))
        {
            enabled.Add(toolId);
            EnabledToolsChanged?.Invoke(enabled);
            SetEnabledTools(enabled);
            CalcToolbar();
            InvalidateToolbarArea();
        }
    }
}
