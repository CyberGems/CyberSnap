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

        var menu = WindowsMenuRenderer.Create(showImages: false, minWidth: 200);
        _toolbarContextMenu = menu;
        menu.Closed += (s, e) => {
            _toolbarContextMenu = null;
        };

        // 1. Tip item (only when right-clicking toolbar background/system buttons)
        if (tool == null)
        {
            var tipText = isSpanish
                ? "💡 Haz clic derecho sobre los botones para ocultarlos."
                : "💡 Right-click on the buttons to hide them.";
            var tipItem = new ToolStripMenuItem(tipText) { Enabled = false };
            menu.Items.Add(tipItem);
            menu.Items.Add(new ToolStripSeparator());
        }

        // 2. Hide option (only if hideable button with tool clicked)
        if (tool != null)
        {
            var hideText = isSpanish ? $"Ocultar \"{LocalizationService.Translate(tool.Label)}\"" : $"Hide \"{LocalizationService.Translate(tool.Label)}\"";
            var hideItem = new ToolStripMenuItem(hideText);
            hideItem.Click += (s, e) => {
                HideTool(tool.Id);
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
        var showBarItem = new ToolStripMenuItem(showBarText)
        {
            CheckOnClick = true,
            Checked = annotationToolsCount > 0
        };
        showBarItem.Click += (s, e) => {
            if (showBarItem.Checked)
            {
                ShowAllAnnotationTools();
            }
            else
            {
                HideAllAnnotationTools();
            }
        };
        menu.Items.Add(showBarItem);
        menu.Items.Add(new ToolStripSeparator());

        // 3. Show Hidden submenu
        var showHiddenText = isSpanish ? "Mostrar ocultos" : "Show Hidden";
        var showHiddenSubmenu = WindowsMenuRenderer.Submenu(showHiddenText, showImages: false);

        var allTools = ToolDef.AllTools;
        var hiddenTools = allTools.Where(t => !currentlyEnabled.Contains(t.Id)).ToList();

        if (hiddenTools.Count == 0)
        {
            showHiddenSubmenu.Enabled = false;
        }
        else
        {
            foreach (var hTool in hiddenTools)
            {
                var toolItem = new ToolStripMenuItem(LocalizationService.Translate(hTool.Label));
                var targetId = hTool.Id;
                toolItem.Click += (s, e) => {
                    ShowTool(targetId);
                };
                showHiddenSubmenu.DropDownItems.Add(toolItem);
            }
        }
        menu.Items.Add(showHiddenSubmenu);

        // 4. Show all hidden
        if (hiddenTools.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            var showAllText = isSpanish ? "Mostrar todos" : "Show all hidden";
            var showAllItem = new ToolStripMenuItem(showAllText);
            showAllItem.Click += (s, e) => {
                ShowAllTools();
            };
            menu.Items.Add(showAllItem);
        }

        WindowsMenuRenderer.NormalizeItemWidths(menu, 200);
        if (showHiddenSubmenu.DropDownItems.Count > 0)
        {
            WindowsMenuRenderer.NormalizeDropDownWidths(showHiddenSubmenu, 160);
        }

        var screenPoint = PointToScreen(clickLocation);
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
        var annotationIds = ToolDef.AllTools.Where(t => t.Group == 1).Select(t => t.Id);
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
        var annotationIds = ToolDef.AllTools.Where(t => t.Group == 1).Select(t => t.Id);
        foreach (var id in annotationIds)
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
