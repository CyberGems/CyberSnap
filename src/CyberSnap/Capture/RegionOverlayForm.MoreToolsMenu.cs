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
    private ContextMenuStrip? _confirmContextMenu;
    private QuickStartGuide? _quickStartGuide;
    private bool _highlightMenuActivatorForGuide;
    private DateTime _menuActivatorPulseStart;
    private System.Windows.Forms.Timer? _menuActivatorPulseTimer;
    private static List<string>? _rememberedAnnotationTools;

    private void ShowQuickStartGuide()
    {
        if (_quickStartGuide != null && _quickStartGuide.Visible)
        {
            _quickStartGuide.Close();
            return;
        }

        var anchorLocal = GetQuickStartAnchorLocal();
        if (anchorLocal.Width <= 0 || anchorLocal.Height <= 0)
            return;

        _quickStartGuide ??= new QuickStartGuide();

        var anchorScreen = new Rectangle(
            _virtualBounds.X + anchorLocal.X,
            _virtualBounds.Y + anchorLocal.Y,
            anchorLocal.Width,
            anchorLocal.Height);

        // Bubble above the logo when the toolbar is at the bottom or side; below when docked top.
        bool above = CaptureDockSide != CaptureDockSide.Top;

        _quickStartGuide.ShowNear(this, anchorScreen, above: above);
        _quickStartGuide.FormClosed -= OnQuickStartGuideClosed;
        _quickStartGuide.FormClosed += OnQuickStartGuideClosed;
        StartMenuActivatorPulse();
    }

    private void OnQuickStartGuideClosed(object? sender, FormClosedEventArgs e)
    {
        if (_quickStartGuide != null)
        {
            _quickStartGuide.FormClosed -= OnQuickStartGuideClosed;
            _quickStartGuide = null;
        }

        StopMenuActivatorPulse();

        // First dismissal (auto or manual) marks the guide as seen so it does not re-auto-open.
        QuickStartGuideDismissed?.Invoke();
    }

    private void StartMenuActivatorPulse()
    {
        _highlightMenuActivatorForGuide = true;
        _menuActivatorPulseStart = DateTime.UtcNow;
        if (_menuActivatorPulseTimer == null)
        {
            _menuActivatorPulseTimer = new System.Windows.Forms.Timer { Interval = UiChrome.FrameIntervalMs };
            _menuActivatorPulseTimer.Tick += (_, _) =>
            {
                if (!_highlightMenuActivatorForGuide || IsDisposed || Disposing)
                {
                    StopMenuActivatorPulse();
                    return;
                }
                InvalidateMenuActivatorArea();
            };
        }
        _menuActivatorPulseTimer.Start();
        InvalidateMenuActivatorArea();
    }

    private void StopMenuActivatorPulse()
    {
        _highlightMenuActivatorForGuide = false;
        try { _menuActivatorPulseTimer?.Stop(); } catch { }
        if (_menuActivatorPulseTimer != null)
        {
            try { _menuActivatorPulseTimer.Dispose(); } catch { }
            _menuActivatorPulseTimer = null;
        }
        if (!IsDisposed && !Disposing)
            InvalidateMenuActivatorArea();
    }

    private void InvalidateMenuActivatorArea()
    {
        // Toolbar is a separate layered form that only redraws when render version bumps.
        // Invalidate alone does nothing — must MarkToolbarRenderDirty + UpdateSurface.
        UpdateToolbarSurfaceOnly();
    }

    private Rectangle GetQuickStartAnchorLocal()
    {
        // Prefer the painted logo; brand column is a solid fallback after CalcToolbar.
        if (_logoRect.Width > 0 && _logoRect.Height > 0)
            return _logoRect.Width > 0 && _brandRect.Width > 0
                ? Rectangle.Union(_logoRect, _brandRect)
                : _logoRect;
        if (_brandRect.Width > 0 && _brandRect.Height > 0)
            return _brandRect;
        // Last resort: left edge of the toolbar surface.
        if (_toolbarRect.Width > 0)
            return new Rectangle(_toolbarRect.X + 4, _toolbarRect.Y + 4, 24, 24);
        return Rectangle.Empty;
    }

    /// <summary>
    /// First-run: open the talk-bubble guide once the toolbar logo has a valid anchor.
    /// </summary>
    private void TryAutoShowQuickStartGuide()
    {
        if (IsDisposed || Disposing || !Visible || _isConfirmingSelection)
            return;

        var settings = SettingsService.LoadStatic();
        if (settings == null || settings.HasSeenQuickStartGuide)
            return;

        if (_quickStartGuide != null && _quickStartGuide.Visible)
            return;

        // Ensure logo/brand rects exist (painted during toolbar surface update).
        EnsureToolbarReady();
        try { _toolbarForm?.UpdateSurface(); } catch { }

        if (GetQuickStartAnchorLocal().IsEmpty)
            return;

        ShowQuickStartGuide();
    }

    private void DismissQuickStartGuide()
    {
        if (_quickStartGuide != null && _quickStartGuide.Visible)
            _quickStartGuide.Close();
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
            _lastContextMenuClosedTime = DateTime.UtcNow;
            _lastContextMenuBtnIndex = buttonIndex;
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
                systemIconId = "signOut";
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
                var headerText = $"CyberSnap  {Services.UpdateService.GetCurrentVersionLabel()}";

                var headerLabel = new ToolStripLabel(headerText)
                {
                    ForeColor = UiChrome.SurfaceTextMuted,
                    Font = UiChrome.ChromeFont(8.5f),
                    Padding = new System.Windows.Forms.Padding(10, 12, 0, 2),
                    AutoSize = true,
                };
                menu.Items.Add(headerLabel);
                menu.Items.Add(new ToolStripSeparator());
            }
        }

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
            menu.Items.Add(new ToolStripSeparator());
        }

        // Bulk show/hide annotation tools — only while confirming (capture has no annot dock).
        if (_isConfirmingSelection)
        {
            var annotationToolsCount = currentlyEnabled.Count(id => ToolDef.AllTools.Any(t => t.Id == id && t.Group == 1));
            bool toolsVisible = annotationToolsCount > 0;
            var showAnnotItem = WindowsMenuRenderer.Item(
                "Show annotation tools",
                iconId: toolsVisible ? "check" : null,
                iconSize: 24);
            showAnnotItem.Click += (s, e) => {
                if (toolsVisible)
                    HideAllAnnotationTools();
                else
                    ShowAllAnnotationTools();
                _toolbarContextMenu?.Close();
            };
            menu.Items.Add(showAnnotItem);
        }

        // 3. Show Hidden — rendered as a flat section, NOT a nested submenu.
        // single window that spans every monitor, so on a multi-monitor setup with mixed DPI the
        // WinForms ToolStripDropDown places a second-level submenu on the wrong monitor and swallows
        // its first hover (per-monitor-DPI support for ToolStrip is known to be unreliable). Listing
        // the hidden tools inline sidesteps that entire class of bug.

        // When annotation tools are bulk-hidden, don't flood the menu with every
        // individual annotation tool — the "Show annotation tools" toggle restores them.
        // Capture tools hidden one-by-one still appear here.
        var annotationToolsEnabled = currentlyEnabled.Count(id => ToolDef.AllTools.Any(t => t.Id == id && t.Group == 1));
        if (annotationToolsEnabled == 0)
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
        AddMenuSeparatorIfNeeded(menu);
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
        AddMenuSeparatorIfNeeded(menu);
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

        // Quick-start guide (always available after first run; re-opens the talk bubble)
        var guideText = LocalizationService.Translate("Show quick start guide");
        var guideItem = WindowsMenuRenderer.Item(guideText, iconId: "info", iconSize: 24);
        guideItem.Click += (s, e) =>
        {
            _toolbarContextMenu?.Close();
            // Open after the menu has fully closed so it does not steal the first click.
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || Disposing || !Visible)
                    return;
                if (_quickStartGuide != null && _quickStartGuide.Visible)
                    return;
                ShowQuickStartGuide();
            }));
        };
        menu.Items.Add(guideItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu, 200, itemHeight: 46);

        var screenPoint = PointToScreen(clickLocation);

        // When triggered from the menu activator (... button), anchor the menu at
        // the right edge so it never covers the button — regardless of where inside
        // the activator the user clicked.
        if (buttonIndex == -1 && _menuActivatorRect.Contains(clickLocation))
            screenPoint = PointToScreen(new Point(_menuActivatorRect.Right, _menuActivatorRect.Top));

        menu.Show(screenPoint);
    }

    private static void AddMenuSeparatorIfNeeded(ContextMenuStrip menu)
    {
        if (menu.Items.Count == 0) return;
        if (menu.Items[menu.Items.Count - 1] is ToolStripSeparator) return;
        menu.Items.Add(new ToolStripSeparator());
    }

    private void ShowAllTools()
    {
        var enabled = ToolDef.AllTools.Select(t => t.Id).ToList();
        EnabledToolsChanged?.Invoke(enabled);
        SetEnabledTools(enabled);
        RefreshToolbar();
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
        RefreshToolbar();
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
        RefreshToolbar();
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
            RefreshToolbar();

            var tool = ToolDef.AllTools.FirstOrDefault(t => t.Id == toolId);
            var name = tool != null
                ? LocalizationService.Translate(tool.Label)
                : toolId;
            // Brief banner so the user knows how to bring the tool back.
            var msg = string.Format(
                LocalizationService.Translate("\"{0}\" hidden · Restore from ▼"),
                name);
            ShowToolBanner(msg, persistent: false);
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
            RefreshToolbar();
        }
    }
}
