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
    private QuickStartGuide? _quickStartGuide;
    private static List<string>? _rememberedAnnotationTools;

    private void ShowQuickStartGuide()
    {
        if (_quickStartGuide != null && _quickStartGuide.Visible)
        {
            _quickStartGuide.Close();
            _quickStartGuide = null;
            return;
        }

        _quickStartGuide ??= new QuickStartGuide();

        var logoScreen = new Rectangle(
            _virtualBounds.X + _logoRect.X,
            _virtualBounds.Y + _logoRect.Y,
            _logoRect.Width,
            _logoRect.Height);

        _quickStartGuide.ShowNear(this, logoScreen, above: IsBottomDock);
        _quickStartGuide.FormClosed += (_, _) => _quickStartGuide = null;
    }

    private void DismissQuickStartGuide()
    {
        if (_quickStartGuide != null && _quickStartGuide.Visible)
        {
            _quickStartGuide.Close();
            _quickStartGuide = null;
        }
    }

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
        menu.Font = UiChrome.ChromeFont(11.0f); // unified style with confirm context menu
        _toolbarContextMenu = menu;
        menu.Closed += (s, e) => {
            _toolbarContextMenu = null;
        };

        // Compute hidden tools early — needed for header separators and Restore item below
        var allTools = ToolDef.AllTools;
        var hiddenTools = allTools.Where(t => !currentlyEnabled.Contains(t.Id)).ToList();

        // CyberSnap header — always shown when a system button or toolbar background is clicked
        if (tool == null)
        {
            string? systemButtonName = null;
            string? systemIconId = null;
            if (buttonIndex == StrokeWidthButtonIndex)
            {
                systemButtonName = LocalizationService.Translate("Shape stroke width");
                systemIconId = "ruler";
            }
            else if (buttonIndex == ColorButtonIndex)
            {
                systemButtonName = LocalizationService.Translate("Active drawing and text color");
                systemIconId = "picker";
            }
            else if (buttonIndex == PositionButtonIndex)
            {
                systemButtonName = LocalizationService.Translate("Toolbar Position");
                systemIconId = "position";
            }
            else if (buttonIndex == CloseButtonIndex)
            {
                systemButtonName = LocalizationService.Translate("Cancel");
                systemIconId = "close";
            }

            if (systemButtonName != null)
            {
                // Button reference line — shows the icon for visual context
                var sysLine = isSpanish
                    ? $"Botón {systemButtonName}  •  Siempre visible"
                    : $"{systemButtonName}  button  •  Always visible";
                var refItem = WindowsMenuRenderer.Item(sysLine, iconId: systemIconId, iconSize: 24);
                refItem.Enabled = false;
                menu.Items.Add(refItem);
                menu.Items.Add(new ToolStripSeparator());
            }
            else
            {
                var hint = isSpanish
                    ? "Clic en el logo para ver consejos"
                    : "Click the logo for tips";
                var headerText = $"CyberSnap  {Services.UpdateService.GetCurrentVersionLabel()}  •  {hint}";

                var headerLabel = new ToolStripLabel(headerText)
                {
                    ForeColor = UiChrome.SurfaceTextMuted,
                    Font = UiChrome.ChromeFont(8.5f),
                    Padding = new System.Windows.Forms.Padding(10, 12, 0, 2),
                    AutoSize = true,
                };
                menu.Items.Add(headerLabel);
                if (hiddenTools.Count > 0)
                    menu.Items.Add(new ToolStripSeparator());
            }
        }

        // 1. Tip item removed — hint is now part of the header line

        // 2. Hide option (only if hideable button with tool clicked)
        if (tool != null)
        {
            var toolIconId = tool.Id switch { "crop" => "rect", "rect" => "captureRect", "scroll" => "scrollCapture", var id => id };
            var hideText = isSpanish ? $"Ocultar \"{LocalizationService.Translate(tool.Label)}\"" : $"Hide \"{LocalizationService.Translate(tool.Label)}\"";
            var hideItem = WindowsMenuRenderer.Item(hideText, iconId: toolIconId, iconSize: 24);
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
            if (hiddenTools.Count > 0)
                menu.Items.Add(new ToolStripSeparator());
        }

        // Show annotation bar checkable toggle — created now, added at different positions
        // depending on whether we're in the tool-specific or general menu.
        var annotationToolsCount = currentlyEnabled.Count(id => ToolDef.AllTools.Any(t => t.Id == id && t.Group == 1));
        var showBarText = isSpanish ? "Mostrar barra de anotaciones" : "Show annotation bar";
        var showBarItem = WindowsMenuRenderer.Item(showBarText, iconId: annotationToolsCount > 0 ? "check" : null, iconSize: 24);
        showBarItem.Click += (s, e) => {
            bool wasVisible = annotationToolsCount > 0;
            if (wasVisible)
                HideAllAnnotationTools();
            else
                ShowAllAnnotationTools();
            // Defer toast to after menu closes — toolbar rebuild can disrupt the menu pump.
            _toolbarContextMenu?.Close();
        };

        if (tool == null)
        {
            menu.Items.Add(showBarItem);
        }

        // 3. Show Hidden — rendered as a flat section, NOT a nested submenu. The capture overlay is a
        // single window that spans every monitor, so on a multi-monitor setup with mixed DPI the
        // WinForms ToolStripDropDown places a second-level submenu on the wrong monitor and swallows
        // its first hover (per-monitor-DPI support for ToolStrip is known to be unreliable). Listing
        // the hidden tools inline sidesteps that entire class of bug.

        // When the entire annotation bar is toggled off, don't flood the menu with every
        // individual annotation tool — the "Show annotation bar" toggle is enough to
        // restore them all. Capture tools hidden one-by-one still appear here.
        if (annotationToolsCount == 0)
            hiddenTools = hiddenTools.Where(t => t.Group != 1).ToList();

        if (hiddenTools.Count > 0)
        {
            // Restore all hidden tools
            AddRestoreHiddenToolsItem(menu, isSpanish, hiddenTools.Count);

            var headerText = isSpanish ? "Herramientas ocultas:" : "Hidden tools:";
            var header = WindowsMenuRenderer.Item(headerText, iconId: null, iconSize: 24);
            header.Enabled = false;
            menu.Items.Add(header);

            foreach (var hTool in hiddenTools)
            {
                var toolItem = WindowsMenuRenderer.Item(LocalizationService.Translate(hTool.Label), iconId: "add", iconSize: 24);
                toolItem.Padding = new Padding(24, 0, 0, 0);
                var targetId = hTool.Id;
                toolItem.Click += (s, e) => {
                    ShowTool(targetId);
                    _toolbarContextMenu?.Close();
                };
                menu.Items.Add(toolItem);
            }
        }

        // Confirm before exit toggle
        menu.Items.Add(new ToolStripSeparator());
        var confirmExitEnabled = settings.ConfirmBeforeExit;
        var confirmExitText = isSpanish ? "Confirmar antes de salir" : "Confirm before exit";
        var confirmExitItem = WindowsMenuRenderer.Item(confirmExitText, iconId: confirmExitEnabled ? "check" : null, iconSize: 24);
        confirmExitItem.Click += (s, e) =>
        {
            var svc = new Services.SettingsService(null);
            svc.Load();
            var newVal = !svc.Settings.ConfirmBeforeExit;
            svc.Settings.ConfirmBeforeExit = newVal;
            svc.Save();
            _toolbarContextMenu?.Close();
        };
        menu.Items.Add(confirmExitItem);

        // Help banners toggle
        menu.Items.Add(new ToolStripSeparator());
        var bannersEnabled = settings.ShowToolBanners;
        var bannersText = isSpanish ? "Mostrar banners de ayuda" : "Show help banners";
        var bannersItem = WindowsMenuRenderer.Item(bannersText, iconId: bannersEnabled ? "check" : null, iconSize: 24);
        bannersItem.Click += (s, e) =>
        {
            var svc = new Services.SettingsService(null);
            svc.Load();
            var newValue = !svc.Settings.ShowToolBanners;
            svc.Settings.ShowToolBanners = newValue;
            StandaloneToolBanner.Enabled = newValue;
            svc.Save();

            _toolbarContextMenu?.Close();
        };
        menu.Items.Add(bannersItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu, 200, itemHeight: 46);

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

    private void AddRestoreHiddenToolsItem(ContextMenuStrip menu, bool isSpanish, int hiddenCount)
    {
        var restoreText = isSpanish ? "Restaurar herramientas ocultas" : "Restore hidden tools";
        var restoreItem = WindowsMenuRenderer.Item(restoreText, iconId: "add", iconSize: 24);
        restoreItem.Enabled = hiddenCount > 0;
        restoreItem.Click += (s, e) => {
            ShowAllTools();
            _toolbarContextMenu?.Close();
        };
        menu.Items.Add(restoreItem);
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
