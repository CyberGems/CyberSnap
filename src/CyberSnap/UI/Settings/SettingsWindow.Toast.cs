using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
using Orientation = System.Windows.Controls.Orientation;

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

        if (ConfirmPillShowLabelsCheck is not null)
        {
            _suppressToastPreferenceChange = true;
            try
            {
                ConfirmPillShowLabelsCheck.IsChecked = _settingsService.Settings.ConfirmPillShowLabels;
            }
            finally
            {
                _suppressToastPreferenceChange = false;
            }
        }
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
            // No hover tooltips in the designer — they cover the mock constantly.
            border.ToolTip = null;
            AutomationProperties.SetHelpText(border, string.Format(
                Services.LocalizationService.Translate("Click × to remove {0} from the confirm bar."),
                ToTitleCase(label)));
        }
    }

    private void BuildToastButtonRows()
    {
        ToastButtonRows.Children.Clear();

        // Tray only lists destinations that are currently OFF the confirm bar.
        var hidden = ToastButtonLayout.ConfirmActionButtons
            .Where(k => !ToastButtonLayout.IsVisible(ToastButtons, k))
            .ToList();

        if (ToastTrayEmptyHint is not null)
            ToastTrayEmptyHint.Visibility = hidden.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var kind in hidden)
            ToastButtonRows.Children.Add(BuildTrayChip(kind));
    }

    /// <summary>
    /// Chip in the centered "available" tray — click or press + to put the destination back on the bar.
    /// </summary>
    private Border BuildTrayChip(ToastButtonKind kind)
    {
        var label = FormatToastButtonLabel(kind);

        var icon = new Image
        {
            Source = Helpers.FluentIcons.RenderWpf(ToastButtonIconId(kind), GetToastLayoutIconColor(active: false), 18),
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(icon, System.Windows.Media.BitmapScalingMode.HighQuality);

        var body = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 12, 0)
        };
        body.Children.Add(icon);
        body.Children.Add(new TextBlock
        {
            Text = ToTitleCase(label),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Foreground = Theme.Brush(Theme.IsDark ? Color.FromRgb(232, 232, 232) : Color.FromRgb(24, 24, 24))
        });

        var host = new Grid
        {
            Margin = new Thickness(0, 4, 10, 4),
            ClipToBounds = false
        };
        var chip = new Border
        {
            Background = Theme.Brush(Theme.IsDark ? Theme.BgSecondary : Color.FromRgb(235, 246, 253)),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Height = 34,
            MinWidth = 34,
            Child = body,
            VerticalAlignment = System.Windows.VerticalAlignment.Center
        };
        host.Children.Add(chip);

        // No tooltips on designer chrome — they cover the mock and tray constantly.
        var plus = BuildCornerBadge("＋", accent: true, tooltip: null);
        plus.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        plus.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        plus.Margin = new Thickness(0, -6, -6, 0);
        var captured = kind;
        plus.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            AddConfirmDestination(captured);
        };
        host.Children.Add(plus);

        var row = new Border
        {
            Child = host,
            Tag = kind,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = System.Windows.Media.Brushes.Transparent,
            Focusable = true,
            ToolTip = null
        };
        AutomationProperties.SetName(row, string.Format(
            Services.LocalizationService.Translate("Add {0} to the confirm bar"), ToTitleCase(label)));
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            AddConfirmDestination(captured);
        };
        return row;
    }

    /// <param name="accent">True = add (+); false = remove (×) red, hover-only on mock buttons.</param>
    private static Border BuildCornerBadge(
        string glyph,
        bool accent,
        string? tooltip,
        bool hoverOnly = false,
        FrameworkElement? hoverHost = null)
    {
        var label = new TextBlock
        {
            Text = glyph,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        };

        var badge = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            // Designer badges intentionally have no ToolTip — too noisy over the mock.
            ToolTip = null,
            Child = label
        };
        _ = tooltip; // reserved if we re-enable sparse tooltips later

        // Scale transform for hover polish (especially + badges).
        var scale = new ScaleTransform(1, 1);
        badge.RenderTransform = scale;
        badge.RenderTransformOrigin = new Point(0.5, 0.5);

        void ApplyAccentRest()
        {
            if (Theme.IsDark)
            {
                badge.Background = Theme.Brush(Color.FromRgb(42, 52, 58));
                badge.BorderBrush = Theme.Brush(Color.FromRgb(70, 90, 100));
                label.Foreground = Theme.Brush(Color.FromRgb(160, 190, 200));
            }
            else
            {
                badge.Background = Theme.Brush(Color.FromRgb(230, 238, 242));
                badge.BorderBrush = Theme.Brush(Color.FromRgb(160, 180, 190));
                label.Foreground = Theme.Brush(Color.FromRgb(40, 80, 100));
            }
        }

        void ApplyAccentHover()
        {
            // Lift: slightly larger, brighter cyan edge without full neon flood.
            if (Theme.IsDark)
            {
                badge.Background = Theme.Brush(Color.FromRgb(28, 70, 78));
                badge.BorderBrush = Theme.Brush(Color.FromRgb(80, 200, 210));
                label.Foreground = Theme.Brush(Color.FromRgb(200, 245, 250));
            }
            else
            {
                badge.Background = Theme.Brush(Color.FromRgb(210, 240, 245));
                badge.BorderBrush = Theme.Brush(Color.FromRgb(0, 140, 160));
                label.Foreground = Theme.Brush(Color.FromRgb(0, 100, 120));
            }
        }

        if (accent)
        {
            ApplyAccentRest();
            badge.MouseEnter += (_, _) =>
            {
                ApplyAccentHover();
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 1.18, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 1.18, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            };
            badge.MouseLeave += (_, _) =>
            {
                ApplyAccentRest();
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.18, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.18, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
            };
        }
        else
        {
            // × remove — soft red, white glyph
            badge.Background = Theme.Brush(Color.FromRgb(0xDC, 0x4C, 0x4C));
            badge.BorderBrush = Theme.Brush(Color.FromRgb(0xB9, 0x1C, 0x1C));
            label.Foreground = Theme.Brush(Color.FromRgb(255, 255, 255));
        }

        if (hoverOnly)
        {
            badge.Visibility = Visibility.Collapsed;
            badge.Opacity = 0;
            if (hoverHost is not null)
            {
                hoverHost.MouseEnter += (_, _) =>
                {
                    badge.Visibility = Visibility.Visible;
                    badge.Opacity = 1;
                };
                hoverHost.MouseLeave += (_, _) =>
                {
                    if (badge.IsMouseOver) return;
                    badge.Visibility = Visibility.Collapsed;
                    badge.Opacity = 0;
                };
                badge.MouseLeave += (_, _) =>
                {
                    if (!hoverHost.IsMouseOver)
                    {
                        badge.Visibility = Visibility.Collapsed;
                        badge.Opacity = 0;
                    }
                };
            }
        }

        return badge;
    }

    private void AddConfirmDestination(ToastButtonKind kind)
    {
        if (!ToastButtonLayout.IsConfirmActionButton(kind))
            return;
        if (ToastButtonLayout.IsVisible(ToastButtons, kind))
            return;

        // Prefer next free confirm slot left→right.
        bool placed = false;
        foreach (var slot in ToastButtonLayout.ConfirmDestinationSlots)
        {
            if (ToastButtonLayout.PlaceFromHidden(ToastButtons, kind, slot))
            {
                placed = true;
                break;
            }
        }
        if (!placed)
            ToastButtonLayout.SetVisible(ToastButtons, kind, true);

        ToastButtons.Manual = true;
        _toastLayoutHint = null;
        PersistToastButtonLayout();
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

        // Immediate click feedback — these buttons are not removable.
        FlashFixedConfirmPill(pill);
        ShowFixedButtonHint(pill);

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

        // Drag attempt: refresh the red flash + banner so the rejection stays obvious.
        _fixedPillArmed = false;
        FlashFixedConfirmPill(_fixedPillPressSource);
        ShowFixedButtonHint(_fixedPillPressSource);
        e.Handled = true;
    }

    private void ConfirmFixedPill_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _fixedPillArmed = false;
        _fixedPillPressSource = null;
    }

    private void ShowFixedButtonHint(Border pill)
    {
        string message = ReferenceEquals(pill, ConfirmMockRetryPill)
            ? Services.LocalizationService.Translate("Retry stays fixed on the confirm bar. It cannot be moved or removed.")
            : Services.LocalizationService.Translate("Cancel stays fixed on the confirm bar. It cannot be moved or removed.");
        ShowConfirmBarHint(message);
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

    // Send a destination pill to the available tray (× on the mock, or legacy double-click).
    private void RemoveToastButton(ToastButtonKind kind)
    {
        if (!ToastButtonLayout.IsVisible(ToastButtons, kind))
            return;

        // Keep at least one destination so the bar can always commit.
        int visibleCount = ToastButtonLayout.ConfirmActionButtons
            .Count(b => ToastButtonLayout.IsVisible(ToastButtons, b));
        if (visibleCount <= 1)
        {
            ShowConfirmBarHint(Services.LocalizationService.Translate(
                "Keep at least one destination on the confirm bar."));
            return;
        }

        ToastButtonLayout.SetVisible(ToastButtons, kind, false);
        ToastButtons.Manual = true;
        _toastLayoutHint = null;
        PersistToastButtonLayout();
    }

    private System.Windows.Threading.DispatcherTimer? _confirmBarHintTimer;

    /// <summary>
    /// Banner just above the confirm buttons. Collapsed when idle (no gap); when shown the shell's
    /// image row (*) shrinks so buttons stay pinned to the bottom of the mock.
    /// </summary>
    private void ShowConfirmBarHint(string message)
    {
        if (ConfirmBarInlineHint is null)
        {
            _toastLayoutHint = message;
            RefreshToastLayoutStatus();
            return;
        }

        ConfirmBarInlineHint.BeginAnimation(UIElement.OpacityProperty, null);
        ConfirmBarInlineHint.Text = message;
        ConfirmBarInlineHint.Visibility = Visibility.Visible;
        ConfirmBarInlineHint.Opacity = 0;
        ConfirmBarInlineHint.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        });

        _confirmBarHintTimer?.Stop();
        _confirmBarHintTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        var captured = message;
        _confirmBarHintTimer.Tick += (_, _) =>
        {
            _confirmBarHintTimer.Stop();
            if (ConfirmBarInlineHint is null
                || !string.Equals(ConfirmBarInlineHint.Text, captured, StringComparison.Ordinal))
                return;

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) =>
            {
                if (ConfirmBarInlineHint is null
                    || !string.Equals(ConfirmBarInlineHint.Text, captured, StringComparison.Ordinal))
                    return;
                ConfirmBarInlineHint.Text = "";
                ConfirmBarInlineHint.Visibility = Visibility.Collapsed;
                ConfirmBarInlineHint.Opacity = 1;
            };
            ConfirmBarInlineHint.BeginAnimation(UIElement.OpacityProperty, fade);
        };
        _confirmBarHintTimer.Start();
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

    private void ApplyToastPreset(ToastButtonPreset preset)
    {
        ToastButtonLayout.ApplyPreset(ToastButtons, preset);
        ToastButtons.Manual = false;
        _toastLayoutHint = null;
        PersistToastButtonLayout();
    }

    private void ResetToastButtonsBtn_Click(object sender, RoutedEventArgs e)
    {
        // Restore Full destination layout (Save/Copy/Edit/Share/Gallery) as the shipping default.
        _settingsService.Settings.ToastButtons = new AppSettings.ToastButtonLayoutSettings();
        ToastButtonLayout.ApplyPreset(_settingsService.Settings.ToastButtons, ToastButtonPreset.Full);
        _settingsService.Settings.ToastButtons.Manual = false;
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
        // Rebuild the available-destinations tray whenever the active set changes
        // (× remove / + add). Without this, removed pills vanished from the mock and
        // never reappeared under "Available destinations".
        BuildToastButtonRows();
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
        // Confirm destinations always sit on a single horizontal strip (5 slots).
        const double rowHeight = 36;
        if (ToastLayoutActionsPanel.RowDefinitions.Count > 0)
            ToastLayoutActionsPanel.RowDefinitions[0].Height = new GridLength(rowHeight);
        ToastLayoutActionsPanel.Height = rowHeight;
        bool labels = _settingsService.Settings.ConfirmPillShowLabels;
        // Icon-only: ~40px each; with labels, leave room for short text.
        ToastLayoutActionsPanel.MinWidth = labels ? 5 * 72 : 5 * 40;
        if (ToastLayoutShell is not null)
            ToastLayoutShell.MaxWidth = labels ? 560 : 420;
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
        border.CornerRadius = new CornerRadius(14);
        var iconColor = GetToastLayoutIconColor(active: false);
        icon.Source = Helpers.FluentIcons.RenderWpf(iconId, iconColor, 22);

        // Reflect ConfirmPillShowLabels: icon-only vs icon + short label (matches capture chrome).
        bool showLabels = _settingsService.Settings.ConfirmPillShowLabels
            && ToastButtonLayout.IsConfirmActionButton(kind);
        ApplyConfirmPreviewPillChrome(border, icon, kind, showLabels);
    }

    private void ApplyConfirmPreviewPillChrome(
        Border border,
        Image icon,
        ToastButtonKind kind,
        bool showLabels)
    {
        // WPF forbids assigning a FrameworkElement that already has a logical parent.
        DetachFromLogicalParent(icon);
        if (border.Child is not null && !ReferenceEquals(border.Child, icon))
            border.Child = null;

        UIElement faceContent;
        if (!showLabels)
        {
            border.Width = 34;
            border.Height = 34;
            border.MinWidth = 34;
            border.Padding = new Thickness(0);
            icon.Width = 22;
            icon.Height = 22;
            icon.Margin = new Thickness(0);
            icon.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            icon.VerticalAlignment = System.Windows.VerticalAlignment.Center;
            faceContent = icon;
        }
        else
        {
            string label = kind switch
            {
                ToastButtonKind.Save => Services.LocalizationService.Translate("Save"),
                ToastButtonKind.Copy => Services.LocalizationService.Translate("Copy"),
                ToastButtonKind.Edit => Services.LocalizationService.Translate("Edit"),
                ToastButtonKind.Share => Services.LocalizationService.Translate("Share"),
                ToastButtonKind.History => Services.LocalizationService.Translate("Gallery"),
                _ => FormatToastButtonLabel(kind)
            };

            icon.Width = 16;
            icon.Height = 16;
            icon.Margin = new Thickness(0, 0, 5, 0);
            icon.VerticalAlignment = System.Windows.VerticalAlignment.Center;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock
            {
                Text = ToTitleCase(label),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Foreground = Theme.Brush(Theme.IsDark ? Color.FromRgb(230, 231, 233) : Color.FromRgb(30, 30, 30))
            });

            border.Width = double.NaN;
            border.MinWidth = 34;
            border.Height = 34;
            border.Padding = new Thickness(8, 0, 10, 0);
            faceContent = row;
        }

        // Face + micro × badge (top-right, red, hover-only) to remove the destination.
        var faceHost = new Grid { ClipToBounds = false };
        var face = new Border
        {
            Child = faceContent,
            Background = System.Windows.Media.Brushes.Transparent,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = System.Windows.VerticalAlignment.Stretch
        };
        faceHost.Children.Add(face);

        var remove = BuildCornerBadge(
            "×",
            accent: false,
            tooltip: null,
            hoverOnly: true,
            hoverHost: border);
        remove.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        remove.VerticalAlignment = System.Windows.VerticalAlignment.Top;
        remove.Margin = new Thickness(0, -7, -7, 0);
        var captured = kind;
        remove.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // Don't start a drag when clicking ×
            e.Handled = true;
        };
        remove.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RemoveToastButton(captured);
        };
        faceHost.Children.Add(remove);

        border.ClipToBounds = false;
        border.Child = faceHost;
        border.ToolTip = null;

        AutomationProperties.SetHelpText(border, string.Format(
            Services.LocalizationService.Translate("Click × to remove {0} from the confirm bar."),
            ToTitleCase(FormatToastButtonLabel(kind))));
    }

    private static void DetachFromLogicalParent(FrameworkElement? element)
    {
        if (element?.Parent is null)
            return;

        switch (element.Parent)
        {
            case System.Windows.Controls.Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator: // Border, etc.
                if (ReferenceEquals(decorator.Child, element))
                    decorator.Child = null;
                break;
            case ContentControl content:
                if (ReferenceEquals(content.Content, element))
                    content.Content = null;
                break;
        }
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
        // Preset chips (Basic / Standard / Full / Manual) were removed from the designer —
        // drag-drop + RMB hide cover the same ground. Method kept as a no-op for callers.
    }

    private static void HighlightToastPreset(Button button, bool active)
    {
        if (button is null) return;
        button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        button.BorderBrush = active
            ? Theme.Brush(Theme.IsDark ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0))
            : Theme.Brush(Theme.BorderSubtle);
        button.BorderThickness = new Thickness(active ? 2 : 1);
    }

    private void RefreshToastLayoutStatus()
    {
        // Legacy distant status line — keep collapsed; actionable hints use ConfirmBarInlineHint.
        if (ToastLayoutSelectionText is not null)
        {
            ToastLayoutSelectionText.Text = string.Empty;
            ToastLayoutSelectionText.Visibility = Visibility.Collapsed;
        }

        // Don't clobber a live inline banner on routine designer refresh.
        if (_toastLayoutHint is not null)
            ShowConfirmBarHint(_toastLayoutHint);
    }

    private void PersistToastButtonLayout()
    {
        _settingsService.Save();
        ToastWindow.SetButtonLayout(ToastButtons);
        RefreshToastButtonLayoutDesigner();
    }

    /// <summary>
    /// Themes the confirm-destination pill designer mock (single column).
    /// </summary>
    private void RefreshEditorPreviewState()
    {
        // Guard: this runs from theme refreshes that can fire before the designer is built.
        if (EditorButtonsCard is null && ToastLayoutShell is null)
            return;

        // Confirm-bar destination designer — selection silhouette, not a toast shell.
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

        ApplySectionSelectionHighlight(EditorButtonsCard, selected: false);

        if (ToastLayoutStack is not null)
            ToastLayoutStack.Opacity = 1.0;
        if (CaptureDesignIdleNote is not null)
            CaptureDesignIdleNote.Visibility = Visibility.Collapsed;
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
        ToastButtonKind.History => Services.LocalizationService.Translate(
            "Gallery: saves the capture to disk, then opens it in Gallery (hotkey G)."),
        ToastButtonKind.Edit => Services.LocalizationService.Translate("Open the capture in the Annotations Editor when confirming."),
        _ => null
    };

    /// <summary>Called from capture overlay when the user hides/shows destination pills via RMB.</summary>
    public void RefreshConfirmPillDesigner()
    {
        try
        {
            // Settings service already holds the updated ToastButtons; rebuild designer chrome.
            RefreshToastButtonLayoutDesigner();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.refresh-confirm-pills", ex);
        }
    }

    /// <summary>Keeps the labels checkbox aligned when toggled from the capture confirm bar.</summary>
    public void SyncConfirmPillShowLabels(bool show)
    {
        try
        {
            if (ConfirmPillShowLabelsCheck is null) return;
            if (ConfirmPillShowLabelsCheck.IsChecked == show) return;
            _suppressToastPreferenceChange = true;
            try { ConfirmPillShowLabelsCheck.IsChecked = show; }
            finally { _suppressToastPreferenceChange = false; }
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.sync-confirm-labels", ex);
        }
    }

    private void ConfirmPillShowLabelsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _suppressToastPreferenceChange) return;
        bool show = ConfirmPillShowLabelsCheck.IsChecked == true;
        if (_settingsService.Settings.ConfirmPillShowLabels == show) return;
        _settingsService.Settings.ConfirmPillShowLabels = show;
        _settingsService.Save();
        // Live-update the confirm-bar mock so the toggle effect is visible immediately.
        RefreshToastPreview();
    }

    private void OpenCaptureConfirmPills_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            NavigateToCaptureConfirmPills();
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogError("settings.open-capture-confirm-pills", ex);
        }
    }

    public void NavigateToCaptureConfirmPills()
    {
        CaptureTab.IsChecked = true;
        ApplyMainTabSelection();
        if (CaptureConfirmPillsSection is FrameworkElement fe)
        {
            fe.BringIntoView();
            try { CapturePanel.ScrollToVerticalOffset(Math.Max(0, fe.TranslatePoint(new Point(0, 0), CapturePanel).Y - 12)); }
            catch { /* layout may not be ready */ }
        }
    }

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
