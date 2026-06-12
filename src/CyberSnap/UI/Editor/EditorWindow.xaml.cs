using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CyberSnap.Helpers;
using CyberSnap.Services;
using CyberSnap.UI.Controls;
using Color = System.Drawing.Color;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace CyberSnap.UI.Editor
{
    public partial class EditorWindow : Window
    {
        private static EditorWindow? _instance;
        public static EditorWindow? ActiveInstance => _instance is { IsVisible: true } ? _instance : null;

        private readonly AnnotationCanvas _canvas;
        private string? _savedFilePath;
        private bool _suppressCloseConfirm;
        private readonly DispatcherTimer _saveStatusTimer = new() { Interval = TimeSpan.FromMilliseconds(2200) };

        private readonly Dictionary<Color, ToggleButton> _colorButtons = new();
        private readonly List<ToggleButton> _strokeWidthButtons = new();
        private readonly Dictionary<AnnotationCanvas.CanvasTool, ToggleButton> _toolButtons = new();

        private static readonly Color[] PaletteColors =
        {
            Color.FromArgb(0, 255, 255),
            Color.FromArgb(0, 136, 255),
            Color.FromArgb(168, 85, 247),
            Color.FromArgb(255, 45, 85),
            Color.FromArgb(245, 158, 11),
            Color.FromArgb(234, 179, 8),
            Color.FromArgb(34, 197, 94),
            Color.White,
            Color.FromArgb(15, 23, 42),
            Color.FromArgb(236, 72, 153),
            Color.FromArgb(148, 163, 184),
            Color.Black,
        };

        private static readonly float[] StrokeWidths = { 2f, 3f, 4f, 6f, 10f };

        public static void ShowEditor(Bitmap captured, string? savedFilePath = null)
        {
            if (_instance is not null)
            {
                _instance.LoadCapture(captured, savedFilePath);
                _instance.RestoreAndActivate();
                return;
            }
            _instance = new EditorWindow(captured, savedFilePath);
            _instance.Show();
        }

        public static void ShowEditorFromFile(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    return;

                using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                using (var tempBmp = new Bitmap(stream))
                {
                    var captured = new Bitmap(tempBmp);
                    ShowEditor(captured, filePath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static void ShowEditorEmptyOrPrompt()
        {
            if (_instance is not null)
            {
                _instance.RestoreAndActivate();
                return;
            }

            var blank = new Bitmap(1024, 768);
            using (var g = Graphics.FromImage(blank))
            {
                var color1 = Color.FromArgb(20, 22, 33);
                var color2 = Color.FromArgb(28, 30, 43);
                g.Clear(color1);
                using (var brush = new SolidBrush(color2))
                {
                    int size = 16;
                    for (int y = 0; y < blank.Height; y += size)
                    {
                        for (int x = 0; x < blank.Width; x += size)
                        {
                            if (((x / size) + (y / size)) % 2 == 1)
                            {
                                g.FillRectangle(brush, x, y, size, size);
                            }
                        }
                    }
                }
            }
            ShowEditor(blank);
            if (_instance is not null)
            {
                _instance._canvas.IsDefaultBlank = true;
                _instance.RefreshUi();
                _instance._canvas.Invalidate();
            }
        }

        private void RestoreAndActivate()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            if (!IsVisible)
                Show();

            Activate();
            Focus();
            _canvas?.Focus();
        }

        public EditorWindow(Bitmap captured, string? savedFilePath = null)
        {
            InitializeComponent();
            _savedFilePath = savedFilePath;

            var settings = SettingsService.LoadStatic();
            _canvas = new AnnotationCanvas(new Bitmap(captured))
            {
                BackColor = System.Drawing.Color.FromArgb(8, 10, 16),
                ToolColor = settings != null ? Color.FromArgb(settings.EditorToolColorArgb) : Color.FromArgb(0, 255, 255),
                StrokeWidth = settings?.StrokeWidth ?? 4f,
                TextFontSize = settings?.EditorTextFontSize ?? 24f,
                FitToWindowOnLoad = settings?.EditorFitToWindowOnOpen ?? true,
                ShowBanners = settings?.EditorShowBanners ?? true,
                EditorAutoCropControls = settings?.EditorAutoCropControls ?? true,
            };

            _canvas.StateChanged += (_, _) => RefreshUi();
            _canvas.TextFontSizeChanged += size =>
            {
                if (Application.Current is CyberSnap.App app)
                    app.PersistEditorTextFontSize(size);
            };
            _canvas.MouseMove += OnCanvasMouseMove;
            _canvas.MouseUp += (_, _) => RefreshUi();
            _canvas.DoubleClick += (_, _) => { if (_canvas.IsDefaultBlank) DoOpen(); };

            _saveStatusTimer.Tick += (_, _) =>
            {
                _saveStatusTimer.Stop();
                RefreshUi();
            };

            // Host the canvas
            CanvasHost.Child = _canvas;

            // Load icons on top/bottom bar
            LoadWpfIcons();

            // Populate mapping dictionaries
            MapToolButtons();

            // Populate color swatches
            PopulateColors();

            // Populate stroke widths
            PopulateStrokeWidths();

            RefreshUi();

            Closing += OnWindowClosing;
            Closed += (_, _) =>
            {
                _saveStatusTimer.Stop();
                if (ReferenceEquals(_instance, this)) _instance = null;
            };

            // Hook global key bindings
            PreviewKeyDown += OnWindowPreviewKeyDown;
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            var helper = new WindowInteropHelper(this);
            CyberSnap.Native.Dwm.TrySetWindowCornerPreference(helper.Handle, CyberSnap.Native.Dwm.DWMWCP_ROUND);
        }

        private void LoadWpfIcons()
        {
            var white = System.Drawing.Color.FromArgb(230, 240, 255);
            var accent = System.Drawing.Color.FromArgb(0, 255, 255);
            var secondary = System.Drawing.Color.FromArgb(160, 180, 210);

            CoordsIcon.Source = FluentIcons.RenderWpf("select", accent, 20);
            DimsIcon.Source = FluentIcons.RenderWpf("rect", accent, 20);
            FileIcon.Source = FluentIcons.RenderWpf("camera", accent, 20);
        }

        private void MapToolButtons()
        {
            _toolButtons[AnnotationCanvas.CanvasTool.Pan] = ToolPan;
            _toolButtons[AnnotationCanvas.CanvasTool.Select] = ToolSelect;
            _toolButtons[AnnotationCanvas.CanvasTool.Crop] = ToolCrop;
            _toolButtons[AnnotationCanvas.CanvasTool.Eraser] = ToolEraser;
            _toolButtons[AnnotationCanvas.CanvasTool.Draw] = ToolDraw;
            _toolButtons[AnnotationCanvas.CanvasTool.Arrow] = ToolArrow;
            _toolButtons[AnnotationCanvas.CanvasTool.CurvedArrow] = ToolCurved;
            _toolButtons[AnnotationCanvas.CanvasTool.Line] = ToolLine;
            _toolButtons[AnnotationCanvas.CanvasTool.Rect] = ToolRect;
            _toolButtons[AnnotationCanvas.CanvasTool.Circle] = ToolCircle;
            _toolButtons[AnnotationCanvas.CanvasTool.Text] = ToolText;
            _toolButtons[AnnotationCanvas.CanvasTool.Highlight] = ToolHighlight;
            _toolButtons[AnnotationCanvas.CanvasTool.Blur] = ToolBlur;
            _toolButtons[AnnotationCanvas.CanvasTool.StepNumber] = ToolStep;
            _toolButtons[AnnotationCanvas.CanvasTool.Magnifier] = ToolMagnifier;
            _toolButtons[AnnotationCanvas.CanvasTool.Emoji] = ToolEmoji;
        }

        private void PopulateColors()
        {
            SwatchesGrid.Children.Clear();
            foreach (var color in PaletteColors)
            {
                var btn = new ToggleButton
                {
                    Width = 32,
                    Height = 32,
                    Margin = new Thickness(2),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B)),
                    BorderBrush = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    ToolTip = color.Name
                };
                
                btn.Click += (s, e) =>
                {
                    _canvas.ToolColor = color;
                    UpdateColorSwatchSelection();
                    if (Application.Current is CyberSnap.App app)
                        app.PersistEditorToolColor(color.ToArgb());
                };

                _colorButtons[color] = btn;
                SwatchesGrid.Children.Add(btn);
            }
        }

        private void PopulateStrokeWidths()
        {
            StrokeWidthGrid.Children.Clear();
            foreach (var w in StrokeWidths)
            {
                var btn = new ToggleButton
                {
                    Height = 32,
                    Width = 32,
                    Margin = new Thickness(2),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Content = new Border
                    {
                        Height = Math.Max(2, w),
                        Background = System.Windows.Media.Brushes.White,
                        CornerRadius = new CornerRadius(Math.Max(1, w / 2)),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
                    }
                };

                btn.Click += (s, e) =>
                {
                    _canvas.StrokeWidth = w;
                    UpdateStrokeWidthSelection();
                    if (Application.Current is CyberSnap.App app)
                        app.PersistEditorStrokeWidth(w);
                };

                _strokeWidthButtons.Add(btn);
                StrokeWidthGrid.Children.Add(btn);
            }
        }

        private void UpdateColorSwatchSelection()
        {
            foreach (var kv in _colorButtons)
            {
                kv.Value.IsChecked = kv.Key.ToArgb() == _canvas.ToolColor.ToArgb();
                kv.Value.BorderBrush = kv.Value.IsChecked == true ? System.Windows.Media.Brushes.Cyan : System.Windows.Media.Brushes.Transparent;
            }
        }

        private void UpdateStrokeWidthSelection()
        {
            foreach (var btn in _strokeWidthButtons)
            {
                var border = btn.Content as Border;
                if (border != null)
                {
                    bool isChecked = Math.Abs(border.Height - _canvas.StrokeWidth) < 0.01f;
                    btn.IsChecked = isChecked;
                    btn.BorderBrush = isChecked ? System.Windows.Media.Brushes.Cyan : System.Windows.Media.Brushes.Transparent;
                }
            }
        }

        private void OnCanvasMouseMove(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            var img = _canvas.PointFromScreenToImage(e.Location);
            LblCoords.Text = $"{img.X}, {img.Y}";
        }

        private void RefreshUi()
        {
            UpdateZoomStatus();
            if (!_saveStatusTimer.IsEnabled)
            {
                LblHint.Text = "";
                LblHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                LblHint.Visibility = Visibility.Visible;
            }
            UpdateToolButtonState();
            UpdateCaptureCaption();
        }

        private void UpdateZoomStatus()
        {
            int pct = (int)Math.Round(_canvas.Zoom * 100);
            LblZoom.Text = $"{pct}%";
            SldZoom.Value = Math.Clamp(pct, SldZoom.Minimum, SldZoom.Maximum);
        }

        private void UpdateToolButtonState()
        {
            var active = _canvas.ActiveTool;
            foreach (var kv in _toolButtons)
            {
                kv.Value.IsChecked = kv.Key == active;
            }
            UpdateColorSwatchSelection();
            UpdateStrokeWidthSelection();
        }

        private void UpdateCaptureCaption()
        {
            var bitmap = _canvas.BaseBitmap;
            if (bitmap == null) return;
            LblDims.Text = $"{bitmap.Width} x {bitmap.Height}";
            LblFileName.Text = string.IsNullOrWhiteSpace(_savedFilePath) ? "Unsaved capture" : Path.GetFileName(_savedFilePath);
        }

        private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_suppressCloseConfirm || !_canvas.IsDirty) return;
            
            var helper = new WindowInteropHelper(this);
            var discard = ThemedConfirmDialog.Confirm(
                helper.Handle,
                "Unsaved changes",
                "Discard changes?",
                "Discard",
                "Keep editing",
                danger: false);

            if (!discard)
                e.Cancel = true;
        }

        private void OnWindowPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control) { DoOpen(); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { DoSave(); e.Handled = true; }
            else if (e.Key == Key.S && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) { DoSaveAs(); e.Handled = true; }
            else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { DoCopy(); e.Handled = true; }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) { DoPaste(); e.Handled = true; }
        }

        private void TitleBar_CloseRequested(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            _canvas.Undo();
            RefreshUi();
        }

        private void BtnRedo_Click(object sender, RoutedEventArgs e)
        {
            _canvas.Redo();
            RefreshUi();
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            DoOpen();
        }

        private void BtnPaste_Click(object sender, RoutedEventArgs e)
        {
            DoPaste();
        }

        private void BtnCopy_Click(object sender, RoutedEventArgs e)
        {
            DoCopy();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            DoSave();
        }

        private void BtnSaveAs_Click(object sender, RoutedEventArgs e)
        {
            DoSaveAs();
        }

        private void Tool_Checked(object sender, RoutedEventArgs e)
        {
            var toggle = sender as ToggleButton;
            if (toggle == null) return;

            var activeTool = _toolButtons.FirstOrDefault(x => x.Value == toggle).Key;
            _canvas.ActiveTool = activeTool;
            UpdateToolButtonState();
        }

        private void SldZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_canvas != null)
            {
                _canvas.ZoomToPercent((int)e.NewValue);
                if (LblZoom != null) LblZoom.Text = $"{(int)e.NewValue}%";
            }
        }

        private void BtnResetZoom_Click(object sender, RoutedEventArgs e)
        {
            _canvas.ZoomReset();
            RefreshUi();
        }

        private void BtnFitZoom_Click(object sender, RoutedEventArgs e)
        {
            _canvas.ZoomFit();
            RefreshUi();
        }

        private void ChkAutoFit_Changed(object sender, RoutedEventArgs e)
        {
            if (_canvas == null) return;
            bool isChecked = ChkAutoFit.IsChecked == true;
            _canvas.FitToWindowOnLoad = isChecked;
            _canvas.ApplyInitialView();
            if (Application.Current is CyberSnap.App app)
                app.PersistEditorFitPreference(isChecked);
        }

        private void DoSave()
        {
            try
            {
                using var output = _canvas.RenderFinal();
                if (!string.IsNullOrWhiteSpace(_savedFilePath))
                {
                    SaveRenderedBitmap(output, _savedFilePath!);
                }
                else
                {
                    DoSaveAs(output);
                    return;
                }
                FinishSuccessfulSave(output, _savedFilePath!);
            }
            catch (Exception ex)
            {
                var helper = new WindowInteropHelper(this);
                ThemedConfirmDialog.Alert(helper.Handle, "Save failed", ex.Message, error: true);
            }
        }

        private void DoSaveAs()
        {
            using var output = _canvas.RenderFinal();
            DoSaveAs(output);
        }

        private void DoSaveAs(Bitmap output)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG|*.png|JPEG|*.jpg",
                FileName = string.IsNullOrWhiteSpace(_savedFilePath)
                    ? $"CyberSnap_{DateTime.Now:yyyyMMdd_HHmmss}.png"
                    : Path.GetFileNameWithoutExtension(_savedFilePath) + "_edited.png",
                DefaultExt = ".png",
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                SaveRenderedBitmap(output, dlg.FileName);
                _savedFilePath = dlg.FileName;
                FinishSuccessfulSave(output, dlg.FileName);
            }
            catch (Exception ex)
            {
                var helper = new WindowInteropHelper(this);
                ThemedConfirmDialog.Alert(helper.Handle, "Save failed", ex.Message, error: true);
            }
        }

        private void SaveRenderedBitmap(Bitmap output, string filePath)
        {
            if (filePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                filePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                output.Save(filePath, ImageFormat.Jpeg);
                return;
            }
            CaptureOutputService.SavePng(output, filePath);
        }

        private void FinishSuccessfulSave(Bitmap output, string filePath)
        {
            _savedFilePath = filePath;
            _canvas.AcceptSavedBaseline(output);
            if (Application.Current is CyberSnap.App app)
                app.NotifyEditedCaptureSaved(filePath, output.Width, output.Height);
            
            UpdateCaptureCaption();
            RefreshUi();
            ShowSaveStatus(filePath);

            var fileName = Path.GetFileName(filePath);
            var toastTitle = LocalizationService.Translate("System Message");
            var toastBody = string.Format(LocalizationService.Translate("Saved: {0}"), fileName);
            ToastWindow.Show(toastTitle, toastBody, filePath);
        }

        private void ShowSaveStatus(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            LblHint.Text = string.IsNullOrWhiteSpace(fileName) ? "Saved" : $"Saved: {fileName}";
            LblHint.Visibility = Visibility.Visible;
            _saveStatusTimer.Stop();
            _saveStatusTimer.Start();
        }

        private void DoOpen()
        {
            if (_canvas.IsDirty)
            {
                var helper = new WindowInteropHelper(this);
                var discard = ThemedConfirmDialog.Confirm(
                    helper.Handle,
                    "Unsaved changes",
                    "Discard changes?",
                    "Discard",
                    "Keep editing",
                    danger: false);
                if (!discard)
                    return;
            }

            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tiff|All Files|*.*",
                Title = LocalizationService.Translate("Open Image")
            };
            
            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    using (var stream = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read))
                    using (var tempBmp = new Bitmap(stream))
                    {
                        var captured = new Bitmap(tempBmp);
                        LoadCapture(captured, dlg.FileName);
                    }
                }
                catch (Exception ex)
                {
                    var helper = new WindowInteropHelper(this);
                    ThemedConfirmDialog.Alert(helper.Handle, "Error loading image", ex.Message, error: true);
                }
            }
        }

        private void LoadCapture(Bitmap captured, string? savedFilePath)
        {
            _savedFilePath = savedFilePath;
            _canvas.ResetState(new Bitmap(captured));
            _suppressCloseConfirm = false;
            UpdateCaptureCaption();
            RefreshUi();
        }

        private void DoCopy()
        {
            try
            {
                using var output = _canvas.RenderFinal();
                ClipboardService.CopyToClipboard(output, _savedFilePath);

                var toastTitle = LocalizationService.Translate("System Message");
                var toastBody = LocalizationService.Translate("Copied to clipboard");
                ToastWindow.Show(toastTitle, toastBody, _savedFilePath);
            }
            catch (Exception ex)
            {
                var helper = new WindowInteropHelper(this);
                ThemedConfirmDialog.Alert(helper.Handle, "Copy failed", ex.Message, error: true);
            }
        }

        private void DoPaste()
        {
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsImage())
                {
                    using (var img = System.Windows.Forms.Clipboard.GetImage())
                    {
                        if (img != null)
                        {
                            var bmp = new Bitmap(img);
                            var command = new CyberSnap.Models.Commands.PasteImageCommand(bmp);
                            _canvas.Push(command);
                            _canvas.ZoomFit();
                            _canvas.IsDefaultBlank = false;
                            RefreshUi();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var helper = new WindowInteropHelper(this);
                ThemedConfirmDialog.Alert(helper.Handle, "Paste failed", ex.Message, error: true);
            }
        }
    }
}
