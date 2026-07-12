using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Button = System.Windows.Controls.Button;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using GiveFeedbackEventArgs = System.Windows.GiveFeedbackEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    // Preview button Borders keyed by kind, for drag-over eviction preview.
    private Dictionary<ToastButtonKind, Border> _previewButtonBorders = null!;

    // Button temporarily dimmed during drag-over so the user sees which occupant
    // would be evicted on drop. Restored on DragLeave.
    private Border? _dragDimmedButton;

    // Drag is armed on mouse-down and only started once the pointer crosses the system drag
    // threshold in PreviewMouseMove — starting DoDragDrop straight from mouse-down is unreliable
    // (the focus-activation click swallows it).
    private Point _toastDragStart;
    private Border? _toastDragSource;
    private ToastButtonKind _toastDragKind;
    private bool _toastDragArmed;

    // Transient message (e.g. "corner is full") shown in place of the preset name until the next action.
    private string? _toastLayoutHint;

    private AppSettings.ToastButtonLayoutSettings ToastButtons
    {
        get
        {
            _settingsService.Settings.ToastButtons ??= new AppSettings.ToastButtonLayoutSettings();
            return _settingsService.Settings.ToastButtons;
        }
    }

    private void LoadToastButtonLayoutDesigner()
    {
        _previewButtonBorders = new()
        {
            [ToastButtonKind.Close] = ToastLayoutCloseBtn,
            [ToastButtonKind.Pin] = ToastLayoutPinBtn,
            [ToastButtonKind.Save] = ToastLayoutSaveBtn,
            [ToastButtonKind.Copy] = ToastLayoutCopyBtn,
            [ToastButtonKind.Office] = ToastLayoutOfficeBtn,
            [ToastButtonKind.Delete] = ToastLayoutDeleteBtn,
            [ToastButtonKind.History] = ToastLayoutAiRedirectBtn,
            [ToastButtonKind.Edit] = ToastLayoutEditBtn,
        };

        BuildToastButtonRows();
        WireToastPreviewButtonGestures();
        RefreshToastButtonLayoutDesigner();
    }

    // Each preview button can be dragged to move it, double-clicked to send it back to the list,
    // or right-clicked / opened with the Menu key for a context menu (Move to… + Remove) that
    // replaces the old per-row placement combo and keeps the designer keyboard-accessible.
    private void WireToastPreviewButtonGestures()
    {
        foreach (var (kind, border) in _previewButtonBorders)
        {
            var captured = kind;

            // Right-click and the keyboard Menu/Shift+F10 key both open the placement menu;
            // it is rebuilt on each open so corner-occupancy and visibility stay current.
            border.Focusable = true;
            border.ContextMenuOpening += (_, e) =>
            {
                border.ContextMenu = BuildToastButtonContextMenu(captured);
                if (border.ContextMenu is not null)
                    border.ContextMenu.PlacementTarget = border;
            };

            string label = FormatToastButtonLabel(kind);
            string help = string.Format(
                Services.LocalizationService.Translate("Drag to move the {0} button. Double-click to remove it, or right-click (or press the Menu key) for placement options."),
                label);
            ToolTipService.SetInitialShowDelay(border, 300);
            AutomationProperties.SetHelpText(border, help);
        }
    }

    private void BuildToastButtonRows()
    {
        ToastButtonRows.Children.Clear();

        foreach (var kind in ToastButtonLayout.AllButtons)
            ToastButtonRows.Children.Add(BuildToastButtonRow(kind));
    }

    private Border BuildToastButtonRow(ToastButtonKind kind)
    {
        var label = FormatToastButtonLabel(kind);

        // Icon + name only: placement is done by dragging the row onto the preview, or via the
        // right-click / Menu-key context menu (Move to…). No per-row combo any more.
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Image
        {
            Source = Helpers.FluentIcons.RenderWpf(ToastButtonIconId(kind), GetToastLayoutIconColor(active: false), 20),
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 10, 0)
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(icon, System.Windows.Media.BitmapScalingMode.HighQuality);
        Grid.SetColumn(icon, 0);
        grid.Children.Add(icon);

        var desc = GetToastButtonDescription(kind);
        if (desc is not null)
        {
            icon.ToolTip = desc;
            ToolTipService.SetInitialShowDelay(icon, 200);
        }

        var name = new TextBlock
        {
            Text = ToTitleCase(label),
            FontSize = 13,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Foreground = Theme.Brush(Theme.IsDark ? Color.FromRgb(232, 232, 232) : Color.FromRgb(24, 24, 24))
        };
        if (desc is not null)
        {
            name.ToolTip = desc;
            ToolTipService.SetInitialShowDelay(name, 200);
        }
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        // The whole row is a drag handle for adding this button to the preview (the icon stays in
        // the list; a ghost follows the cursor). Right-click / Menu key opens the same placement
        // menu as the preview buttons so the layout stays fully keyboard-accessible.
        var rowTooltip = Services.LocalizationService.Translate("Drag onto the preview to add this button, or right-click (Menu key) to place it by keyboard.");
        var row = new Border
        {
            Child = grid,
            Tag = kind,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
            Focusable = true,
            ToolTip = rowTooltip
        };
        ToolTipService.SetInitialShowDelay(row, 300);
        AutomationProperties.SetName(row, $"{ToTitleCase(label)} button");
        AutomationProperties.SetHelpText(row, string.Format(
            Services.LocalizationService.Translate("Drag onto the preview to add the {0} button, or right-click (or press the Menu key) to place it in a corner."),
            label));
        row.PreviewMouseLeftButtonDown += ToastRow_PreviewMouseLeftButtonDown;
        row.PreviewMouseMove += ToastRow_PreviewMouseMove;
        row.ContextMenuOpening += (_, _) =>
        {
            row.ContextMenu = BuildToastButtonContextMenu(kind);
            if (row.ContextMenu is not null)
                row.ContextMenu.PlacementTarget = row;
        };
        return row;
    }

    /// <summary>Build the placement context menu for a button (shared by the preview buttons and
    /// the list rows). "Move to…" corner items reuse <see cref="ToastButtonLayout.AssignCorner"/>
    /// just as the old combo did; a full corner is disabled, and the current corner is checked.
    /// A final "Remove" item sends the button back to the list. Rebuilt on each open so the state
    /// is always current.</summary>
    private ContextMenu BuildToastButtonContextMenu(ToastButtonKind kind)
    {
        var label = FormatToastButtonLabel(kind);
        bool visible = ToastButtonLayout.IsVisible(ToastButtons, kind);
        ToastCorner? currentCorner = visible ? ToastButtonLayout.GetCorner(ToastButtons, kind) : null;

        var menu = new ContextMenu();

        var header = new MenuItem
        {
            Header = Services.LocalizationService.Translate("Move to"),
            IsEnabled = false
        };
        menu.Items.Add(header);

        foreach (var corner in new[] { ToastCorner.TopLeft, ToastCorner.TopRight, ToastCorner.BottomLeft, ToastCorner.BottomRight })
        {
            var capturedCorner = corner;
            bool isCurrent = visible && currentCorner == corner;
            var item = new MenuItem
            {
                Header = FormatCornerMenuLabel(corner),
                IsCheckable = true,
                IsChecked = isCurrent,
                // A full corner can't take this button — unless the button already lives there.
                IsEnabled = isCurrent || !IsCornerFull(ToastButtons, corner, kind)
            };
            item.Click += (_, _) => MoveToastButtonToCorner(kind, capturedCorner);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());

        var remove = new MenuItem
        {
            Header = Services.LocalizationService.Translate("Remove from notification"),
            IsEnabled = visible
        };
        remove.Click += (_, _) => RemoveToastButton(kind);
        menu.Items.Add(remove);

        return menu;
    }

    // Place (or move) a button into a corner from the context menu — the keyboard-accessible
    // equivalent of dragging it there. Mirrors the old combo's assign-or-warn behaviour.
    private void MoveToastButtonToCorner(ToastButtonKind kind, ToastCorner corner)
    {
        if (ToastButtonLayout.AssignCorner(ToastButtons, kind, corner))
        {
            ToastButtons.Manual = true;
            _toastLayoutHint = null;
            PersistToastButtonLayout();
            return;
        }

        _toastLayoutHint = string.Format(
            Services.LocalizationService.Translate("The {0} corner is full (2 buttons max). Move another button out first."),
            FormatCornerMenuLabel(corner));
        PersistToastButtonLayout();
    }

    /// <summary>Whether the per-button placement controls should be interactive.
    /// True when the user has explicitly opted into Manual mode, or when the current
    /// layout doesn't match any preset (legacy custom layout from before the Manual flag).
    /// </summary>
    private bool IsManualMode
        => ToastButtons.Manual || ToastButtonLayout.DetectPreset(ToastButtons) == null;

    private void ToastPreviewButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string tag } border)
            return;
        if (!Enum.TryParse<ToastButtonKind>(tag, out var kind))
            return;

        // Double-click sends the button back to the list (right-click now opens the placement menu).
        if (e.ClickCount == 2)
        {
            RemoveToastButton(kind);
            e.Handled = true;
            return;
        }

        // Arm the drag but don't start it here. Calling DragDrop.DoDragDrop directly from the
        // mouse-down handler is flaky: the first click on a not-yet-focused control is consumed
        // by focus activation and the drag silently fails to begin (the "some buttons won't drag
        // until you've clicked around a bit" symptom). We wait for real movement instead.
        _toastDragSource = border;
        _toastDragKind = kind;
        _toastDragStart = e.GetPosition(ToastDesignerRoot);
        _toastDragArmed = true;
    }

    private void ToastPreviewSurface_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_toastDragArmed || e.LeftButton != MouseButtonState.Pressed)
        {
            _toastDragArmed = false;
            return;
        }

        var pos = e.GetPosition(ToastDesignerRoot);
        if (Math.Abs(pos.X - _toastDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _toastDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _toastDragArmed = false;
        StartToastDrag();
    }

    // Drag a button in from the list. The row's icon/label act as the grab handle.
    private void ToastRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ToastButtonKind kind })
            return;

        _toastDragSource = null;          // null source marks a drag originating from the list
        _toastDragKind = kind;
        _toastDragStart = e.GetPosition(ToastDesignerRoot);
        _toastDragArmed = true;
    }

    private void ToastRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Only handle list-originated drags here (source == null); preview-button drags are armed
        // and started by the preview surface handler.
        if (!_toastDragArmed || _toastDragSource is not null)
            return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _toastDragArmed = false;
            return;
        }

        var pos = e.GetPosition(ToastDesignerRoot);
        if (Math.Abs(pos.X - _toastDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _toastDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _toastDragArmed = false;
        StartToastDrag();
    }

    private void StartToastDrag()
    {
        var kind = _toastDragKind;

        RestoreDimmedButton();

        // The element that owns DoDragDrop/GiveFeedback: the preview button border when moving an
        // existing button, or the whole designer surface when dragging a button in from the list.
        UIElement dragSource = (UIElement?)_toastDragSource ?? ToastDesignerRoot;

        double savedOpacity = 1;
        if (_toastDragSource is { } border)
        {
            // Snapshot the button at full opacity for the floating ghost, then hide the original so
            // only the ghost follows the cursor.
            ShowToastDragGhost(border);
            savedOpacity = border.Opacity;
            border.Opacity = 0;
        }
        else
        {
            // Dragging in from the list: build a ghost that matches the preview buttons. The list
            // row itself stays put.
            ShowToastDragGhostForKind(kind);
        }

        // Preview buttons are drawn directly on top of the slot drop targets that share their
        // corner (same margins from ToastButtonLayout.ToPlacement). Drag-drop routed events
        // bubble from the hit-test element up through its ancestors, and a slot is a *sibling*
        // of the button covering it — never an ancestor — so while a button sits on top, the
        // slot beneath never receives DragEnter/Drop and the drop silently does nothing.
        // Make every preview button transparent to hit-testing for the duration of the drag so
        // the slots underneath (always present, even when occupied) get the events. Restored
        // in the finally block. The DoDragDrop source reference stays valid regardless.
        foreach (var b in _previewButtonBorders.Values)
            b.IsHitTestVisible = false;

        // Suppress default drag cursors (arrow, "no" circle).
        dragSource.GiveFeedback += OnGiveFeedback;
        try
        {
            DragDrop.DoDragDrop(dragSource, kind, DragDropEffects.Move);
        }
        finally
        {
            dragSource.GiveFeedback -= OnGiveFeedback;
            if (_toastDragSource is { } b2)
                b2.Opacity = savedOpacity;
            foreach (var b in _previewButtonBorders.Values)
                b.IsHitTestVisible = true;
            HideToastDragGhost();
            RestoreDimmedButton();
            RefreshToastSlotIndicators();
            _toastDragSource = null;
        }
    }

    private void ShowToastDragGhost(Border source)
    {
        double w = source.ActualWidth, h = source.ActualHeight;
        if (w < 1 || h < 1)
            return;

        // Render through a VisualBrush rather than rtb.Render(source) directly: RenderTargetBitmap
        // bakes in the element's layout offset (the buttons carry margins of 8–48px from
        // ToPlacement), which would push the content outside the bitmap and leave the ghost blank.
        // A VisualBrush paints the visual's own content from (0,0), sidestepping the offset.
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var brush = new VisualBrush(source) { Stretch = Stretch.None };
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        }

        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        ToastDragGhost.Source = rtb;
        ToastDragGhost.Visibility = Visibility.Visible;

        // _toastDragStart and the ghost both live in ToastDesignerRoot space.
        PositionToastDragGhost(_toastDragStart);
    }

    // Build the floating ghost for a button dragged in from the list, matching the look of the
    // preview buttons (30px rounded chrome + 22px icon) so the drag feels consistent regardless
    // of whether it started on the small list icon or a preview button.
    private void ShowToastDragGhostForKind(ToastButtonKind kind)
    {
        const int size = 30;
        var iconBmp = Helpers.FluentIcons.RenderWpf(ToastButtonIconId(kind), GetToastLayoutIconColor(active: false), 22);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = Theme.Brush(Theme.IsDark ? Color.FromArgb(64, 0, 0, 0) : Color.FromArgb(40, 0, 0, 0));
            dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, size, size), 9, 9);
            if (iconBmp is not null)
            {
                double iw = iconBmp.Width, ih = iconBmp.Height;
                dc.DrawImage(iconBmp, new Rect((size - iw) / 2, (size - ih) / 2, iw, ih));
            }
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        ToastDragGhost.Source = rtb;
        ToastDragGhost.Visibility = Visibility.Visible;
        PositionToastDragGhost(_toastDragStart);
    }

    private void PositionToastDragGhost(Point posInRoot)
        => ToastDragGhost.Margin = new Thickness(
            posInRoot.X - ToastDragGhost.Width / 2,
            posInRoot.Y - ToastDragGhost.Height / 2, 0, 0);

    private void HideToastDragGhost()
    {
        ToastDragGhost.Visibility = Visibility.Collapsed;
        ToastDragGhost.Source = null;
    }

    private static void OnGiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        e.UseDefaultCursors = false;
        e.Handled = true;
    }

    /// <summary>Accept Move effects anywhere on the designer surface so the cursor never shows the
    /// "no" symbol, and keep the floating ghost glued to the cursor across both the preview and the
    /// list.</summary>
    private void ToastDesigner_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ToastButtonKind)))
        {
            e.Effects = DragDropEffects.Move;
            if (ToastDragGhost.Visibility == Visibility.Visible)
                PositionToastDragGhost(e.GetPosition(ToastDesignerRoot));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void RestoreDimmedButton()
    {
        if (_dragDimmedButton is not null)
        {
            _dragDimmedButton.Opacity = 1;
            _dragDimmedButton = null;
        }
    }

    // Send a preview button back to the list (double-click or right-click on it).
    private void RemoveToastButton(ToastButtonKind kind)
    {
        if (!ToastButtonLayout.IsVisible(ToastButtons, kind))
            return;

        ToastButtonLayout.SetVisible(ToastButtons, kind, false);
        ToastButtons.Manual = true;
        _toastLayoutHint = null;
        PersistToastButtonLayout();
    }

    private void ToastSlot_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ToastButtonKind)) || sender is not Border slot)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        slot.Opacity = 0.50;
        slot.BorderThickness = new Thickness(2);

        // Preview the swap: if this exact slot holds a visible button, dim it so the user sees
        // which button will move out of the way on drop.
        if (e.Data.GetData(typeof(ToastButtonKind)) is ToastButtonKind draggedKind
            && slot.Tag is string slotTag
            && Enum.TryParse<ToastButtonSlot>(slotTag, out var targetSlot))
        {
            var occupant = FindVisibleButtonAtSlot(targetSlot, draggedKind);
            if (occupant.HasValue && _previewButtonBorders.TryGetValue(occupant.Value, out var occupantBorder))
            {
                // Restore any previously-dimmed button before dimming a new one.
                RestoreDimmedButton();
                _dragDimmedButton = occupantBorder;
                occupantBorder.Opacity = 0.20;
            }
        }
    }

    private void ToastSlot_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border slot)
        {
            slot.Opacity = 0.22;
            slot.BorderThickness = new Thickness(1);
            e.Handled = true;
        }
    }

    private void ToastSlot_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (sender is not Border { Tag: string slotTag } slot)
            return;
        if (!e.Data.GetDataPresent(typeof(ToastButtonKind)))
            return;
        if (e.Data.GetData(typeof(ToastButtonKind)) is not ToastButtonKind kind)
            return;
        if (!Enum.TryParse<ToastButtonSlot>(slotTag, out var targetSlot))
            return;

        // Reset visual state. Restore the dimmed occupant's opacity — it isn't hidden, it swaps
        // into the dragged button's old slot, so it must not stay stuck at the 0.20 preview
        // opacity (the refresh below re-renders it at its new position regardless).
        slot.Opacity = 0.22;
        slot.BorderThickness = new Thickness(1);
        RestoreDimmedButton();

        // Drop onto the *exact* slot, not just the corner.
        if (!ToastButtonLayout.IsVisible(ToastButtons, kind))
        {
            // A hidden kind can only have come from the list (drag-in). Place it without the
            // stale-slot swap AssignSlot does for an already-visible button: fill the corner,
            // pushing any occupant to the free partner slot, or reject when the corner is full.
            if (!ToastButtonLayout.PlaceFromHidden(ToastButtons, kind, targetSlot))
            {
                _toastLayoutHint = string.Format(
                    Services.LocalizationService.Translate("The {0} corner is full (2 buttons max). Remove a button first."),
                    FormatCornerMenuLabel(ToastButtonLayout.SlotToCorner(targetSlot)));
                PersistToastButtonLayout();
                return;
            }
        }
        else
        {
            // AssignSlot swaps with the visible occupant (if any), which is what lets a button move
            // to the free or occupied slot right next to it within the same corner.
            ToastButtonLayout.AssignSlot(ToastButtons, kind, targetSlot);
        }

        // A drag-and-drop always implies the user is customising manually — activate Manual mode
        // so the combo boxes stay unlocked after the drop.
        ToastButtons.Manual = true;

        _toastLayoutHint = null;
        PersistToastButtonLayout();
    }

    /// <summary>Return the visible button (other than <paramref name="keep"/>) occupying the
    /// given exact slot, or null when the slot is free.</summary>
    private ToastButtonKind? FindVisibleButtonAtSlot(ToastButtonSlot slot, ToastButtonKind keep)
    {
        foreach (var btn in ToastButtonLayout.AllButtons)
        {
            if (btn == keep) continue;
            if (!ToastButtonLayout.IsVisible(ToastButtons, btn)) continue;
            if (ToastButtonLayout.GetSlot(ToastButtons, btn) == slot)
                return btn;
        }
        return null;
    }

    private void ToastPresetNoneBtn_Click(object sender, RoutedEventArgs e) => ApplyToastPreset(ToastButtonPreset.None);
    private void ToastPresetStandardBtn_Click(object sender, RoutedEventArgs e) => ApplyToastPreset(ToastButtonPreset.Standard);
    private void ToastPresetFullBtn_Click(object sender, RoutedEventArgs e) => ApplyToastPreset(ToastButtonPreset.Full);

    private void ToastManualBtn_Click(object sender, RoutedEventArgs e)
    {
        // Activate Manual mode without changing the current layout.
        ToastButtons.Manual = true;
        _toastLayoutHint = null;
        PersistToastButtonLayout();
    }

    private void ApplyToastPreset(ToastButtonPreset preset)
    {
        ToastButtonLayout.ApplyPreset(ToastButtons, preset);
        // Returning to a preset turns off Manual mode: the combo boxes lock again.
        ToastButtons.Manual = false;
        _toastLayoutHint = null;
        PersistToastButtonLayout();
    }

    private void ResetToastButtonsBtn_Click(object sender, RoutedEventArgs e)
    {
        // "Default" restores the shipping layout AND the preview-body click action together —
        // they form one notification interaction surface.
        _settingsService.Settings.ToastButtons = new AppSettings.ToastButtonLayoutSettings();
        _settingsService.Settings.ToastPreviewClickAction = ToastPreviewClickAction.OpenInEditor;
        _toastLayoutHint = null;
        _suppressToastPreferenceChange = true;
        try
        {
            if (ToastPreviewClickActionCombo is not null)
                ToastPreviewClickActionCombo.SelectedIndex = (int)ToastPreviewClickAction.OpenInEditor;
        }
        finally
        {
            _suppressToastPreferenceChange = false;
        }
        PersistToastButtonLayout();
    }

    private void RefreshToastButtonLayoutDesigner()
    {
        RefreshToastPreview();
        RefreshToastSlotIndicators();
        RefreshToastPresetButtons();
        RefreshToastLayoutStatus();
        RefreshEditorPreviewState();
    }

    private void RefreshToastPreview()
    {
        // Collapse all buttons first so no stale positions linger.
        CollapseAllToastPreviewButtons();

        UpdateToastPreviewButton(ToastLayoutCloseBtn, ToastLayoutCloseIcon, "close", ToastButtonKind.Close);
        UpdateToastPreviewButton(ToastLayoutPinBtn, ToastLayoutPinIcon, "pin", ToastButtonKind.Pin);
        UpdateToastPreviewButton(ToastLayoutSaveBtn, ToastLayoutSaveIcon, "download", ToastButtonKind.Save);
        UpdateToastPreviewButton(ToastLayoutCopyBtn, ToastLayoutCopyIcon, "copy", ToastButtonKind.Copy);
        UpdateToastPreviewButton(ToastLayoutOfficeBtn, ToastLayoutOfficeIcon, "arrow", ToastButtonKind.Office);
        UpdateToastPreviewButton(ToastLayoutDeleteBtn, ToastLayoutDeleteIcon, "trash", ToastButtonKind.Delete);
        UpdateToastPreviewButton(ToastLayoutAiRedirectBtn, ToastLayoutAiRedirectIcon, "history", ToastButtonKind.History);
        UpdateToastPreviewButton(ToastLayoutEditBtn, ToastLayoutEditIcon, "draw", ToastButtonKind.Edit);
        RefreshToastActionsPanelMetrics();
    }

    private void RefreshToastActionsPanelMetrics()
    {
        int maxRow = 0;
        foreach (var btn in ToastButtonLayout.AllButtons)
        {
            if (!ToastButtonLayout.IsVisible(ToastButtons, btn))
                continue;

            var (row, _) = ToastButtonLayout.ToGridCell(ToastButtonLayout.GetSlot(ToastButtons, btn));
            maxRow = Math.Max(maxRow, row + 1);
        }

        const double rowHeight = 36;
        ToastLayoutActionsPanel.RowDefinitions[0].Height = maxRow >= 1 ? new GridLength(rowHeight) : new GridLength(0);
        ToastLayoutActionsPanel.RowDefinitions[1].Height = maxRow >= 2 ? new GridLength(rowHeight) : new GridLength(0);
        ToastLayoutActionsPanel.Height = maxRow > 0 ? maxRow * rowHeight : 0;
    }

    private void CollapseAllToastPreviewButtons()
    {
        ToastLayoutCloseBtn.Visibility = Visibility.Collapsed;
        ToastLayoutPinBtn.Visibility = Visibility.Collapsed;
        ToastLayoutSaveBtn.Visibility = Visibility.Collapsed;
        ToastLayoutCopyBtn.Visibility = Visibility.Collapsed;
        ToastLayoutOfficeBtn.Visibility = Visibility.Collapsed;
        ToastLayoutDeleteBtn.Visibility = Visibility.Collapsed;
        ToastLayoutAiRedirectBtn.Visibility = Visibility.Collapsed;
        ToastLayoutEditBtn.Visibility = Visibility.Collapsed;
    }

    private void UpdateToastPreviewButton(Border border, Image icon, string iconId, ToastButtonKind kind)
    {
        bool visible = ToastButtonLayout.IsVisible(ToastButtons, kind);
        border.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;

        var slot = ToastButtonLayout.GetSlot(ToastButtons, kind);
        var (row, col) = ToastButtonLayout.ToGridCell(slot);
        Grid.SetColumn(border, col);
        Grid.SetRow(border, row);
        border.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        border.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        border.Margin = new Thickness(0);
        // Always reset opacity: a drag may have left this button dimmed (drag source at 0.30,
        // or an eviction-preview occupant at 0.20 that ended up not evicted). Without this,
        // the stale transparency persists across refreshes and presets.
        border.Opacity = 1;
        // Default Dark only: Settings chrome. Grayscale/Light keep prior teal/blue button fills.
        if (Theme.IsDark && !Theme.IsGray)
        {
            border.Background = Theme.Brush(Theme.BgSecondary);
            border.BorderBrush = Theme.Brush(Theme.BorderSubtle);
        }
        else if (Theme.IsGray)
        {
            border.Background = Theme.Brush(Color.FromArgb(215, 24, 26, 29));
            border.BorderBrush = Theme.Brush(Color.FromArgb(130, 184, 190, 198));
        }
        else
        {
            border.Background = Theme.Brush(Color.FromArgb(210, 235, 246, 253));
            border.BorderBrush = Theme.Brush(Color.FromArgb(130, 0, 120, 215));
        }
        border.BorderThickness = new System.Windows.Thickness(1);
        icon.Source = Helpers.FluentIcons.RenderWpf(iconId, GetToastLayoutIconColor(active: false), 22);

        // Don't override tooltip - let XAML localization handle it
    }

    private void RefreshToastSlotIndicators()
    {
        // Map every ToastButtonSlot to its named preview Border so we can show
        // which positions are occupied vs. available.
        var slotBorders = new (ToastButtonSlot slot, Border border)[]
        {
            (ToastButtonSlot.TopLeft, ToastLayoutSlotTopLeft),
            (ToastButtonSlot.TopInnerLeft, ToastLayoutSlotTopInnerLeft),
            (ToastButtonSlot.TopRight, ToastLayoutSlotTopRight),
            (ToastButtonSlot.TopInnerRight, ToastLayoutSlotTopInnerRight),
            (ToastButtonSlot.BottomLeft, ToastLayoutSlotBottomLeft),
            (ToastButtonSlot.BottomInnerLeft, ToastLayoutSlotBottomInnerLeft),
            (ToastButtonSlot.BottomRight, ToastLayoutSlotBottomRight),
            (ToastButtonSlot.BottomInnerRight, ToastLayoutSlotBottomInnerRight),
        };

        foreach (var (slot, border) in slotBorders)
        {
            // A slot is occupied when any visible button sits there (not just the first
            // match from FindButtonAt, since two buttons can share the same slot).
            bool occupied = false;
            foreach (var btn in ToastButtonLayout.AllButtons)
            {
                if (ToastButtonLayout.IsVisible(ToastButtons, btn)
                    && ToastButtonLayout.GetSlot(ToastButtons, btn) == slot)
                {
                    occupied = true;
                    break;
                }
            }

            // Keep ghosts always in the tree so they remain valid drop targets even
            // when occupied. Opacity 0 makes them invisible while still hit-testable
            // (unlike Visibility.Collapsed). The real button renders on top.
            border.Visibility = Visibility.Visible;
            border.Opacity = occupied ? 0 : 0.22;
        }
    }

    private static bool IsCornerFull(AppSettings.ToastButtonLayoutSettings settings, ToastCorner corner, ToastButtonKind exclude)
    {
        var (outer, inner) = ToastButtonLayout.CornerSlots(corner);
        bool outerOccupied = false, innerOccupied = false;
        foreach (var btn in ToastButtonLayout.AllButtons)
        {
            if (btn == exclude)
                continue;
            if (!ToastButtonLayout.IsVisible(settings, btn))
                continue;
            var slot = ToastButtonLayout.GetSlot(settings, btn);
            if (slot == outer) outerOccupied = true;
            if (slot == inner) innerOccupied = true;
        }
        return outerOccupied && innerOccupied;
    }

    private void RefreshToastPresetButtons()
    {
        var active = ToastButtonLayout.DetectPreset(ToastButtons);
        bool manualActive = IsManualMode;

        // Preset buttons are highlighted when their preset is active AND Manual mode is off.
        // Minimal was removed from the toolbar (redundant with No buttons); layouts that still
        // match only-close simply leave every preset chip unhighlighted.
        HighlightToastPreset(ToastPresetNoneBtn, active == ToastButtonPreset.None && !manualActive);
        HighlightToastPreset(ToastPresetStandardBtn, active == ToastButtonPreset.Standard && !manualActive);
        HighlightToastPreset(ToastPresetFullBtn, active == ToastButtonPreset.Full && !manualActive);
        // Manual button is highlighted whenever Manual mode is active.
        HighlightToastPreset(ToastManualBtn, manualActive);
    }

    private static void HighlightToastPreset(Button button, bool active)
    {
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        button.BorderBrush = active
            ? Theme.Brush(Theme.IsDark ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0))
            : Theme.Brush(Theme.BorderSubtle);
        button.BorderThickness = new Thickness(active ? 2 : 1);
    }

    private void RefreshToastLayoutStatus()
    {
        // The preset buttons already make the active selection obvious, so this line stays hidden
        // and only surfaces a transient warning (e.g. a corner being full).
        if (_toastLayoutHint is not null)
        {
            ToastLayoutSelectionText.Text = _toastLayoutHint;
            ToastLayoutSelectionText.ToolTip = _toastLayoutHint;
            AutomationProperties.SetHelpText(ToastLayoutSelectionText, _toastLayoutHint);
            ToastLayoutSelectionText.Visibility = Visibility.Visible;
        }
        else
        {
            ToastLayoutSelectionText.Text = string.Empty;
            ToastLayoutSelectionText.Visibility = Visibility.Collapsed;
        }
    }

    private void PersistToastButtonLayout()
    {
        _settingsService.Save();
        ToastWindow.SetButtonLayout(ToastButtons);
        RefreshToastButtonLayoutDesigner();
    }

    /// <summary>
    /// Side-by-side mocks: left = fixed system alert (brief status when Send to Editor is on);
    /// right = customizable capture notification (preview + buttons). Captions and guide use
    /// that vocabulary so newcomers don't confuse media type with notification type.
    /// </summary>
    private void RefreshEditorPreviewState()
    {
        // Guard: this runs from theme refreshes that can fire before the designer is built.
        if (EditorToastMockShell is null || EditorButtonsCard is null)
            return;

        // Two related-but-separate settings: images → editor, video/GIF → trimmer.
        // The widget toggle often sets both; Settings can set them independently.
        bool editorOn = _settingsService.Settings.OpenEditorAfterCapture;
        bool trimmerOn = _settingsService.Settings.OpenVideoTrimmerAfterCapture;
        // Video/GIF "success" path skips the designed preview toast when the trimmer opens
        // (only an encoding system wait toast). Images skip it when the editor opens.
        bool designedToastForImages = !editorOn;
        bool designedToastForVideo = !trimmerOn;
        bool videoOnlyOnRight = editorOn && designedToastForVideo;
        bool showVideoBadge = videoOnlyOnRight; // badge when the mock is video/GIF-only

        // Keep the in-panel editor toggle reflecting the setting. Setting IsChecked to the same
        // value is a no-op; a real change makes the handler re-enter, but it early-outs once the
        // setting already matches, so there's no loop.
        if (NotificationsEditorToggle is not null && NotificationsEditorToggle.IsChecked != editorOn)
            NotificationsEditorToggle.IsChecked = editorOn;

        // Keep both previews faithful to the real notification shell + edge stroke.
        EditorToastMockShell.Background = Theme.Brush(Theme.ToastBg);
        EditorToastMockShell.BorderBrush = ToastAccentStroke();
        if (EditorToastMockCloseIcon is not null)
            EditorToastMockCloseIcon.Source = Helpers.FluentIcons.RenderWpf("close", GetToastLayoutIconColor(active: false), 16);

        // The big designer shell shares the same look so the two previews read as the same object.
        if (ToastLayoutShell is not null)
        {
            ToastLayoutShell.Background = Theme.Brush(Theme.ToastBg);
            ToastLayoutShell.BorderBrush = ToastAccentStroke();
        }

        // Image placeholder. Default Dark: Settings BgSecondary well. Gray/light: prior nudges.
        if (ToastLayoutImageArea is not null)
        {
            ToastLayoutImageArea.Background = Theme.Brush(Theme.IsGray
                ? Color.FromRgb(0x1E, 0x20, 0x23)
                : Theme.IsDark
                ? Theme.BgSecondary
                : Color.FromRgb(0xE2, 0xE5, 0xED));
        }
        if (ToastLayoutImageGlyph is not null)
        {
            // In video-only mock mode the play badge is enough — hide the photo glyph entirely.
            if (showVideoBadge)
            {
                ToastLayoutImageGlyph.Visibility = Visibility.Collapsed;
            }
            else
            {
                ToastLayoutImageGlyph.Visibility = Visibility.Visible;
                ToastLayoutImageGlyph.Foreground = Theme.Brush(Theme.IsDark
                    ? Color.FromRgb(255, 255, 255)
                    : Color.FromRgb(0, 0, 0));
                ToastLayoutImageGlyph.Opacity = Theme.IsDark ? 0.14 : 0.20;
            }
        }

        // Neutral play badge when the right mock is video/GIF-only (matches the live notification).
        UpdateCapturePreviewMediaBadge(videoMode: showVideoBadge);

        // Both mock timeline rails carry the cyber cyan/purple/magenta gradient hardcoded in XAML.
        // In grayscale, swap to the sober silver ramp so previews stay faithful.
        ApplyMockRail(EditorToastMockRail, EditorToastMockRailGlow);
        ApplyMockRail(ToastLayoutRail, ToastLayoutRailGlow);

        // Left: system-alert example only when images are sent to the editor.
        if (EditorToastMockShell is not null)
            EditorToastMockShell.Visibility = editorOn ? Visibility.Visible : Visibility.Collapsed;
        if (SystemAlertExampleLabel is not null)
            SystemAlertExampleLabel.Visibility = editorOn ? Visibility.Visible : Visibility.Collapsed;
        if (SystemAlertOffNote is not null)
            SystemAlertOffNote.Visibility = editorOn ? Visibility.Collapsed : Visibility.Visible;

        // Right: designed capture notification — used for whatever is NOT auto-sent to Editor/Trimmer.
        ApplyEditorPreviewEmphasis(EditorButtonsCard, active: designedToastForImages || designedToastForVideo);
        if (EditorPreviewOtherCaption is not null)
        {
            string captionKey =
                designedToastForImages && designedToastForVideo ? "Capture notification (all)" :
                designedToastForImages ? "Capture notification (images)" :
                designedToastForVideo ? "Capture notification (video and GIF)" :
                "Capture notification (when not opened automatically)";
            string caption = Services.LocalizationService.Translate(captionKey);
            EditorPreviewOtherCaption.Text = caption;
            AutomationProperties.SetName(EditorPreviewOtherCaption, caption);
        }

        if (EditorPreviewImagesCaption is not null)
        {
            string leftCaption = Services.LocalizationService.Translate("System alert");
            EditorPreviewImagesCaption.Text = leftCaption;
            AutomationProperties.SetName(EditorPreviewImagesCaption, leftCaption);
        }

        if (EditorPreviewGuide is not null)
        {
            string guide = Services.LocalizationService.Translate(BuildDesignerGuideKey(
                editorOn, trimmerOn, designedToastForImages, designedToastForVideo));
            EditorPreviewGuide.Text = guide;
            AutomationProperties.SetName(EditorPreviewGuide, guide);
        }
    }

    private static string BuildDesignerGuideKey(
        bool editorOn, bool trimmerOn, bool designedToastForImages, bool designedToastForVideo)
    {
        if (designedToastForImages && designedToastForVideo)
            return "Every capture uses the capture notification you design on the right (preview and actions). Turn on Send to Editor to open images in the editor and show the brief system alert on the left instead.";

        if (editorOn && trimmerOn)
            return "Images open in the editor (brief system alert on the left). Video and GIF open in the trimmer with an encoding wait alert. The design on the right is only used if those automatic opens are off or fail.";

        if (editorOn && designedToastForVideo)
            return "With Send to Editor on, image captures open in the editor and show only this brief system alert. Video and GIF still use the capture notification you design on the right (unless Send to Trimmer is also on in Video settings).";

        // Images use the designed toast; video goes to trimmer.
        return "Image captures use the notification you design on the right. Video and GIF open in the trimmer with an encoding wait alert (Send to Trimmer is on).";
    }

    private static void ApplyEditorPreviewEmphasis(UIElement card, bool active)
        => card.Opacity = active ? 1.0 : 0.40;

    /// <summary>
    /// When the designer represents video/GIF captures only, show a neutral play circle
    /// matching the real capture notification (not the Gallery card color language).
    /// </summary>
    private void UpdateCapturePreviewMediaBadge(bool videoMode)
    {
        if (ToastLayoutVideoBadge is null)
            return;

        ToastLayoutVideoBadge.Visibility = videoMode ? Visibility.Visible : Visibility.Collapsed;
        if (videoMode)
            ToastLayoutVideoBadge.Background = Theme.Brush(Color.FromArgb(180, 0, 0, 0));
    }

    // Outer stroke matching ToastWindow EdgeRing (default Dark solid cyan) or OuterShell rings.
    private static SolidColorBrush ToastAccentStroke()
        => Theme.Brush(Theme.IsGray
            ? Color.FromArgb(160, 184, 190, 198)
            : Theme.IsDark
            ? Theme.ToastBorder
            : Color.FromArgb(160, 0, 110, 205));

    // Paint a preview's bottom timeline rail to match the real toast's ProgressBar: the cyber
    // cyan/purple/magenta gradient in the CyberSnap/Light themes, or the sober silver ramp the real
    // toast swaps in under grayscale (the "Dark" radio). Mirrors ToastWindow's CreateSilverProgressBrush
    // and the ProgressBar gradient defined in ToastWindow.xaml so the previews stay faithful per theme.
    private static void ApplyMockRail(Border? rail, System.Windows.Media.Effects.DropShadowEffect? glow)
    {
        if (rail is null)
            return;

        if (Theme.IsGray)
        {
            // Static (non-looping) silver ramp: bright → mid → dim, matching CreateSilverProgressBrush.
            rail.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0xE6, 0xEA, 0xEF), 0.0),
                    new GradientStop(Color.FromRgb(0xB8, 0xBE, 0xC6), 0.5),
                    new GradientStop(Color.FromRgb(0x7C, 0x82, 0x8C), 1.0),
                }
            };
            if (glow is not null)
                glow.Color = Color.FromRgb(0xE8, 0xEC, 0xF0); // soft white-silver halo
        }
        else
        {
            rail.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                GradientStops = new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x00, 0xF2, 0xFF), 0.0), // Cyber Cyan
                    new GradientStop(Color.FromRgb(0x7A, 0x00, 0xFF), 0.5), // Cyber Purple
                    new GradientStop(Color.FromRgb(0xFF, 0x00, 0xD0), 1.0), // Cyber Magenta
                }
            };
            if (glow is not null)
                glow.Color = Color.FromRgb(0x7A, 0x00, 0xFF);
        }
    }

    // In-panel mirror of the widget's "Enable editor" toggle. Flipping it here updates the setting,
    // keeps the General-tab checkbox and the widget toggle in lockstep, and re-renders both
    // notification previews live so the user can see exactly how the editor state changes them.
    private void NotificationsEditorToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        bool enabled = NotificationsEditorToggle.IsChecked == true;
        if (_settingsService.Settings.OpenEditorAfterCapture == enabled)
            return; // already in sync (e.g. set programmatically by RefreshEditorPreviewState)

        _settingsService.Settings.OpenEditorAfterCapture = enabled;
        _settingsService.Save();
        RefreshEnableEditorCheck();                                 // General-tab checkbox
        ((App)Application.Current).SyncWidgetEnableEditorToggle();  // widget toggle
        RefreshEditorPreviewState();                               // both previews + this toggle
    }

    private static string? GetToastButtonDescription(ToastButtonKind button) => button switch
    {
        ToastButtonKind.Close => Services.LocalizationService.Translate("Close the notification preview."),
        ToastButtonKind.Pin => Services.LocalizationService.Translate("Keep the preview open until you dismiss it manually."),
        ToastButtonKind.Save => Services.LocalizationService.Translate("Save the captured image to a file."),
        ToastButtonKind.Copy => Services.LocalizationService.Translate("Copy capture to the clipboard."),
        ToastButtonKind.Office => Services.LocalizationService.Translate("Open the screenshot with another app (Word, PowerPoint, etc.)."),
        ToastButtonKind.Delete => Services.LocalizationService.Translate("Delete capture"),
        ToastButtonKind.History => Services.LocalizationService.Translate("Open capture in Gallery."),
        ToastButtonKind.Edit => Services.LocalizationService.Translate("Open this capture in the Annotations Editor."),
        _ => null
    };

    private static string FormatToastButtonLabel(ToastButtonKind button) => button switch
    {
        ToastButtonKind.Close => Services.LocalizationService.Translate("close"),
        ToastButtonKind.Pin => Services.LocalizationService.Translate("pin"),
        ToastButtonKind.Save => Services.LocalizationService.Translate("save"),
        ToastButtonKind.Copy => Services.LocalizationService.Translate("copy"),
        ToastButtonKind.Office => Services.LocalizationService.Translate("send to"),
        ToastButtonKind.Delete => Services.LocalizationService.Translate("delete"),
        ToastButtonKind.History => Services.LocalizationService.Translate("gallery"),
        ToastButtonKind.Edit => Services.LocalizationService.Translate("edit"),
        _ => Services.LocalizationService.Translate("notification")
    };

    private static string FormatCornerMenuLabel(ToastCorner corner) => corner switch
    {
        ToastCorner.TopLeft => Services.LocalizationService.Translate("Top Left"),
        ToastCorner.TopRight => Services.LocalizationService.Translate("Top Right"),
        ToastCorner.BottomLeft => Services.LocalizationService.Translate("Bottom Left"),
        _ => Services.LocalizationService.Translate("Bottom Right")
    };

    private static string ToastButtonIconId(ToastButtonKind button) => button switch
    {
        ToastButtonKind.Close => "close",
        ToastButtonKind.Pin => "pin",
        ToastButtonKind.Save => "download",
        ToastButtonKind.Copy => "copy",
        ToastButtonKind.Office => "arrow",
        ToastButtonKind.Delete => "trash",
        ToastButtonKind.History => "history",
        _ => "draw"
    };

    private static string ToTitleCase(string label)
        => string.IsNullOrEmpty(label) ? label : char.ToUpperInvariant(label[0]) + label[1..];

    private static System.Drawing.Color GetToastLayoutIconColor(bool active)
    {
        // Default Dark: Settings text/accent tones. Other themes keep prior white/black icons.
        if (Theme.IsDark && !Theme.IsGray)
        {
            var tone = active ? Theme.Accent : Theme.TextPrimary;
            byte a = active ? (byte)255 : (byte)220;
            return System.Drawing.Color.FromArgb(a, tone.R, tone.G, tone.B);
        }

        return Theme.IsDark
            ? System.Drawing.Color.FromArgb(active ? 255 : 220, 255, 255, 255)
            : System.Drawing.Color.FromArgb(active ? 255 : 210, 24, 24, 24);
    }
}
