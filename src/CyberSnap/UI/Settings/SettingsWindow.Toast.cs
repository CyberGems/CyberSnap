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

    // Fixed Cancel/Retry chrome: arm on press, flash red if the user tries to drag them.
    private Border? _fixedPillPressSource;
    private Point _fixedPillPressStart;
    private bool _fixedPillArmed;
    private System.Windows.Threading.DispatcherTimer? _fixedPillFlashTimer;

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
            [ToastButtonKind.Share] = ToastLayoutShareBtn,
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
                Services.LocalizationService.Translate("Drag to move the {0} pill. Double-click to remove it, or right-click (or press the Menu key) for placement options."),
                label);
            ToolTipService.SetInitialShowDelay(border, 300);
            AutomationProperties.SetHelpText(border, help);
        }
    }

    private void BuildToastButtonRows()
    {
        ToastButtonRows.Children.Clear();

        foreach (var kind in ToastButtonLayout.ConfirmActionButtons)
            ToastButtonRows.Children.Add(BuildToastButtonRow(kind));
    }

    private Border BuildToastButtonRow(ToastButtonKind kind)
    {
        var label = FormatToastButtonLabel(kind);

        // Icon + name only: placement is done by dragging the row onto the preview, or via the
        // right-click / Menu-key context menu (Move to…). No per-row combo any more.
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 14, 8),
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
        var rowTooltip = Services.LocalizationService.Translate("Drag onto the confirm bar to add this pill, or right-click (Menu key) to place it by keyboard.");
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
            Services.LocalizationService.Translate("Drag onto the confirm bar to add the {0} pill, or right-click (or press the Menu key) to place it."),
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
        if (TryFindResource("HistoryActionsMenuStyle") is Style menuStyle)
            menu.Style = menuStyle;

        var header = new MenuItem
        {
            Header = Services.LocalizationService.Translate("Move to"),
            IsEnabled = false
        };
        ApplyToastMenuItemStyle(header);
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
            ApplyToastMenuItemStyle(item);
            item.Click += (_, _) => MoveToastButtonToCorner(kind, capturedCorner);
            menu.Items.Add(item);
        }

        var sep = new Separator();
        if (TryFindResource("HistoryActionsMenuSeparator") is Style sepStyle)
            sep.Style = sepStyle;
        menu.Items.Add(sep);

        var remove = new MenuItem
        {
            Header = Services.LocalizationService.Translate("Remove from confirm bar"),
            IsEnabled = visible
        };
        ApplyToastMenuItemStyle(remove);
        remove.Click += (_, _) => RemoveToastButton(kind);
        menu.Items.Add(remove);

        return menu;
    }

    private void ApplyToastMenuItemStyle(MenuItem item)
    {
        if (TryFindResource("HistoryActionsMenuItem") is Style style)
            item.Style = style;
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
            Services.LocalizationService.Translate("The {0} pair is full (2 pills max). Move another pill out first."),
            FormatCornerMenuLabel(corner));
        PersistToastButtonLayout();
    }

    private void ConfirmFixedPill_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border pill)
            return;

        // Immediate click feedback — these pills are not draggable.
        FlashFixedConfirmPill(pill);

        _fixedPillPressSource = pill;
        _fixedPillPressStart = e.GetPosition(ToastDesignerRoot);
        _fixedPillArmed = true;
    }

    private void ConfirmFixedPill_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fixedPillArmed || e.LeftButton != MouseButtonState.Pressed || _fixedPillPressSource is null)
        {
            _fixedPillArmed = false;
            return;
        }

        var pos = e.GetPosition(ToastDesignerRoot);
        if (Math.Abs(pos.X - _fixedPillPressStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _fixedPillPressStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Drag attempt: refresh the red flash so the rejection stays obvious.
        _fixedPillArmed = false;
        FlashFixedConfirmPill(_fixedPillPressSource);
        e.Handled = true;
    }

    private void ConfirmFixedPill_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _fixedPillArmed = false;
        _fixedPillPressSource = null;
    }

    private void FlashFixedConfirmPill(Border pill)
    {
        var red = Theme.Brush(Color.FromRgb(0xEF, 0x44, 0x44));
        pill.BorderBrush = red;
        pill.BorderThickness = new Thickness(2);
        pill.Opacity = 1;

        if (pill.Child is TextBlock glyph)
            glyph.Foreground = red;

        _fixedPillFlashTimer?.Stop();
        _fixedPillFlashTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };
        _fixedPillFlashTimer.Tick += (_, _) =>
        {
            _fixedPillFlashTimer.Stop();
            RestoreFixedConfirmPillChrome(pill);
        };
        _fixedPillFlashTimer.Start();
    }

    private static void RestoreFixedConfirmPillChrome(Border? pill)
    {
        if (pill is null)
            return;

        pill.Background = Theme.Brush(Theme.IsDark && !Theme.IsGray
            ? Theme.BgSecondary
            : Theme.IsGray
            ? Color.FromArgb(215, 24, 26, 29)
            : Color.FromArgb(210, 235, 246, 253));
        pill.BorderBrush = Theme.Brush(Theme.BorderSubtle);
        pill.BorderThickness = new Thickness(1);
        pill.Opacity = 0.85;

        if (pill.Child is TextBlock glyph)
            glyph.Foreground = Theme.Brush(Theme.TextSecondary);
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
                    Services.LocalizationService.Translate("The {0} pair is full (2 pills max). Remove a pill first."),
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
        UpdateToastPreviewButton(ToastLayoutSaveBtn, ToastLayoutSaveIcon, "save", ToastButtonKind.Save);
        UpdateToastPreviewButton(ToastLayoutCopyBtn, ToastLayoutCopyIcon, "copy", ToastButtonKind.Copy);
        UpdateToastPreviewButton(ToastLayoutShareBtn, ToastLayoutShareIcon, "share", ToastButtonKind.Share);
        UpdateToastPreviewButton(ToastLayoutDeleteBtn, ToastLayoutDeleteIcon, "trash", ToastButtonKind.Delete);
        UpdateToastPreviewButton(ToastLayoutAiRedirectBtn, ToastLayoutAiRedirectIcon, "history", ToastButtonKind.History);
        UpdateToastPreviewButton(ToastLayoutEditBtn, ToastLayoutEditIcon, "draw", ToastButtonKind.Edit);
        RefreshToastActionsPanelMetrics();
    }

    private void RefreshToastActionsPanelMetrics()
    {
        int maxRow = 0;
        foreach (var btn in ToastButtonLayout.ConfirmActionButtons)
        {
            if (!ToastButtonLayout.IsVisible(ToastButtons, btn))
                continue;

            var (row, _) = ToastButtonLayout.ToGridCell(ToastButtonLayout.GetSlot(ToastButtons, btn));
            maxRow = Math.Max(maxRow, row + 1);
        }

        // Always keep at least one destination row so empty slots stay drop targets
        // (e.g. Basic preset still needs a place to drag Save/Copy/Edit/Share).
        const double rowHeight = 36;
        int rows = Math.Max(1, maxRow);
        ToastLayoutActionsPanel.RowDefinitions[0].Height = new GridLength(rowHeight);
        ToastLayoutActionsPanel.RowDefinitions[1].Height = rows >= 2 ? new GridLength(rowHeight) : new GridLength(0);
        ToastLayoutActionsPanel.Height = rows * rowHeight;
    }

    private void CollapseAllToastPreviewButtons()
    {
        ToastLayoutCloseBtn.Visibility = Visibility.Collapsed;
        ToastLayoutPinBtn.Visibility = Visibility.Collapsed;
        ToastLayoutSaveBtn.Visibility = Visibility.Collapsed;
        ToastLayoutCopyBtn.Visibility = Visibility.Collapsed;
        ToastLayoutShareBtn.Visibility = Visibility.Collapsed;
        ToastLayoutDeleteBtn.Visibility = Visibility.Collapsed;
        ToastLayoutAiRedirectBtn.Visibility = Visibility.Collapsed;
        ToastLayoutEditBtn.Visibility = Visibility.Collapsed;
    }

    private void UpdateToastPreviewButton(Border border, Image icon, string iconId, ToastButtonKind kind)
    {
        // Confirm-mode chrome only uses Save / Copy / Edit / Share / History.
        bool visible = ToastButtonLayout.IsConfirmActionButton(kind)
            && ToastButtonLayout.IsVisible(ToastButtons, kind);
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
        border.Width = 34;
        border.Height = 34;
        border.CornerRadius = new CornerRadius(14);
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
        // Minimal was removed from the toolbar (redundant with Basic); layouts that still
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
    /// Side-by-side mocks: left = system-alert examples (encoding / brief status);
    /// right = confirm-destination button layout designer.
    /// </summary>
    private void RefreshEditorPreviewState()
    {
        // Guard: this runs from theme refreshes that can fire before the designer is built.
        if (EditorToastMockShell is null || EditorButtonsCard is null)
            return;

        bool trimmerOn = _settingsService.Settings.OpenVideoTrimmerAfterCapture
            || _settingsService.Settings.OpenGifTrimmerAfterCapture;

        // Keep both previews faithful to the real notification shell + edge stroke.
        EditorToastMockShell.Background = Theme.Brush(Theme.ToastBg);
        EditorToastMockShell.BorderBrush = ToastAccentStroke();
        if (EditorToastMockCloseIcon is not null)
            EditorToastMockCloseIcon.Source = Helpers.FluentIcons.RenderWpf("close", GetToastLayoutIconColor(active: false), 16);

        // Confirm-bar destination designer (images) — selection silhouette, not a toast shell.
        if (ToastLayoutShell is not null)
        {
            ToastLayoutShell.Background = Theme.Brush(Theme.IsDark
                ? Theme.BgPrimary
                : Color.FromRgb(0xF3, 0xF5, 0xF8));
            ToastLayoutShell.BorderBrush = Theme.Brush(Theme.BorderSubtle);
        }

        // Selected-region well — quiet silhouette, no accent “selection” ring.
        if (ToastLayoutImageArea is not null)
        {
            ToastLayoutImageArea.Background = Theme.Brush(Theme.IsGray
                ? Color.FromRgb(0x1E, 0x20, 0x23)
                : Theme.IsDark
                ? Theme.BgSecondary
                : Color.FromRgb(0xE2, 0xE5, 0xED));
            ToastLayoutImageArea.BorderBrush = Theme.Brush(Theme.BorderSubtle);
            ToastLayoutImageArea.BorderThickness = new Thickness(1);
        }
        if (ToastLayoutImageGlyph is not null)
        {
            ToastLayoutImageGlyph.Visibility = Visibility.Visible;
            ToastLayoutImageGlyph.Foreground = Theme.Brush(Theme.IsDark
                ? Color.FromRgb(255, 255, 255)
                : Color.FromRgb(0, 0, 0));
            ToastLayoutImageGlyph.Opacity = Theme.IsDark ? 0.14 : 0.20;
        }

        UpdateCapturePreviewMediaBadge(videoMode: false);
        RestoreFixedConfirmPillChrome(ConfirmMockCancelPill);
        RestoreFixedConfirmPillChrome(ConfirmMockRetryPill);

        ApplyMockRail(EditorToastMockRail, EditorToastMockRailGlow);

        if (EditorToastMockShell is not null)
        {
            EditorToastMockShell.Visibility = Visibility.Visible;
            EditorToastMockShell.Background = Theme.Brush(Theme.ToastBg);
            EditorToastMockShell.BorderBrush = ToastAccentStroke();
        }
        if (EncodingToastMockShell is not null)
        {
            EncodingToastMockShell.Visibility = Visibility.Visible;
            EncodingToastMockShell.Background = Theme.Brush(Theme.ToastBg);
            EncodingToastMockShell.BorderBrush = ToastAccentStroke();
        }
        ApplyMockRail(EncodingToastMockRail, EncodingToastMockRailGlow);

        if (SystemAlertExampleLabel is not null)
            SystemAlertExampleLabel.Visibility = Visibility.Visible;

        // Both columns are informative now — no “selected side” accent ring.
        ApplySectionSelectionHighlight(SystemAlertCard, selected: false);
        ApplySectionSelectionHighlight(EditorButtonsCard, selected: false);

        if (ToastLayoutStack is not null)
            ToastLayoutStack.Opacity = 1.0;
        if (CaptureDesignIdleNote is not null)
            CaptureDesignIdleNote.Visibility = Visibility.Collapsed;

        if (EditorPreviewImagesCaption is not null)
        {
            string leftCaption = Services.LocalizationService.Translate("System notifications");
            EditorPreviewImagesCaption.Text = leftCaption;
            AutomationProperties.SetName(EditorPreviewImagesCaption, leftCaption);
        }

        if (EditorPreviewGuide is not null)
        {
            string guide = Services.LocalizationService.Translate(BuildDesignerGuideKey(trimmerOn));
            EditorPreviewGuide.Text = guide;
            AutomationProperties.SetName(EditorPreviewGuide, guide);
        }
    }

    private static string BuildDesignerGuideKey(bool trimmerOn)
    {
        if (!trimmerOn)
            return "Left: fixed system alerts (encoding / recorded). Right: destination pills on the region confirm bar. Video and GIF use brief toasts unless you open the Trimmer from the recording bar.";

        return "Left: fixed system alerts. Right: image confirm pills only. Video and GIF open in the Trimmer after recording (toggle on the recording bar).";
    }

    /// <summary>
    /// Cyan wrap around the active side of the designer (replaces opacity fading, which felt confusing).
    /// </summary>
    private static void ApplySectionSelectionHighlight(Border? card, bool selected)
    {
        if (card is null)
            return;

        card.Opacity = 1.0;
        if (selected)
        {
            // Match app accent (cyan cyber / silver gray / blue light) — same language as the mockup frame.
            var accent = Theme.Accent;
            card.BorderThickness = new Thickness(2);
            card.BorderBrush = Theme.Brush(accent);
            card.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(accent.R, accent.G, accent.B),
                BlurRadius = 16,
                ShadowDepth = 0,
                Opacity = Theme.IsGray ? 0.28 : 0.45
            };
        }
        else
        {
            card.BorderThickness = new Thickness(1);
            card.BorderBrush = Theme.Brush(Theme.BorderSubtle);
            card.Effect = null;
        }
    }

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

    private static string? GetToastButtonDescription(ToastButtonKind button) => button switch
    {
        ToastButtonKind.Close => Services.LocalizationService.Translate("Not used in confirm mode (Cancel is always available)."),
        ToastButtonKind.Pin => Services.LocalizationService.Translate("Not used in confirm mode."),
        ToastButtonKind.Save => Services.LocalizationService.Translate("Save the capture when you confirm the selected region."),
        ToastButtonKind.Copy => Services.LocalizationService.Translate("Copy the capture to the clipboard when confirming."),
        ToastButtonKind.Share => Services.LocalizationService.Translate("Upload and copy a shareable link when confirming."),
        ToastButtonKind.Delete => Services.LocalizationService.Translate("Not used in confirm mode."),
        ToastButtonKind.History => Services.LocalizationService.Translate("Open the capture in Gallery when confirming."),
        ToastButtonKind.Edit => Services.LocalizationService.Translate("Open the capture in the Annotations Editor when confirming."),
        _ => null
    };

    private static string FormatToastButtonLabel(ToastButtonKind button) => button switch
    {
        ToastButtonKind.Close => Services.LocalizationService.Translate("close"),
        ToastButtonKind.Pin => Services.LocalizationService.Translate("pin"),
        ToastButtonKind.Save => Services.LocalizationService.Translate("save"),
        ToastButtonKind.Copy => Services.LocalizationService.Translate("copy"),
        ToastButtonKind.Share => Services.LocalizationService.Translate("share"),
        ToastButtonKind.Delete => Services.LocalizationService.Translate("delete"),
        ToastButtonKind.History => Services.LocalizationService.Translate("gallery"),
        ToastButtonKind.Edit => Services.LocalizationService.Translate("edit"),
        _ => Services.LocalizationService.Translate("notification")
    };

    private static string FormatCornerMenuLabel(ToastCorner corner) => corner switch
    {
        ToastCorner.TopLeft => Services.LocalizationService.Translate("Left pair"),
        ToastCorner.TopRight => Services.LocalizationService.Translate("Right pair"),
        ToastCorner.BottomLeft => Services.LocalizationService.Translate("Lower left pair"),
        _ => Services.LocalizationService.Translate("Lower right pair")
    };

    private static string ToastButtonIconId(ToastButtonKind button) => button switch
    {
        ToastButtonKind.Close => "close",
        ToastButtonKind.Pin => "pin",
        ToastButtonKind.Save => "save",
        ToastButtonKind.Copy => "copy",
        ToastButtonKind.Share => "share",
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
