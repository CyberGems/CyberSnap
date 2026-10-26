using System.Drawing;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.Capture;

public sealed partial class RegionOverlayForm
{
    private string ConfirmationGuideTitle => SupportsConfirmAnnotations ? "Annotate and confirm" : "Adjust and confirm";

    private QuickStartGuide.ConfirmationContext GetConfirmationGuideContext() => new(
        PinImageOnConfirm ? QuickStartGuide.ConfirmationPurpose.Pin
            : LockConfirmDestination ? QuickStartGuide.ConfirmationPurpose.Zoom
            : QuickStartGuide.ConfirmationPurpose.Capture,
        SupportsConfirmAnnotations, ShowAnnotationChrome, IsPreviewToggleVisible());

    private ContextMenuStrip BuildConfirmationOptionsMenu()
    {
        var settings = SettingsService.LoadStatic() ?? new AppSettings();
        var enabled = settings.EnabledTools ?? ToolDef.DefaultEnabledIds();
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: 240);
        menu.Font = UiChrome.ChromeFont(11);
        void Heading(string text)
        {
            menu.Items.Add(new ToolStripLabel(LocalizationService.Translate(text))
            {
                ForeColor = UiChrome.SurfaceTextMuted,
                Font = UiChrome.ChromeFont(9, FontStyle.Bold),
                Padding = new Padding(10, 8, 4, 4)
            });
        }
        ToolStripMenuItem Toggle(string name, string text, bool value, Action action)
        {
            var item = WindowsMenuRenderer.Item(LocalizationService.Translate(text), iconId: value ? "check" : null, iconSize: 24);
            item.Name = name;
            item.Click += (_, _) => { action(); menu.Close(); };
            menu.Items.Add(item);
            return item;
        }
        Heading("Confirmation options");
        Toggle("primary-label", "Show primary button text", _confirmDoneShowLabel, ToggleConfirmDoneLabel);
        if (!IsConfirmDestinationLocked)
            Toggle("mode-labels", "Show mode names", _confirmPillShowLabels, ToggleConfirmPillShowLabels);

        if (SupportsConfirmAnnotations)
        {
            menu.Items.Add(new ToolStripSeparator());
            Heading("Annotations");
            Toggle("annotation-toolbar", "Show annotation toolbar", _showAnnotationToolbar, ToggleAnnotationToolbar);
            Toggle("remember-annotation", "Remember last annotation tool", _rememberAnnotationTool, ToggleRememberAnnotationTool)
                .Enabled = _showAnnotationToolbar;
            var hidden = ToolDef.AllTools.Where(t => t.Group == 1 && !enabled.Contains(t.Id)).ToArray();
            if (_showAnnotationToolbar && hidden.Length > 0)
            {
                var restore = WindowsMenuRenderer.Item(LocalizationService.Translate("Restore hidden tools"), iconId: "add", iconSize: 24);
                restore.Name = "restore-annotations";
                restore.Click += (_, _) => { RestoreHiddenAnnotationTools(); menu.Close(); };
                menu.Items.Add(restore);
                Heading("Hidden annotation tools:");
                foreach (var tool in hidden)
                {
                    var item = WindowsMenuRenderer.Item(LocalizationService.Translate(tool.Label), iconId: "add", iconSize: 24);
                    item.Name = "restore-annotation:" + tool.Id;
                    item.Padding = new Padding(24, 0, 0, 0);
                    item.Click += (_, _) => { ShowTool(tool.Id); menu.Close(); };
                    menu.Items.Add(item);
                }
            }
        }
        menu.Items.Add(new ToolStripSeparator());
        Heading("Help");
        var guide = WindowsMenuRenderer.Item(LocalizationService.Translate(ConfirmationGuideTitle), iconId: "info", iconSize: 24);
        guide.Name = "confirmation-guide";
        guide.Click += (_, _) =>
        {
            menu.Close();
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && !Disposing && Visible)
                    ShowQuickStartGuideAt(_confirmOptionsPillRect);
            }));
        };
        menu.Items.Add(guide);
        WindowsMenuRenderer.NormalizeItemWidths(menu, 240, itemHeight: 46);
        return menu;
    }

    private void ShowConfirmationOptionsMenu(Point clickLocation)
    {
        var menu = BuildConfirmationOptionsMenu();
        _toolbarContextMenu = menu;
        menu.Closed += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.AppClicked)
            {
                // Same click-away contract as the widget: native close runs before
                // the underlying mouse event. Clear at background priority after input.
                _suppressConfirmOptionsMouseDown = true;
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => _suppressConfirmOptionsMouseDown = false));
            }
            _lastContextMenuClosedTime = DateTime.UtcNow;
            _lastContextMenuBtnIndex = -1;
            if (ReferenceEquals(_toolbarContextMenu, menu)) _toolbarContextMenu = null;
        };
        var anchor = _confirmOptionsPillRect.IsEmpty ? clickLocation
            : new Point(_confirmOptionsPillRect.Right, _confirmOptionsPillRect.Top);
        menu.Show(PointToScreen(anchor));
    }

    private void ActivateConfirmFrameMove()
    {
        _suppressAnnotationToolMemory = true;
        try { SetMode(CaptureMode.Move, "select", showHelpBanner: false); }
        finally { _suppressAnnotationToolMemory = false; }
        _activeToolId = null;
    }

    private void ToggleAnnotationToolbar()
    {
        _showAnnotationToolbar = !_showAnnotationToolbar;
        CloseAltToolPopup(invalidate: false);
        DismissQuickStartGuide();
        if (!_showAnnotationToolbar)
        {
            HideToolbarImmediately();
            ActivateConfirmFrameMove();
        }
        else
        {
            // A legacy bulk-hide may have left no enabled annotation tools at all.
            if (!_visibleTools.Any(t => t.Group == 1)) RestoreHiddenAnnotationTools();
            RefreshToolbar();
            TryRestoreLastAnnotationTool();
            EnsureDefaultAnnotationTool();
        }
        ShowAnnotationToolbarChanged?.Invoke(_showAnnotationToolbar);
        _confirmChromeLayoutDirty = true;
        RefreshToolbar();
        LayoutConfirmChromeRects();
        Invalidate();
    }

    private void RestoreHiddenAnnotationTools()
    {
        var settings = SettingsService.LoadStatic() ?? new AppSettings();
        var enabled = (settings.EnabledTools ?? ToolDef.DefaultEnabledIds()).ToList();
        foreach (var tool in ToolDef.AllTools.Where(t => t.Group == 1))
            if (!enabled.Contains(tool.Id)) enabled.Add(tool.Id);
        EnabledToolsChanged?.Invoke(enabled);
        SetEnabledTools(enabled);
        EnsureDefaultAnnotationTool();
        RefreshToolbar();
    }
}
