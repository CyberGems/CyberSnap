using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI.Controls;
using Color = System.Windows.Media.Color;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace CyberSnap.UI;

public partial class CapturePreviewDialog
{
    private void UpdateOutlineButton()
    {
        string language = _settingsService.Settings.InterfaceLanguage;
        PreviewOutlineBtn.ToolTip = LocalizationService.Translate(language, "Add an outline to saved and copied images.");
        AutomationProperties.SetName(PreviewOutlineBtn, LocalizationService.Translate(language, "Outline options"));
        var color = _active.OutlineEnabled ? Theme.Accent : Theme.TextSecondary;
        SetPreviewIcon(PreviewOutlineIcon, "rect", System.Drawing.Color.FromArgb(color.R, color.G, color.B), 12);
    }

    private bool ApplyOutlineSettings(bool enabled, int thickness, int color)
    {
        if (_isClosing) return false;
        try
        {
            var previous = EffectiveBitmap;
            _active.SetOutline(enabled, thickness, color);
            var settings = _settingsService.Settings;
            settings.PreviewOutlineEnabled = _active.OutlineEnabled;
            settings.PreviewOutlineThickness = _active.OutlineThickness;
            settings.PreviewOutlineColorArgb = _active.OutlineColorArgb;
            _settingsService.Save();
            if (!ReferenceEquals(previous, EffectiveBitmap))
            {
                RefreshFramedPreview();
            }
            UpdateOutlineButton();
            CancelAutoCloseOnInteraction();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            ToastWindow.ShowError(LocalizationService.Translate("Outline not available"), LocalizationService.Translate(ex.Message));
            return false;
        }
        catch (OutOfMemoryException)
        {
            ToastWindow.ShowError(LocalizationService.Translate("Outline not available"),
                LocalizationService.Translate("Not enough memory to add the outline."));
            return false;
        }
    }

    private void PreviewOutlineBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing || !IsLoaded) return;
        CancelAutoCloseOnInteraction();
        if (_previewBackgroundColorPopup?.IsOpen == true)
        {
            bool same = ReferenceEquals(_previewBackgroundColorPopup.PlacementTarget, PreviewOutlineBtn);
            ClosePreviewBackgroundColorPicker();
            if (same) return;
        }
        bool updating = false;
        string language = _settingsService.Settings.InterfaceLanguage;
        var picker = new ColorPickerPopup(LocalizationService.Translate(language, "Reset"));
        picker.Resources["ThemeTextPrimaryBrush"] = Theme.Brush(Theme.TextPrimary);
        picker.Resources["ThemeAccentBrush"] = Theme.Brush(Theme.Accent);
        picker.Resources["ThemeInputBorderBrush"] = Theme.Brush(Theme.BorderSubtle);
        var enabled = new CheckBox
        {
            Foreground = Theme.Brush(Theme.TextPrimary),
            IsChecked = _active.OutlineEnabled,
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(enabled, LocalizationService.Translate(language, "Capture outline"));
        var enableRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(enabled, Dock.Right);
        enableRow.Children.Add(enabled);
        enableRow.Children.Add(new TextBlock
        {
            Text = LocalizationService.Translate(language, "Capture outline"),
            Foreground = Theme.Brush(Theme.TextPrimary),
            VerticalAlignment = VerticalAlignment.Center
        });
        var thickness = new Slider
        {
            Minimum = 1, Maximum = CaptureOutlineRenderer.MaxThickness,
            TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 4,
            Value = _active.OutlineThickness,
            Style = (Style)Resources["PreviewOutlineSlider"],
            Margin = new Thickness(0, 0, 8, 0)
        };
        AutomationProperties.SetName(thickness, LocalizationService.Translate(language, "Outline thickness"));
        var value = new TextBlock
        {
            Foreground = Theme.Brush(Theme.TextSecondary),
            Text = $"{_active.OutlineThickness} px", Width = 36,
            VerticalAlignment = VerticalAlignment.Center
        };
        var row = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(thickness);
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(enableRow);
        header.Children.Add(row);
        picker.SetHeader(header);
        Color CurrentColor()
        {
            var rgb = System.Drawing.Color.FromArgb(_active.OutlineColorArgb);
            return Color.FromRgb(rgb.R, rgb.G, rgb.B);
        }
        picker.SetColor(CurrentColor());
        void RefreshHeader(bool syncColor = false)
        {
            updating = true;
            try
            {
                enabled.IsChecked = _active.OutlineEnabled;
                thickness.Value = _active.OutlineThickness;
                value.Text = $"{_active.OutlineThickness} px";
                if (syncColor) picker.SetColor(CurrentColor());
            }
            finally { updating = false; }
        }
        enabled.Checked += (_, _) =>
        {
            if (updating) return;
            ApplyOutlineSettings(true, _active.OutlineThickness, _active.OutlineColorArgb);
            RefreshHeader();
        };
        enabled.Unchecked += (_, _) =>
        {
            if (updating) return;
            ApplyOutlineSettings(false, _active.OutlineThickness, _active.OutlineColorArgb);
            RefreshHeader();
        };
        thickness.ValueChanged += (_, _) =>
        {
            if (updating) return;
            ApplyOutlineSettings(true, (int)thickness.Value, _active.OutlineColorArgb);
            RefreshHeader();
        };
        picker.ColorChanged += color =>
        {
            if (updating) return;
            bool applied;
            if (color is { } c)
                applied = ApplyOutlineSettings(true, _active.OutlineThickness, System.Drawing.Color.FromArgb(c.R, c.G, c.B).ToArgb());
            else
                applied = ApplyOutlineSettings(false, 2, unchecked((int)0xFF000000));
            // Keep the wheel's HSV state during live edits, including V=0 (black).
            // Only a reset or rejected edit needs to replace the picker's color.
            RefreshHeader(syncColor: color is null || !applied);
        };
        ShowPreviewColorPicker(picker, PreviewOutlineBtn);
    }
}
