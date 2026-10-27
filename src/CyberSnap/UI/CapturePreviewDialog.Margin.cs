using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI.Controls;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CyberSnap.UI;

public partial class CapturePreviewDialog
{
    private static void ShowMarginError(Exception exception) => ToastWindow.ShowError(
        LocalizationService.Translate("Margin not available"),
        LocalizationService.Translate(exception is OutOfMemoryException
            ? "Not enough memory to add the margin." : exception.Message));

    private void RefreshFramedPreview()
    {
        PreviewImage.Source = BitmapPerf.ToBitmapSource(EffectiveBitmap);
        _active.PreviewSource = PreviewImage.Source;
        UpdateScaleControls();
        ApplyZoom();
    }

    private bool ApplyMarginSettings(bool enabled, int size)
    {
        if (_isClosing) return false;
        try
        {
            var previous = EffectiveBitmap;
            var solid = GetPreviewBackgroundColor(Theme.IsDark);
            _active.SetMargin(enabled, size, System.Drawing.Color.FromArgb(solid.R, solid.G, solid.B).ToArgb());
            var settings = _settingsService.Settings;
            settings.PreviewMarginEnabled = _active.MarginEnabled;
            settings.PreviewMarginSize = _active.MarginSize;
            _settingsService.Save();
            if (!ReferenceEquals(previous, EffectiveBitmap)) RefreshFramedPreview();
            CancelAutoCloseOnInteraction();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
        {
            ShowMarginError(ex);
            return false;
        }
    }

    private WpfTextBox CreatePreviewPixelInput(string accessibleName, int value)
    {
        var input = new WpfTextBox
        {
            Style = (Style)Resources["PreviewPixelInput"],
            Text = value.ToString(CultureInfo.InvariantCulture),
            Margin = new Thickness(8, 0, 4, 0), MaxLength = 3
        };
        AutomationProperties.SetName(input, accessibleName);
        return input;
    }

    private void AddPreviewMarginControls(ColorPickerPopup picker)
    {
        string language = _settingsService.Settings.InterfaceLanguage;
        picker.Resources["ThemeTextPrimaryBrush"] = Theme.Brush(Theme.TextPrimary);
        picker.Resources["ThemeTextSecondaryBrush"] = Theme.Brush(Theme.TextSecondary);
        picker.Resources["ThemeInputBackgroundBrush"] = Theme.Brush(Theme.BgSecondary);
        picker.Resources["ThemeInputBorderBrush"] = Theme.Brush(Theme.BorderSubtle);
        picker.Resources["ThemeAccentBrush"] = Theme.Brush(Theme.Accent);
        var enabled = new WpfCheckBox { IsChecked = _active.MarginEnabled, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(enabled, LocalizationService.Translate(language, "Capture margin"));
        var enableRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(enabled, Dock.Right);
        enableRow.Children.Add(enabled);
        enableRow.Children.Add(new TextBlock
        {
            Text = LocalizationService.Translate(language, "Capture margin"),
            Foreground = Theme.Brush(Theme.TextPrimary), VerticalAlignment = VerticalAlignment.Center
        });
        string sizeLabel = LocalizationService.Translate(language, "Margin size (px)");
        var size = new Slider
        {
            Minimum = 1, Maximum = CaptureFrameRenderer.MaxMargin,
            Value = _active.MarginSize, TickFrequency = 1, IsSnapToTickEnabled = true,
            SmallChange = 1, LargeChange = 8, Style = (Style)Resources["PreviewOutlineSlider"]
        };
        AutomationProperties.SetName(size, sizeLabel);
        var value = CreatePreviewPixelInput(sizeLabel, _active.MarginSize);
        var units = new TextBlock { Text = "px", Foreground = Theme.Brush(Theme.TextSecondary), VerticalAlignment = VerticalAlignment.Center };
        var sizeRow = new DockPanel();
        DockPanel.SetDock(units, Dock.Right); sizeRow.Children.Add(units);
        DockPanel.SetDock(value, Dock.Right); sizeRow.Children.Add(value);
        sizeRow.Children.Add(size);
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(enableRow); header.Children.Add(sizeRow);
        header.Children.Add(new TextBlock
        {
            Text = LocalizationService.Translate(language, "Outside the outline, in final-image pixels."),
            Foreground = Theme.Brush(Theme.TextSecondary), FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0)
        });
        picker.SetHeader(header);
        bool updating = false;
        void Refresh()
        {
            updating = true;
            try
            {
                enabled.IsChecked = _active.MarginEnabled;
                size.Value = _active.MarginSize;
                value.Text = _active.MarginSize.ToString(CultureInfo.InvariantCulture);
            }
            finally { updating = false; }
        }
        enabled.Checked += (_, _) => { if (!updating) { ApplyMarginSettings(true, _active.MarginSize); Refresh(); } };
        enabled.Unchecked += (_, _) => { if (!updating) { ApplyMarginSettings(false, _active.MarginSize); Refresh(); } };
        size.ValueChanged += (_, _) => { if (!updating) { ApplyMarginSettings(true, (int)size.Value); Refresh(); } };
        value.TextChanged += (_, _) =>
        {
            if (updating || !int.TryParse(value.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int pixels)
                || pixels < 1 || pixels > CaptureFrameRenderer.MaxMargin) return;
            ApplyMarginSettings(true, pixels);
            Refresh();
        };
        value.LostKeyboardFocus += (_, _) => { if (!updating) Refresh(); };
    }
}
