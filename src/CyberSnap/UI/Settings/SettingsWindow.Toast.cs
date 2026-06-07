using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Models;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using GiveFeedbackEventArgs = System.Windows.GiveFeedbackEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    // ComboBox per button row, keyed by kind so the designer can refresh selections
    // and corner-occupancy without walking the visual tree.
    private readonly Dictionary<ToastButtonKind, ComboBox> _toastComboBoxes = new();

    // Preview button Borders keyed by kind, for drag-over eviction preview.
    private Dictionary<ToastButtonKind, Border> _previewButtonBorders = null!;

    // Button temporarily dimmed during drag-over so the user sees which occupant
    // would be evicted on drop. Restored on DragLeave.
    private Border? _dragDimmedButton;

    // Guard to suppress re-entrant SelectionChanged during programmatic refresh.
    private bool _refreshingToastCombos;

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
            [ToastButtonKind.Office] = ToastLayoutOfficeBtn,
            [ToastButtonKind.Delete] = ToastLayoutDeleteBtn,
            [ToastButtonKind.History] = ToastLayoutAiRedirectBtn,
            [ToastButtonKind.Edit] = ToastLayoutEditBtn,
        };

        BuildToastButtonRows();
        RefreshToastButtonLayoutDesigner();
    }

    private void BuildToastButtonRows()
    {
        _toastComboBoxes.Clear();
        ToastButtonRows.Children.Clear();

        foreach (var kind in ToastButtonLayout.AllButtons)
            ToastButtonRows.Children.Add(BuildToastButtonRow(kind));
    }

    private Border BuildToastButtonRow(ToastButtonKind kind)
    {
        var label = FormatToastButtonLabel(kind);

        // Left-packed with a fixed label column so the ComboBox stays near the label and aligned
        // across rows, instead of being pushed to the far right edge on wide windows.
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
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

        var combo = new ComboBox
        {
            Width = 130,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Tag = kind
        };
        AutomationProperties.SetName(combo, $"Placement for {label} button");
        AutomationProperties.SetHelpText(combo, $"Choose which corner the {label} button appears in, or hide it.");
        if (desc is not null)
        {
            combo.ToolTip = desc;
            ToolTipService.SetInitialShowDelay(combo, 200);
            AutomationProperties.SetHelpText(combo, desc);
        }

        combo.Items.Add(CreateCornerComboItem("Hidden", null, $"Hide the {label} button"));
        combo.Items.Add(CreateCornerComboItem("Top Left", ToastCorner.TopLeft, $"Place the {label} button in the top-left corner"));
        combo.Items.Add(CreateCornerComboItem("Top Right", ToastCorner.TopRight, $"Place the {label} button in the top-right corner"));
        combo.Items.Add(CreateCornerComboItem("Bottom Left", ToastCorner.BottomLeft, $"Place the {label} button in the bottom-left corner"));
        combo.Items.Add(CreateCornerComboItem("Bottom Right", ToastCorner.BottomRight, $"Place the {label} button in the bottom-right corner"));

        combo.SelectionChanged += ToastButtonCombo_SelectionChanged;
        Grid.SetColumn(combo, 2);
        grid.Children.Add(combo);

        _toastComboBoxes[kind] = combo;
        return new Border { Child = grid };
    }

    private static ComboBoxItem CreateCornerComboItem(string text, ToastCorner? corner, string tooltip)
    {
        var item = new ComboBoxItem
        {
            Content = text,
            Tag = corner,
            ToolTip = tooltip
        };
        AutomationProperties.SetName(item, text);
        AutomationProperties.SetHelpText(item, tooltip);
        return item;
    }

    private void ToastButtonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingToastCombos)
            return;

        if (sender is not ComboBox combo || combo.Tag is not ToastButtonKind kind)
            return;
        if (combo.SelectedItem is not ComboBoxItem item)
            return;

        var corner = item.Tag as ToastCorner?;

        if (corner is null)
        {
            ToastButtonLayout.SetVisible(ToastButtons, kind, false);
            _toastLayoutHint = null;
            PersistToastButtonLayout();
            return;
        }

        if (ToastButtonLayout.AssignCorner(ToastButtons, kind, corner.Value))
        {
            _toastLayoutHint = null;
            PersistToastButtonLayout();
            return;
        }

        // Corner full — shouldn't happen when disabled items work, but guard anyway.
        _toastLayoutHint = $"The {FormatCornerLabel(corner.Value)} corner is full (2 buttons max). Move another button out first.";
        PersistToastButtonLayout();
    }

    /// <summary>Whether the per-button combo boxes should be interactive.
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

        // Arm the drag but don't start it here. Calling DragDrop.DoDragDrop directly from the
        // mouse-down handler is flaky: the first click on a not-yet-focused control is consumed
        // by focus activation and the drag silently fails to begin (the "some buttons won't drag
        // until you've clicked around a bit" symptom). We wait for real movement instead.
        _toastDragSource = border;
        _toastDragKind = kind;
        _toastDragStart = e.GetPosition(ToastLayoutPreviewSurface);
        _toastDragArmed = true;
    }

    private void ToastPreviewSurface_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_toastDragArmed || e.LeftButton != MouseButtonState.Pressed)
        {
            _toastDragArmed = false;
            return;
        }

        var pos = e.GetPosition(ToastLayoutPreviewSurface);
        if (Math.Abs(pos.X - _toastDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(pos.Y - _toastDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _toastDragArmed = false;
        StartToastDrag();
    }

    private void StartToastDrag()
    {
        if (_toastDragSource is not { } border)
            return;
        var kind = _toastDragKind;

        RestoreDimmedButton();

        // Snapshot the button at full opacity for the floating ghost, then hide the original so
        // only the ghost follows the cursor.
        ShowToastDragGhost(border);

        var savedOpacity = border.Opacity;
        border.Opacity = 0;

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
        border.GiveFeedback += OnGiveFeedback;
        try
        {
            DragDrop.DoDragDrop(border, kind, DragDropEffects.Move);
        }
        finally
        {
            border.GiveFeedback -= OnGiveFeedback;
            border.Opacity = savedOpacity;
            foreach (var b in _previewButtonBorders.Values)
                b.IsHitTestVisible = true;
            HideToastDragGhost();
            RestoreDimmedButton();
            RefreshToastSlotIndicators();
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

        // _toastDragStart is in ToastLayoutPreviewSurface space; the ghost lives in
        // ToastLayoutSurfaceRoot space, so translate before positioning.
        var p = ToastLayoutPreviewSurface.TransformToVisual(ToastLayoutSurfaceRoot).Transform(_toastDragStart);
        PositionToastDragGhost(p);
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

    /// <summary>Accept Move effects anywhere on the preview surface so the cursor
    /// never shows the "no" symbol between slot targets.</summary>
    private void ToastPreviewSurface_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(ToastButtonKind)))
        {
            e.Effects = DragDropEffects.Move;
            if (ToastDragGhost.Visibility == Visibility.Visible)
                PositionToastDragGhost(e.GetPosition(ToastLayoutSurfaceRoot));
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

        // Drop onto the *exact* slot, not just the corner. AssignSlot swaps with the visible
        // occupant (if any), which is what lets a button move to the free or occupied slot
        // right next to it within the same corner.
        ToastButtonLayout.AssignSlot(ToastButtons, kind, targetSlot);

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

    private void ToastPresetMinimalBtn_Click(object sender, RoutedEventArgs e) => ApplyToastPreset(ToastButtonPreset.Minimal);
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
        _settingsService.Settings.ToastButtons = new AppSettings.ToastButtonLayoutSettings();
        _toastLayoutHint = null;
        PersistToastButtonLayout();
    }

    private void RefreshToastButtonLayoutDesigner()
    {
        RefreshToastPreview();
        RefreshToastSlotIndicators();
        RefreshToastComboBoxes();
        RefreshToastPresetButtons();
        RefreshToastLayoutStatus();
    }

    private void RefreshToastPreview()
    {
        // Collapse all buttons first so no stale positions linger.
        CollapseAllToastPreviewButtons();

        UpdateToastPreviewButton(ToastLayoutCloseBtn, ToastLayoutCloseIcon, "close", ToastButtonKind.Close);
        UpdateToastPreviewButton(ToastLayoutPinBtn, ToastLayoutPinIcon, "pin", ToastButtonKind.Pin);
        UpdateToastPreviewButton(ToastLayoutSaveBtn, ToastLayoutSaveIcon, "download", ToastButtonKind.Save);
        UpdateToastPreviewButton(ToastLayoutOfficeBtn, ToastLayoutOfficeIcon, "arrow", ToastButtonKind.Office);
        UpdateToastPreviewButton(ToastLayoutDeleteBtn, ToastLayoutDeleteIcon, "trash", ToastButtonKind.Delete);
        UpdateToastPreviewButton(ToastLayoutAiRedirectBtn, ToastLayoutAiRedirectIcon, "history", ToastButtonKind.History);
        UpdateToastPreviewButton(ToastLayoutEditBtn, ToastLayoutEditIcon, "draw", ToastButtonKind.Edit);
    }

    private void CollapseAllToastPreviewButtons()
    {
        ToastLayoutCloseBtn.Visibility = Visibility.Collapsed;
        ToastLayoutPinBtn.Visibility = Visibility.Collapsed;
        ToastLayoutSaveBtn.Visibility = Visibility.Collapsed;
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

        var placement = ToastButtonLayout.ToPlacement(ToastButtonLayout.GetSlot(ToastButtons, kind));
        border.HorizontalAlignment = placement.horizontal;
        border.VerticalAlignment = placement.vertical;
        border.Margin = placement.margin;
        // Always reset opacity: a drag may have left this button dimmed (drag source at 0.30,
        // or an eviction-preview occupant at 0.20 that ended up not evicted). Without this,
        // the stale transparency persists across refreshes and presets.
        border.Opacity = 1;
        border.Background = Theme.Brush(Theme.IsDark ? Color.FromArgb(64, 0, 0, 0) : Color.FromArgb(40, 0, 0, 0));
        icon.Source = Helpers.FluentIcons.RenderWpf(iconId, GetToastLayoutIconColor(active: false), 22);
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

    private void RefreshToastComboBoxes()
    {
        bool manual = IsManualMode;
        _refreshingToastCombos = true;
        try
        {
            foreach (var (kind, combo) in _toastComboBoxes)
            {
                // Enable the entire combo box only when Manual mode is active.
                combo.IsEnabled = manual;

                bool visible = ToastButtonLayout.IsVisible(ToastButtons, kind);
                ToastCorner? currentCorner = visible
                    ? ToastButtonLayout.GetCorner(ToastButtons, kind)
                    : null;

                foreach (ComboBoxItem item in combo.Items)
                {
                    var itemCorner = item.Tag as ToastCorner?;

                    // Select the item that matches the current state.
                    bool isMatch = itemCorner is null
                        ? !visible
                        : visible && itemCorner.Value == currentCorner;

                    if (isMatch && !ReferenceEquals(combo.SelectedItem, item))
                        combo.SelectedItem = item;

                    // Disable corner options whose two slots are already occupied by
                    // other visible buttons.
                    if (itemCorner is not null)
                        item.IsEnabled = !IsCornerFull(ToastButtons, itemCorner.Value, kind);
                }
            }
        }
        finally
        {
            _refreshingToastCombos = false;
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
        HighlightToastPreset(ToastPresetMinimalBtn, active == ToastButtonPreset.Minimal && !manualActive);
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
        string status;
        if (_toastLayoutHint is not null)
        {
            status = _toastLayoutHint;
        }
        else if (IsManualMode)
        {
            // Manual mode: show the matching preset name as a base, plus "(manual)".
            status = ToastButtonLayout.DetectPreset(ToastButtons) switch
            {
                ToastButtonPreset.Minimal => "Minimal preset (manual)",
                ToastButtonPreset.Standard => "Standard preset (manual)",
                ToastButtonPreset.Full => "Full preset (manual)",
                _ => "Custom layout"
            };
        }
        else
        {
            status = ToastButtonLayout.DetectPreset(ToastButtons) switch
            {
                ToastButtonPreset.Minimal => "Minimal preset",
                ToastButtonPreset.Standard => "Standard preset",
                ToastButtonPreset.Full => "Full preset",
                _ => "Custom layout"
            };
        }

        ToastLayoutSelectionText.Text = status;
        ToastLayoutSelectionText.ToolTip = status;
        AutomationProperties.SetHelpText(ToastLayoutSelectionText, status);
    }

    private void PersistToastButtonLayout()
    {
        _settingsService.Save();
        ToastWindow.SetButtonLayout(ToastButtons);
        RefreshToastButtonLayoutDesigner();
    }

    private static string GetToastButtonDescription(ToastButtonKind button) => button switch
    {
        ToastButtonKind.Close => "Close the notification preview.",
        ToastButtonKind.Pin => "Keep the preview open until you dismiss it manually.",
        ToastButtonKind.Save => "Save the captured image to a file.",
        ToastButtonKind.Office => "Open the screenshot with another app (Word, PowerPoint, etc.).",
        ToastButtonKind.Delete => "Delete the saved file for this preview.",
        ToastButtonKind.History => "Open the capture history window.",
        ToastButtonKind.Edit => "Open the preview in the post-capture editor.",
        _ => null
    };

    private static string FormatToastButtonLabel(ToastButtonKind button) => button switch
    {
        ToastButtonKind.Close => "close",
        ToastButtonKind.Pin => "pin",
        ToastButtonKind.Save => "save",
        ToastButtonKind.Office => "send to",
        ToastButtonKind.Delete => "delete",
        ToastButtonKind.History => "history",
        ToastButtonKind.Edit => "edit",
        _ => "notification"
    };

    private static string FormatCornerLabel(ToastCorner corner) => corner switch
    {
        ToastCorner.TopLeft => "top-left",
        ToastCorner.TopRight => "top-right",
        ToastCorner.BottomLeft => "bottom-left",
        _ => "bottom-right"
    };

    private static string ToastButtonIconId(ToastButtonKind button) => button switch
    {
        ToastButtonKind.Close => "close",
        ToastButtonKind.Pin => "pin",
        ToastButtonKind.Save => "download",
        ToastButtonKind.Office => "arrow",
        ToastButtonKind.Delete => "trash",
        ToastButtonKind.History => "history",
        _ => "draw"
    };

    private static string ToTitleCase(string label)
        => string.IsNullOrEmpty(label) ? label : char.ToUpperInvariant(label[0]) + label[1..];

    private static System.Drawing.Color GetToastLayoutIconColor(bool active)
        => Theme.IsDark
            ? System.Drawing.Color.FromArgb(active ? 255 : 220, 255, 255, 255)
            : System.Drawing.Color.FromArgb(active ? 255 : 210, 24, 24, 24);
}
