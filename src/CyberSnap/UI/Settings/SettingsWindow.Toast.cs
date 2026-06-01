using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Models;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Image = System.Windows.Controls.Image;
using Button = System.Windows.Controls.Button;

namespace CyberSnap.UI;

public partial class SettingsWindow
{
    // One Off + four corner segments per button row, captured so the designer can refresh the
    // selected state without walking the visual tree.
    private readonly List<(ToastButtonKind kind, ToastCorner? corner, Border segment)> _toastSegments = new();

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
        BuildToastButtonRows();
        RefreshToastButtonLayoutDesigner();
    }

    private void BuildToastButtonRows()
    {
        _toastSegments.Clear();
        ToastButtonRows.Children.Clear();

        foreach (var kind in ToastButtonLayout.AllButtons)
            ToastButtonRows.Children.Add(BuildToastButtonRow(kind));
    }

    private Border BuildToastButtonRow(ToastButtonKind kind)
    {
        var label = FormatToastButtonLabel(kind);

        // Left-packed with a fixed label column so the segments stay near the labels and aligned
        // across rows, instead of being pushed to the far right edge on wide windows.
        var grid = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
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

        var name = new TextBlock
        {
            Text = ToTitleCase(label),
            FontSize = 13,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            Foreground = Theme.Brush(Theme.IsDark ? Color.FromRgb(232, 232, 232) : Color.FromRgb(24, 24, 24))
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var segments = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        Grid.SetColumn(segments, 2);
        segments.Children.Add(BuildToastSegment(kind, null, "Off", $"Hide the {label} button"));
        segments.Children.Add(BuildToastSegment(kind, ToastCorner.TopLeft, "↖", $"Place the {label} button in the top-left corner"));
        segments.Children.Add(BuildToastSegment(kind, ToastCorner.TopRight, "↗", $"Place the {label} button in the top-right corner"));
        segments.Children.Add(BuildToastSegment(kind, ToastCorner.BottomLeft, "↙", $"Place the {label} button in the bottom-left corner"));
        segments.Children.Add(BuildToastSegment(kind, ToastCorner.BottomRight, "↘", $"Place the {label} button in the bottom-right corner"));
        grid.Children.Add(segments);

        return new Border { Child = grid };
    }

    private Border BuildToastSegment(ToastButtonKind kind, ToastCorner? corner, string glyph, string helpText)
    {
        var segment = new Border
        {
            Width = corner is null ? 42 : 34,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(4, 0, 0, 0),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = true,
            Tag = (kind, corner),
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = corner is null ? 12 : 14,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        };

        AutomationProperties.SetName(segment, helpText);
        AutomationProperties.SetHelpText(segment, "Press Enter or Space to apply.");
        segment.MouseLeftButtonDown += ToastSegment_MouseLeftButtonDown;
        segment.KeyDown += ToastSegment_KeyDown;

        _toastSegments.Add((kind, corner, segment));
        return segment;
    }

    private void ToastSegment_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is Border { Tag: ValueTuple<ToastButtonKind, ToastCorner?> tag })
            ChooseToastButtonPlacement(tag.Item1, tag.Item2);
    }

    private void ToastSegment_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsToastLayoutActivationKey(e))
            return;

        e.Handled = true;
        if (sender is Border { Tag: ValueTuple<ToastButtonKind, ToastCorner?> tag })
            ChooseToastButtonPlacement(tag.Item1, tag.Item2);
    }

    private void ChooseToastButtonPlacement(ToastButtonKind kind, ToastCorner? corner)
    {
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

        // Corner already holds two buttons — leave the layout untouched and explain why.
        _toastLayoutHint = $"The {FormatCornerLabel(corner.Value)} corner is full (2 buttons max). Move another button out first.";
        RefreshToastButtonLayoutDesigner();
    }

    private void ToastPresetMinimalBtn_Click(object sender, RoutedEventArgs e) => ApplyToastPreset(ToastButtonPreset.Minimal);
    private void ToastPresetStandardBtn_Click(object sender, RoutedEventArgs e) => ApplyToastPreset(ToastButtonPreset.Standard);
    private void ToastPresetFullBtn_Click(object sender, RoutedEventArgs e) => ApplyToastPreset(ToastButtonPreset.Full);

    private void ApplyToastPreset(ToastButtonPreset preset)
    {
        ToastButtonLayout.ApplyPreset(ToastButtons, preset);
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
        RefreshToastSegments();
        RefreshToastPresetButtons();
        RefreshToastLayoutStatus();
    }

    private void RefreshToastPreview()
    {
        UpdateToastPreviewButton(ToastLayoutCloseBtn, ToastLayoutCloseIcon, "close", ToastButtonKind.Close);
        UpdateToastPreviewButton(ToastLayoutPinBtn, ToastLayoutPinIcon, "pin", ToastButtonKind.Pin);
        UpdateToastPreviewButton(ToastLayoutSaveBtn, ToastLayoutSaveIcon, "download", ToastButtonKind.Save);
        UpdateToastPreviewButton(ToastLayoutOfficeBtn, ToastLayoutOfficeIcon, "arrow", ToastButtonKind.Office);
        UpdateToastPreviewButton(ToastLayoutDeleteBtn, ToastLayoutDeleteIcon, "trash", ToastButtonKind.Delete);
        UpdateToastPreviewButton(ToastLayoutAiRedirectBtn, ToastLayoutAiRedirectIcon, "history", ToastButtonKind.History);
        UpdateToastPreviewButton(ToastLayoutEditBtn, ToastLayoutEditIcon, "draw", ToastButtonKind.Edit);
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
        border.Background = Theme.Brush(Theme.IsDark ? Color.FromArgb(64, 0, 0, 0) : Color.FromArgb(40, 0, 0, 0));
        icon.Source = Helpers.FluentIcons.RenderWpf(iconId, GetToastLayoutIconColor(active: false), 22);
    }

    private void RefreshToastSegments()
    {
        foreach (var (kind, corner, segment) in _toastSegments)
        {
            bool visible = ToastButtonLayout.IsVisible(ToastButtons, kind);
            bool selected = corner is null
                ? !visible
                : visible && ToastButtonLayout.GetCorner(ToastButtons, kind) == corner.Value;

            segment.Background = selected
                ? Theme.Brush(Theme.IsDark ? Color.FromRgb(70, 70, 70) : Color.FromRgb(222, 222, 222))
                : Theme.Brush(Theme.IsDark ? Color.FromRgb(40, 40, 40) : Color.FromRgb(246, 246, 246));
            segment.BorderBrush = selected
                ? Theme.Brush(Theme.IsDark ? Color.FromArgb(220, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0))
                : Theme.Brush(Theme.BorderSubtle);

            if (segment.Child is TextBlock glyph)
                glyph.Foreground = Theme.Brush(Theme.IsDark
                    ? Color.FromArgb((byte)(selected ? 255 : 200), 255, 255, 255)
                    : Color.FromArgb((byte)(selected ? 255 : 190), 24, 24, 24));
        }
    }

    private void RefreshToastPresetButtons()
    {
        var active = ToastButtonLayout.DetectPreset(ToastButtons);
        HighlightToastPreset(ToastPresetMinimalBtn, active == ToastButtonPreset.Minimal);
        HighlightToastPreset(ToastPresetStandardBtn, active == ToastButtonPreset.Standard);
        HighlightToastPreset(ToastPresetFullBtn, active == ToastButtonPreset.Full);
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
        string status = _toastLayoutHint ?? ToastButtonLayout.DetectPreset(ToastButtons) switch
        {
            ToastButtonPreset.Minimal => "Minimal preset",
            ToastButtonPreset.Standard => "Standard preset",
            ToastButtonPreset.Full => "Full preset",
            _ => "Custom layout"
        };

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

    private static bool IsToastLayoutActivationKey(KeyEventArgs e)
        => e.Key is Key.Enter or Key.Space;

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
