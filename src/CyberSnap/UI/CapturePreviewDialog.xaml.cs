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
        private bool _isPinned = false;
        private AfterCaptureOutcomeState _lastOutcomeState;

        public RegionOverlayForm.ConfirmCommitAction SelectedAction { get; private set; } = RegionOverlayForm.ConfirmCommitAction.Default;

        public CapturePreviewDialog(Bitmap bitmap, SettingsService settingsService)
        {
            _capturedBitmap = bitmap;
            _settingsService = settingsService;

            InitializeComponent();
            TitleBar.IsPinActive = _isPinned;
            Topmost = true; // Temporary topmost to force it to the foreground on launch
            ContentRendered += (s, e) => Topmost = _isPinned;
            Activated += CapturePreviewDialog_Activated;
            SettingsService.SettingsChanged += SettingsService_SettingsChanged;
            Closed += (s, e) => SettingsService.SettingsChanged -= SettingsService_SettingsChanged;

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
            CancelText.Text = LocalizationService.Translate("Close");
            EditSettingsBtnText.Text = LocalizationService.Translate("Edit");

            var greenBrush = System.Drawing.Color.FromArgb(255, 34, 197, 94);
            var blueBrush = System.Drawing.Color.FromArgb(255, 0, 162, 255);
            var violetBrush = System.Drawing.Color.FromArgb(255, 139, 92, 246);
            var cyanBrush = System.Drawing.Color.FromArgb(255, 6, 182, 212);
            var amberBrush = System.Drawing.Color.FromArgb(255, 245, 158, 11);
            var neutralBrush = System.Drawing.Color.FromArgb(255, 160, 160, 160);

            SaveIcon.Source = FluentIcons.RenderWpf("save", greenBrush, 14, active: true);
            CopyIcon.Source = FluentIcons.RenderWpf("copy", blueBrush, 14, active: true);
            EditIcon.Source = FluentIcons.RenderWpf("draw", violetBrush, 14, active: true);
            ShareIcon.Source = FluentIcons.RenderWpf("share", cyanBrush, 14, active: true);
            GalleryIcon.Source = FluentIcons.RenderWpf("history", amberBrush, 14, active: true);
            CancelIcon.Source = FluentIcons.RenderWpf("cross", neutralBrush, 14, active: true);
            EditSettingsBtnIcon.Source = FluentIcons.RenderWpf("settings", neutralBrush, 12, active: true);

            AfterCaptureHeaderIcon.Source = FluentIcons.RenderWpf("settings", neutralBrush, 14, active: true);
            AfterCaptureHeaderLabel.Text = LocalizationService.Translate("Automatic actions") + ":";

            PreviewImage.Source = BitmapPerf.ToBitmapSource(bitmap);
            PopulateAfterCapturePills();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
            _lastOutcomeState = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
        }

        private void CapturePreviewDialog_Activated(object? sender, EventArgs e)
        {
            RefreshLiveSettings();
        }

        private void SettingsService_SettingsChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(SettingsService_SettingsChanged));
                return;
            }
            RefreshLiveSettings();
        }

        private void RefreshLiveSettings()
        {
            if (!IsLoaded) return;

            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            if (state == _lastOutcomeState) return;
            _lastOutcomeState = state;

            PopulateAfterCapturePills();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
        }

        private void EditAfterCaptureSettingsBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app)
            {
                app.ShowSettings("confirm-pills");
            }
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

            PopulateAfterCapturePills();
            UpdateContinueOrExitButton();
            UpdateOptionalActionsAvailability();
        }

        private void PopulateAfterCapturePills()
        {
            if (AfterCapturePillsPanel == null || _settingsService?.Settings == null) return;
            AfterCapturePillsPanel.Children.Clear();

            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);

            foreach (var pill in AfterCaptureOutcomeModel.AllPills)
            {
                if (!AfterCaptureOutcomeModel.IsActive(state, pill))
                    continue;

                var (iconId, color, labelKey, tooltipKey) = pill switch
                {
                    AfterCapturePillKind.Save => ("save", System.Drawing.Color.FromArgb(255, 34, 197, 94), "Outcome step: save file", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.Preview => ("eye", System.Drawing.Color.FromArgb(255, 59, 130, 246), "Outcome step: preview", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.Clipboard => ("copy", System.Drawing.Color.FromArgb(255, 0, 162, 255), "Auto-copy", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.Notification => ("bell", System.Drawing.Color.FromArgb(255, 245, 158, 11), "Outcome step: show notification", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.Editor => ("draw", System.Drawing.Color.FromArgb(255, 139, 92, 246), "Outcome step: open editor", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    AfterCapturePillKind.SystemViewer => ("openFolder", System.Drawing.Color.FromArgb(255, 6, 182, 212), "Outcome step: open in system viewer", AfterCaptureOutcomeModel.TooltipKey(pill)),
                    _ => ("settings", System.Drawing.Color.FromArgb(255, 150, 150, 150), pill.ToString(), "")
                };

                string label = LocalizationService.Translate(labelKey);
                string tooltip = LocalizationService.Translate(tooltipKey);

                var chip = CreateAfterCapturePillChip(iconId, color, label, tooltip);
                AfterCapturePillsPanel.Children.Add(chip);
            }
        }

        private System.Windows.FrameworkElement CreateAfterCapturePillChip(string iconId, System.Drawing.Color color, string label, string tooltip)
        {
            var border = new System.Windows.Controls.Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(8, 3, 10, 3),
                Margin = new Thickness(0, 2, 6, 2),
                Background = Theme.Brush(System.Windows.Media.Color.FromArgb(30, color.R, color.G, color.B)),
                BorderBrush = Theme.Brush(System.Windows.Media.Color.FromArgb(100, color.R, color.G, color.B)),
                BorderThickness = new Thickness(1),
                ToolTip = string.IsNullOrWhiteSpace(tooltip) ? null : tooltip,
                SnapsToDevicePixels = true
            };

            var stack = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var img = new System.Windows.Controls.Image
            {
                Width = 12,
                Height = 12,
                Margin = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Source = FluentIcons.RenderWpf(iconId, color, 12, active: true)
            };

            var txt = new System.Windows.Controls.TextBlock
            {
                Text = label,
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = Theme.Brush(Theme.TextPrimary),
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(img);
            stack.Children.Add(txt);
            border.Child = stack;

            return border;
        }

        private void UpdateContinueOrExitButton()
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            bool viewerOn = state.SystemViewer;
            bool editorOn = state.Destination == AfterCaptureDestination.Editor;

            if (viewerOn || editorOn)
            {
                CancelText.Text = LocalizationService.Translate("Continue");
                CancelIcon.Visibility = Visibility.Collapsed;
                if (editorOn)
                {
                    ViewerHintBadge.Text = LocalizationService.Translate("The annotation editor opens when this window closes.");
                }
                else
                {
                    ViewerHintBadge.Text = LocalizationService.Translate("The system viewer opens when this window closes.");
                }
                ViewerHintBadge.Visibility = Visibility.Visible;
                CancelBtn.ToolTip = ViewerHintBadge.Text;
            }
            else
            {
                CancelText.Text = LocalizationService.Translate("Exit");
                CancelIcon.Visibility = Visibility.Visible;
                ViewerHintBadge.Visibility = Visibility.Collapsed;
                CancelBtn.ToolTip = null;
            }
        }

        private void UpdateOptionalActionsAvailability()
        {
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            SaveBtn.IsEnabled = !AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Save);
            CopyBtn.IsEnabled = !AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Clipboard);
            EditBtn.IsEnabled = !AfterCaptureOutcomeModel.IsActive(state, AfterCapturePillKind.Editor);

            OptionalActionsHeaderLabel.Text = LocalizationService.Translate("Optional actions");
            OptionalActionsHeaderLabel.ToolTip =
                LocalizationService.Translate("Buttons covered by an active automatic action are disabled.");
        }

        private void TitleBar_CloseRequested(object sender, EventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_PinRequested(object sender, EventArgs e)
        {
            _isPinned = !_isPinned;
            TitleBar.IsPinActive = _isPinned;
            Topmost = _isPinned;
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedAction = RegionOverlayForm.ConfirmCommitAction.Default;
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
            var state = AfterCaptureOutcomeModel.FromSettings(_settingsService.Settings);
            if (state.SystemViewer || state.Destination == AfterCaptureDestination.Editor)
            {
                SelectedAction = RegionOverlayForm.ConfirmCommitAction.Default;
                DialogResult = true;
            }
            else
            {
                DialogResult = false;
            }
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
