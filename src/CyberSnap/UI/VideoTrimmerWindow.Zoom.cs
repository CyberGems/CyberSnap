using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;

namespace CyberSnap.UI
{
    public partial class VideoTrimmerWindow
    {
        private const double ZoomMin = 0.1;
        private const double ZoomMax = 8.0;
        private const double ZoomStep = 1.2;

        private double _currentZoom = 1.0;
        private bool _zoomToFit;
        private bool _didInitialContain;
        private bool _isPanning;
        private System.Windows.Point _panStart;
        private double _panStartHorizontalOffset;
        private double _panStartVerticalOffset;
        private bool _zoomPointerInside;
        private DispatcherTimer? _zoomHideTimer;
        private int _previewPixelW;
        private int _previewPixelH;

        private void SetupPreviewZoom()
        {
            InitZoomIcons();
            ZoomViewport.SizeChanged += (_, _) =>
            {
                ApplyZoom();
                TryApplyInitialContain();
            };
            Loaded += (_, _) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyZoom();
                    TryApplyInitialContain();
                }), DispatcherPriority.ContextIdle);
            };
        }

        private void ResetPreviewZoom()
        {
            _didInitialContain = false;
            _currentZoom = 1.0;
            _zoomToFit = false;
            _previewPixelW = 0;
            _previewPixelH = 0;
        }

        private void InitZoomIcons()
        {
            if (ZoomOutIcon is null) return;
            var c = Theme.TextSecondary;
            var iconColor = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
            SetPreviewIcon(ZoomOutIcon, "zoomOut", iconColor, 12);
            SetPreviewIcon(ZoomInIcon, "zoomIn", iconColor, 12);
            UpdateZoomViewButton();
            UpdateZoomLevelText();
            ZoomOutBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate("Zoom out"), "Ctrl+-");
            ZoomInBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate("Zoom in"), "Ctrl++");
            ZoomLevelBtn.ToolTip = WithHotkeyHint(
                LocalizationService.Translate("Click for actual size (100%)"), "Ctrl+1");
            AutomationProperties.SetName(ZoomOutBtn, LocalizationService.Translate("Zoom out"));
            AutomationProperties.SetName(ZoomInBtn, LocalizationService.Translate("Zoom in"));
        }

        private static void SetPreviewIcon(
            System.Windows.Controls.Image image,
            string iconId,
            System.Drawing.Color color,
            int displayDip)
        {
            image.Source = FluentIcons.RenderWpf(iconId, color, displayDip * 2, active: false);
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        }

        private static string WithHotkeyHint(string text, string shortcut)
            => string.IsNullOrWhiteSpace(text) ? $"({shortcut})" : $"{text} ({shortcut})";

        private void GetPhysicalBaseDimensions(BitmapSource bmp, out double baseW, out double baseH)
        {
            double dpiScale = 1.0;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                if (dpi.DpiScaleX > 0)
                    dpiScale = dpi.DpiScaleX;
            }
            catch { }

            double uiScale = UiScale.Current > 0 ? UiScale.Current : 1.0;
            double physicalToDip = 1.0 / (dpiScale * uiScale);
            baseW = bmp.PixelWidth * physicalToDip;
            baseH = bmp.PixelHeight * physicalToDip;
        }

        private void ApplyZoom()
        {
            if (GifPreviewImage.Source is not BitmapSource bmp) return;

            double availW = ZoomViewport.ViewportWidth;
            double availH = ZoomViewport.ViewportHeight;
            if (availW <= 0 || availH <= 0) return;

            GetPhysicalBaseDimensions(bmp, out double baseW, out double baseH);

            if (_zoomToFit)
            {
                ZoomCanvas.Width = availW;
                ZoomCanvas.Height = availH;
                _currentZoom = ComputeFitZoom(availW, availH, baseW, baseH);
                GifPreviewImage.Width = baseW * _currentZoom;
                GifPreviewImage.Height = baseH * _currentZoom;
            }
            else
            {
                double scaledW = _currentZoom * baseW;
                double scaledH = _currentZoom * baseH;
                ZoomCanvas.Width = Math.Max(availW, scaledW);
                ZoomCanvas.Height = Math.Max(availH, scaledH);
                GifPreviewImage.Width = scaledW;
                GifPreviewImage.Height = scaledH;
            }

            UpdateZoomLevelText();
            UpdateZoomCursor();
            UpdateZoomControlsVisibility();
            UpdateZoomViewButton();
        }

        /// <summary>
        /// First layout: shrink oversized videos so the whole frame is visible, but keep
        /// small videos at 1:1. User-requested Fit (the toggle) may still enlarge.
        /// </summary>
        private void TryApplyInitialContain()
        {
            if (_didInitialContain) return;
            if (GifPreviewImage.Source is not BitmapSource bmp) return;
            double availW = ZoomViewport.ViewportWidth;
            double availH = ZoomViewport.ViewportHeight;
            if (availW <= 0 || availH <= 0) return;

            _didInitialContain = true;
            GetPhysicalBaseDimensions(bmp, out double baseW, out double baseH);
            if (baseW > availW || baseH > availH)
                ZoomToFitWindow();
        }

        private static double ComputeFitZoom(double availW, double availH, double baseW, double baseH)
        {
            if (baseW <= 0 || baseH <= 0) return 1.0;
            double fit = Math.Min(availW / baseW, availH / baseH) * 0.95;
            return Math.Clamp(fit, ZoomMin, ZoomMax);
        }

        private bool IsZoomFitted()
        {
            if (_zoomToFit) return true;
            if (GifPreviewImage.Source is not BitmapSource bmp) return false;
            double availW = ZoomViewport.ViewportWidth;
            double availH = ZoomViewport.ViewportHeight;
            if (availW <= 0 || availH <= 0) return false;
            GetPhysicalBaseDimensions(bmp, out double baseW, out double baseH);
            return Math.Abs(_currentZoom - ComputeFitZoom(availW, availH, baseW, baseH)) < 0.02;
        }

        private void ToggleZoomView()
        {
            if (IsZoomFitted())
                ZoomActualSize();
            else
                ZoomToFitWindow();
        }

        private void UpdateZoomViewButton()
        {
            if (ZoomFitBtn is null || ZoomFitIcon is null) return;
            bool fitted = IsZoomFitted();
            var c = Theme.TextSecondary;
            var iconColor = System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
            SetPreviewIcon(ZoomFitIcon, fitted ? "fullscreen" : "zoomFit", iconColor, 12);
            string key = fitted ? "Original size" : "Fit to window";
            ZoomFitBtn.ToolTip = WithHotkeyHint(LocalizationService.Translate(key), "Ctrl+0");
            AutomationProperties.SetName(ZoomFitBtn, LocalizationService.Translate(key));
        }

        private void UpdateZoomLevelText()
        {
            if (ZoomLevelText is null) return;
            ZoomLevelText.Text = $"{(_currentZoom * 100):0}%";
        }

        private void UpdateZoomCursor()
        {
            bool canPan = !_zoomToFit
                && (ZoomViewport.ScrollableWidth > 1 || ZoomViewport.ScrollableHeight > 1);
            if (!_isPanning)
                ZoomViewport.Cursor = canPan ? System.Windows.Input.Cursors.SizeAll : System.Windows.Input.Cursors.Arrow;
        }

        private void UpdateZoomControlsVisibility()
        {
            bool overlayBusy = ProgressOverlay is { Visibility: Visibility.Visible };
            bool shouldShow = _zoomPointerInside
                && !overlayBusy
                && GifPreviewImage.Source is BitmapSource;
            SetZoomOverlayVisibility(shouldShow);
        }

        private void SetZoomOverlayVisibility(bool visible)
        {
            double target = visible ? 1.0 : 0.0;
            ZoomControlsOverlay.IsHitTestVisible = visible;
            var fade = new DoubleAnimation(target, Motion.Ms(180))
            {
                EasingFunction = Motion.Ease(visible ? Motion.SmoothOut : Motion.SmoothIn)
            };
            ZoomControlsOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void CancelZoomHideTimer()
        {
            if (_zoomHideTimer != null)
            {
                _zoomHideTimer.Stop();
                _zoomHideTimer = null;
            }
        }

        private void ScheduleZoomControlsHide()
        {
            CancelZoomHideTimer();
            _zoomHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _zoomHideTimer.Tick += (_, _) =>
            {
                _zoomHideTimer!.Stop();
                _zoomHideTimer = null;
                SetZoomOverlayVisibility(false);
            };
            _zoomHideTimer.Start();
        }

        private void ZoomToFixedPos(System.Windows.Point viewportPos, double newZoom)
        {
            if (GifPreviewImage.Source is not BitmapSource bmp) return;

            newZoom = Math.Clamp(newZoom, ZoomMin, ZoomMax);

            double vpW = ZoomViewport.ViewportWidth;
            double vpH = ZoomViewport.ViewportHeight;
            if (vpW <= 0 || vpH <= 0) return;

            GetPhysicalBaseDimensions(bmp, out double baseW, out double baseH);

            double oldScale = Math.Min(GifPreviewImage.ActualWidth / baseW, GifPreviewImage.ActualHeight / baseH);
            double contentW = baseW * oldScale;
            double contentH = baseH * oldScale;
            double padX = GifPreviewImage.ActualWidth > 0 ? Math.Max(0, (GifPreviewImage.ActualWidth - contentW) / 2) : 0;
            double padY = GifPreviewImage.ActualHeight > 0 ? Math.Max(0, (GifPreviewImage.ActualHeight - contentH) / 2) : 0;

            var ptInSv = ZoomViewport.TranslatePoint(viewportPos, this);
            double contentX = ZoomViewport.HorizontalOffset + ptInSv.X - ZoomCanvas.Margin.Left;
            double contentY = ZoomViewport.VerticalOffset + ptInSv.Y - ZoomCanvas.Margin.Top;

            double relX = contentW > 0 ? Math.Clamp((contentX - padX) / contentW, 0, 1) : 0.5;
            double relY = contentH > 0 ? Math.Clamp((contentY - padY) / contentH, 0, 1) : 0.5;

            double oldZoom = _currentZoom;
            _currentZoom = newZoom;
            _zoomToFit = false;
            ApplyZoom();
            ZoomViewport.UpdateLayout();

            if (Math.Abs(_currentZoom - oldZoom) < double.Epsilon)
                return;

            double newContentX = relX * (_currentZoom * baseW);
            double newContentY = relY * (_currentZoom * baseH);
            ZoomViewport.ScrollToHorizontalOffset(newContentX - ptInSv.X);
            ZoomViewport.ScrollToVerticalOffset(newContentY - ptInSv.Y);
        }

        private void ZoomToFitWindow()
        {
            _currentZoom = 1.0;
            _zoomToFit = true;
            ApplyZoom();
        }

        private void ZoomActualSize()
        {
            _currentZoom = 1.0;
            _zoomToFit = false;
            ApplyZoom();
            ZoomViewport.UpdateLayout();

            double offX = Math.Max(0, (ZoomViewport.ExtentWidth - ZoomViewport.ViewportWidth) / 2);
            double offY = Math.Max(0, (ZoomViewport.ExtentHeight - ZoomViewport.ViewportHeight) / 2);
            ZoomViewport.ScrollToHorizontalOffset(offX);
            ZoomViewport.ScrollToVerticalOffset(offY);
        }

        private void ZoomInBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomToFixedPos(
                new System.Windows.Point(ZoomViewport.ViewportWidth / 2, ZoomViewport.ViewportHeight / 2),
                _currentZoom * ZoomStep);
        }

        private void ZoomOutBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomToFixedPos(
                new System.Windows.Point(ZoomViewport.ViewportWidth / 2, ZoomViewport.ViewportHeight / 2),
                _currentZoom / ZoomStep);
        }

        private void ZoomFitBtn_Click(object sender, RoutedEventArgs e)
        {
            ToggleZoomView();
        }

        private void ZoomLevelBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomActualSize();
        }

        private void ZoomViewport_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var posInViewport = e.GetPosition(ZoomViewport);
            double newZoom = e.Delta > 0
                ? _currentZoom * ZoomStep
                : _currentZoom / ZoomStep;
            ZoomToFixedPos(posInViewport, newZoom);
            e.Handled = true;
        }

        private void ZoomViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestorButton(e.OriginalSource as DependencyObject) != null)
                return;

            if (!_zoomToFit && _currentZoom > 0
                && (ZoomViewport.ScrollableWidth > 1 || ZoomViewport.ScrollableHeight > 1))
            {
                _isPanning = true;
                _panStart = e.GetPosition(ZoomViewport);
                _panStartHorizontalOffset = ZoomViewport.HorizontalOffset;
                _panStartVerticalOffset = ZoomViewport.VerticalOffset;
                ZoomViewport.CaptureMouse();
                ZoomViewport.Cursor = System.Windows.Input.Cursors.SizeAll;
                e.Handled = true;
            }
        }

        private static System.Windows.Controls.Button? FindAncestorButton(DependencyObject? start)
        {
            for (var current = start; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is System.Windows.Controls.Button button)
                    return button;
            }
            return null;
        }

        private void ZoomViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                ZoomViewport.ReleaseMouseCapture();
                UpdateZoomCursor();
                e.Handled = true;
            }
        }

        private void ZoomViewport_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_isPanning && e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(ZoomViewport);
                double dx = _panStart.X - pos.X;
                double dy = _panStart.Y - pos.Y;
                ZoomViewport.ScrollToHorizontalOffset(_panStartHorizontalOffset + dx);
                ZoomViewport.ScrollToVerticalOffset(_panStartVerticalOffset + dy);
            }
        }

        private void ZoomViewport_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _zoomPointerInside = true;
            CancelZoomHideTimer();
            UpdateZoomControlsVisibility();
        }

        private void ZoomViewport_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _zoomPointerInside = false;
            ScheduleZoomControlsHide();
        }

        private bool TryHandleZoomHotkey(System.Windows.Input.KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
                return false;

            if (e.Key is Key.Add or Key.OemPlus)
            {
                ZoomInBtn_Click(ZoomInBtn, new RoutedEventArgs());
                e.Handled = true;
                return true;
            }
            if (e.Key is Key.Subtract or Key.OemMinus)
            {
                ZoomOutBtn_Click(ZoomOutBtn, new RoutedEventArgs());
                e.Handled = true;
                return true;
            }
            if (e.Key is Key.D0 or Key.NumPad0)
            {
                ToggleZoomView();
                e.Handled = true;
                return true;
            }
            if (e.Key is Key.D1 or Key.NumPad1)
            {
                ZoomActualSize();
                e.Handled = true;
                return true;
            }

            return false;
        }
    }
}
