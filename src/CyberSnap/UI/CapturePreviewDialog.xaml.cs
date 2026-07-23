using System;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.Capture;

namespace CyberSnap.UI
{
    public partial class CapturePreviewDialog : Window
    {
        private readonly SettingsService _settingsService;
        private readonly Bitmap _capturedBitmap;

        public RegionOverlayForm.ConfirmCommitAction SelectedAction { get; private set; } = RegionOverlayForm.ConfirmCommitAction.Default;

        public CapturePreviewDialog(Bitmap bitmap, SettingsService settingsService)
        {
            _capturedBitmap = bitmap;
            _settingsService = settingsService;

            InitializeComponent();
            CyberSnapWindowChrome.Apply(this);
            UiScale.Set(settingsService.Settings.UiScale);
            UiScale.ApplyToWindow(this, RootBorder, scaleWindowBounds: true);

            Theme.Refresh();
            ApplyTheme();
            LocalizationService.ApplyCurrentCulture(settingsService.Settings.InterfaceLanguage);
            LocalizationService.ApplyTo(this, settingsService.Settings.InterfaceLanguage);

            var lang = settingsService.Settings.InterfaceLanguage;
            Helpers.WindowTitles.ApplyTaskbar(this, Helpers.WindowTitles.Preview, lang);
            TitleBar.Title = LocalizationService.Translate("Capture Preview");
            SaveText.Text = LocalizationService.Translate("Save");
            CopyText.Text = LocalizationService.Translate("Copy");
            EditText.Text = LocalizationService.Translate("Edit");
            ShareText.Text = LocalizationService.Translate("Share");
            GalleryText.Text = LocalizationService.Translate("Gallery");
            CancelText.Text = LocalizationService.Translate("Cancel");

            var greenBrush = System.Drawing.Color.FromArgb(255, 34, 197, 94);
            var blueBrush = System.Drawing.Color.FromArgb(255, 0, 162, 255);
            var violetBrush = System.Drawing.Color.FromArgb(255, 139, 92, 246);
            var cyanBrush = System.Drawing.Color.FromArgb(255, 6, 182, 212);
            var amberBrush = System.Drawing.Color.FromArgb(255, 245, 158, 11);
            var redBrush = System.Drawing.Color.FromArgb(255, 239, 68, 68);

            SaveIcon.Source = FluentIcons.RenderWpf("save", greenBrush, 14, active: true);
            CopyIcon.Source = FluentIcons.RenderWpf("copy", blueBrush, 14, active: true);
            EditIcon.Source = FluentIcons.RenderWpf("draw", violetBrush, 14, active: true);
            ShareIcon.Source = FluentIcons.RenderWpf("share", cyanBrush, 14, active: true);
            GalleryIcon.Source = FluentIcons.RenderWpf("history", amberBrush, 14, active: true);
            CancelIcon.Source = FluentIcons.RenderWpf("signOut", redBrush, 14, active: true);

            PreviewImage.Source = BitmapPerf.ToBitmapSource(bitmap);
        }

        private void ApplyTheme()
        {
            RootBorder.Background = Theme.Brush(Theme.BgPrimary);
            RootBorder.BorderBrush = Theme.Brush(Theme.WindowBorder);
            RootBorder.BorderThickness = new Thickness(1);

            Resources["ThemeTextPrimaryBrush"] = Theme.Brush(Theme.TextPrimary);
            Resources["ThemeTextSecondaryBrush"] = Theme.Brush(Theme.TextSecondary);
            Resources["ThemeMutedBrush"] = Theme.Brush(Theme.TextMuted);
            Resources["ThemeCardBrush"] = Theme.Brush(Theme.BgCard);
            Resources["ThemeInputBackgroundBrush"] = Theme.Brush(Theme.BgSecondary);
            Resources["ThemeInputBorderBrush"] = Theme.Brush(Theme.BorderSubtle);
            Resources["ThemeWindowBorderBrush"] = Theme.Brush(Theme.WindowBorder);
            Resources["ThemeAccentBrush"] = Theme.Brush(Theme.Accent);
            Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);
        }

        private void TitleBar_CloseRequested(object sender, EventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Save;
            DialogResult = true;
            Close();
        }

        private void CopyBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Copy;
            DialogResult = true;
            Close();
        }

        private void EditBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Edit;
            DialogResult = true;
            Close();
        }

        private void ShareBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Share;
            DialogResult = true;
            Close();
        }

        private void GalleryBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.History;
            DialogResult = true;
            Close();
        }

        private void CancelBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                Close();
            }
        }
    }
}
