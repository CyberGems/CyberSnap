using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CyberSnap.Services;
using CyberSnap.Helpers;
using CyberSnap.Capture;

namespace CyberSnap.UI
{
    public partial class VideoTrimmerWindow : Window
    {
        private readonly string _mediaFilePath;
        private readonly SettingsService _settingsService;
        private DateTime _lastPlaybackUpdate;
        private bool _isSliderDragging;
        private double _videoDurationSeconds;
        private bool _isPinned = false;

        private double _startTimeSeconds;
        private double _endTimeSeconds;
        private double _fps = 30.0;
        private double _lastTargetSeekSeconds = -1;
        private bool _hasAudioTrack;
        private readonly bool _isGif;
        private readonly DispatcherTimer _audioPersistTimer;
        private bool _audioPersistPending;
        private bool _trimButtonVisible;
        private double _trimButtonExpandedWidth;
        private bool _detailedTimeDisplay = true;

        public VideoTrimmerWindow(string filePath, SettingsService settingsService)
        {
            _mediaFilePath = filePath;
            _settingsService = settingsService;
            _isGif = string.Equals(Path.GetExtension(filePath), ".gif", StringComparison.OrdinalIgnoreCase);

            _audioPersistTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _audioPersistTimer.Tick += (_, _) =>
            {
                _audioPersistTimer.Stop();
                if (!_audioPersistPending)
                    return;

                _audioPersistPending = false;
                if (Application.Current is App app)
                    app.PersistVideoTrimmerAudio(VolumeControl.Volume, VolumeControl.IsExportMuted);
            };
            
            InitializeComponent();

            CyberSnapWindowChrome.Apply(this);
            UiScale.Set(settingsService.Settings.UiScale);
            UiScale.ApplyToWindow(this, RootBorder, scaleWindowBounds: true);
            
            Theme.Refresh();
            ApplyTheme();
            
            LocalizationService.ApplyCurrentCulture(settingsService.Settings.InterfaceLanguage);
            LocalizationService.ApplyTo(this, settingsService.Settings.InterfaceLanguage);
            
            _lastPlaybackUpdate = DateTime.UtcNow;
            CompositionTarget.Rendering += OnRendering;
            
            // Determine FPS from settings
            _fps = _isGif ? settingsService.Settings.GifFps : settingsService.Settings.RecordingFps;
            if (_fps <= 0) _fps = 30.0;

            InitializeVolumeControl();
            CacheTrimButtonWidth();
            SetTrimButtonVisible(false, animate: false);

            // Set up Pin/Topmost state (default is Off)
            TrimmerTitleBar.IsPinActive = _isPinned;
            Topmost = _isPinned;
            
            UpdatePlayPauseToolTip();
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
            VolumeControl.RefreshThemeBrushes();
            Icon = ThemedLogo.Square(32);
        }
        
        private void OnRendering(object? sender, EventArgs e)
        {
            // Throttle to ~20 updates/sec (50 ms), matching the original timer interval
            var now = DateTime.UtcNow;
            if ((now - _lastPlaybackUpdate).TotalMilliseconds < 50)
                return;
            _lastPlaybackUpdate = now;

            if (!_isSliderDragging && _videoDurationSeconds > 0)
            {
                double current = MediaPlayer.Position.TotalSeconds;
                bool isPlaying = PlayPauseIconText.Text == "\uE769";
                
                if (isPlaying && _endTimeSeconds > 0 && current >= _endTimeSeconds)
                {
                    if (Math.Abs(_lastTargetSeekSeconds - _startTimeSeconds) > 0.01)
                    {
                        _lastTargetSeekSeconds = _startTimeSeconds;
                        MediaPlayer.Position = TimeSpan.FromSeconds(_startTimeSeconds);
                    }
                }
                else
                {
                    if (current < _endTimeSeconds - 0.2)
                    {
                        _lastTargetSeekSeconds = -1;
                    }
                    TimeSlider.Value = current;
                    UpdateTimeStatus();
                }
            }
        }
        
        private void MediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (MediaPlayer.NaturalDuration.HasTimeSpan)
            {
                _videoDurationSeconds = MediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                TimeSlider.Maximum = _videoDurationSeconds;
                
                // Initialize range markers to full duration
                _startTimeSeconds = 0;
                _endTimeSeconds = _videoDurationSeconds;
                
                RefreshTimeDisplay();
                UpdateMarkerLabels();
                EvaluateCropState();
                
                // MediaOpened means the decoder graph is ready.  Seek to the
                // start and play directly — no deferral needed.  Combined with
                // the 1-second keyframe interval set during encoding (-g), this
                // ensures the very first frame is decoded and displayed.
                MediaPlayer.Position = TimeSpan.Zero;
                MediaPlayer.Play();
            }
        }
        
        private void MediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            MediaPlayer.Position = TimeSpan.FromSeconds(_startTimeSeconds);
            MediaPlayer.Play();
        }
        
        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (PlayPauseIconText.Text == "\uE768") // Play glyph
            {
                MediaPlayer.Play();
                PlayPauseIconText.Text = "\uE769"; // Pause glyph
            }
            else
            {
                MediaPlayer.Pause();
                PlayPauseIconText.Text = "\uE768";
            }
            UpdatePlayPauseToolTip();
        }

        private void UpdatePlayPauseToolTip()
        {
            string lang = _settingsService.Settings.InterfaceLanguage;
            PlayPauseBtn.ToolTip = PlayPauseIconText.Text == "\uE768"
                ? LocalizationService.Translate(lang, "Play")
                : LocalizationService.Translate(lang, "Pause");
        }

        private void InitializeVolumeControl()
        {
            string lang = _settingsService.Settings.InterfaceLanguage;
            VolumeControl.InterfaceLanguage = lang;
            VolumeControl.Volume = Math.Clamp(_settingsService.Settings.VideoTrimmerVolume, 0.0, 1.0);
            VolumeControl.IsExportMuted = _settingsService.Settings.VideoTrimmerExportMuted;
            VolumeControl.UpdateTooltips();

            if (_isGif)
            {
                VolumeControl.SetGifMode();
                return;
            }

            _hasAudioTrack = MediaHasAudioTrack(_mediaFilePath);
            if (!_hasAudioTrack)
            {
                VolumeControl.SetNoAudioMode();
                return;
            }

            VolumeControl.HasAudioTrack = true;
            SyncMediaAudio();
        }

        private void SyncMediaAudio()
        {
            if (!_hasAudioTrack)
                return;

            MediaPlayer.Volume = VolumeControl.Volume;
            MediaPlayer.IsMuted = VolumeControl.IsExportMuted || VolumeControl.Volume <= 0.001;
        }

        private void ScheduleAudioPersist()
        {
            _audioPersistPending = true;
            _audioPersistTimer.Stop();
            _audioPersistTimer.Start();
        }

        private void VolumeControl_VolumeChanged(object sender, RoutedEventArgs e)
        {
            SyncMediaAudio();
            ScheduleAudioPersist();
        }

        private void VolumeControl_ExportMuteChanged(object sender, RoutedEventArgs e)
        {
            SyncMediaAudio();
            ScheduleAudioPersist();
        }

        private static bool MediaHasAudioTrack(string mediaPath)
        {
            string? ffmpeg = VideoRecorder.FindFfmpeg();
            if (ffmpeg == null)
                return false;

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-hide_banner -i \"{mediaPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });

                if (process == null)
                    return false;

                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                return stderr.Contains("Audio:", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string BuildTrimArguments(
            string input,
            string output,
            double start,
            double end,
            bool isGif,
            bool hasAudio,
            double volume,
            bool exportMuted)
        {
            string cultureStart = start.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            string cultureEnd = end.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

            if (isGif)
                return $"-y -ss {cultureStart} -to {cultureEnd} -i \"{input}\" \"{output}\"";

            if (!hasAudio || exportMuted || volume <= 0.001)
                return $"-y -ss {cultureStart} -to {cultureEnd} -i \"{input}\" -c:v copy -an \"{output}\"";

            if (Math.Abs(volume - 1.0) < 0.001)
                return $"-y -ss {cultureStart} -to {cultureEnd} -i \"{input}\" -c copy \"{output}\"";

            string cultureVolume = volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return $"-y -ss {cultureStart} -to {cultureEnd} -i \"{input}\" -map 0:v -map 0:a? -c:v copy -af \"volume={cultureVolume}\" -c:a aac -b:a 192k \"{output}\"";
        }

        private void StepBackBtn_Click(object sender, RoutedEventArgs e)
        {
            MediaPlayer.Pause();
            PlayPauseIconText.Text = "\uE768";
            UpdatePlayPauseToolTip();
            
            double target = MediaPlayer.Position.TotalSeconds - (1.0 / _fps);
            if (target < 0) target = 0;
            
            MediaPlayer.Position = TimeSpan.FromSeconds(target);
            TimeSlider.Value = target;
            UpdateTimeStatus();
        }

        private void StepForwardBtn_Click(object sender, RoutedEventArgs e)
        {
            MediaPlayer.Pause();
            PlayPauseIconText.Text = "\uE768";
            UpdatePlayPauseToolTip();
            
            double target = MediaPlayer.Position.TotalSeconds + (1.0 / _fps);
            if (target > _videoDurationSeconds) target = _videoDurationSeconds;
            
            MediaPlayer.Position = TimeSpan.FromSeconds(target);
            TimeSlider.Value = target;
            UpdateTimeStatus();
        }

        private void PausePlayback()
        {
            MediaPlayer.Pause();
            PlayPauseIconText.Text = "\uE768";
            UpdatePlayPauseToolTip();
        }

        private void StartSeekBtn_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();
            MediaPlayer.Position = TimeSpan.FromSeconds(_startTimeSeconds);
            TimeSlider.Value = _startTimeSeconds;
            UpdateTimeStatus();
        }

        private void EndSeekBtn_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();
            MediaPlayer.Position = TimeSpan.FromSeconds(_endTimeSeconds);
            TimeSlider.Value = _endTimeSeconds;
            UpdateTimeStatus();
        }

        private void PlaybackTimeBtn_Click(object sender, RoutedEventArgs e)
        {
            _detailedTimeDisplay = !_detailedTimeDisplay;
            RefreshTimeDisplay();
        }
        
        private void TimeSlider_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _isSliderDragging = true;
            PausePlayback();
        }
        
        private void TimeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isSliderDragging)
            {
                _isSliderDragging = false;
                MediaPlayer.Position = TimeSpan.FromSeconds(TimeSlider.Value);
            }
        }

        private void TimeSlider_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
        {
            // Safety net: if the Thumb's internal capture is lost unexpectedly,
            // ensure we clear the drag flag so OnRendering resumes updating.
            _isSliderDragging = false;
        }
        
        private void TimeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isSliderDragging)
            {
                MediaPlayer.Position = TimeSpan.FromSeconds(TimeSlider.Value);
                UpdateTimeStatus();
            }
        }
        
        private void UpdateTimeStatus()
        {
            RefreshTimeDisplay();
        }

        private void RefreshTimeDisplay()
        {
            if (_detailedTimeDisplay)
            {
                TimeStatusText.Text = FormatTime(MediaPlayer.Position.TotalSeconds);
                TimeStatusSeparator.Visibility = Visibility.Visible;
                DurationStatusText.Visibility = Visibility.Visible;
                DurationStatusText.Text = FormatTime(_videoDurationSeconds);
            }
            else
            {
                TimeStatusText.Text = FormatSimpleTime(MediaPlayer.Position.TotalSeconds);
                TimeStatusSeparator.Visibility = Visibility.Collapsed;
                DurationStatusText.Visibility = Visibility.Collapsed;
            }
        }
        
        private string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return $"{t.Minutes:D2}:{t.Seconds:D2}.{t.Milliseconds / 100:D1}";
        }

        private static string FormatSimpleTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(seconds);
            return $"{t.Minutes:D2}:{t.Seconds:D2}";
        }
        
        private void SetStartBtn_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();
            double current = MediaPlayer.Position.TotalSeconds;
            if (current < _endTimeSeconds)
            {
                _startTimeSeconds = current;
                UpdateMarkerLabels();
                EvaluateCropState();
            }
        }
        
        private void SetEndBtn_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();
            double current = MediaPlayer.Position.TotalSeconds;
            if (current > _startTimeSeconds)
            {
                _endTimeSeconds = current;
                UpdateMarkerLabels();
                EvaluateCropState();
            }
        }
        
        private void UpdateMarkerLabels()
        {
            StartPointText.Text = FormatTime(_startTimeSeconds);
            EndPointText.Text = FormatTime(_endTimeSeconds);

            UpdateRangeBarDisplay();
        }

        private void UpdateRangeBarDisplay()
        {
            if (_videoDurationSeconds <= 0) return;
            
            double startCut = _startTimeSeconds;
            double keep = _endTimeSeconds - _startTimeSeconds;
            double endCut = _videoDurationSeconds - _endTimeSeconds;
            
            // Ensure no negative values
            if (startCut < 0) startCut = 0;
            if (keep < 0) keep = 0;
            if (endCut < 0) endCut = 0;
            
            ColStartCut.Width = new GridLength(startCut, GridUnitType.Star);
            ColKeep.Width = new GridLength(keep, GridUnitType.Star);
            ColEndCut.Width = new GridLength(endCut, GridUnitType.Star);
        }
        
        private void EvaluateCropState()
        {
            bool isModified = _startTimeSeconds > 0.05 || _endTimeSeconds < (_videoDurationSeconds - 0.05);
            SetTrimButtonVisible(isModified);
        }

        private void CacheTrimButtonWidth()
        {
            TrimBtn.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            _trimButtonExpandedWidth = Math.Max(TrimBtn.DesiredSize.Width + TrimBtn.Margin.Right, 96);
        }

        private void SetTrimButtonVisible(bool visible, bool animate = true)
        {
            if (_trimButtonVisible == visible)
                return;

            _trimButtonVisible = visible;

            if (_trimButtonExpandedWidth <= 0)
                CacheTrimButtonWidth();

            if (!animate)
            {
                TrimBtnHost.BeginAnimation(FrameworkElement.MaxWidthProperty, null);
                TrimBtnHost.BeginAnimation(UIElement.OpacityProperty, null);
                TrimBtnHost.MaxWidth = visible ? _trimButtonExpandedWidth : 0;
                TrimBtnHost.Opacity = visible ? 1 : 0;
                TrimBtn.IsEnabled = visible;
                return;
            }

            TrimBtnHost.BeginAnimation(FrameworkElement.MaxWidthProperty, null);
            TrimBtnHost.BeginAnimation(UIElement.OpacityProperty, null);

            var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            var widthAnimation = new DoubleAnimation
            {
                From = visible ? 0 : _trimButtonExpandedWidth,
                To = visible ? _trimButtonExpandedWidth : 0,
                Duration = TimeSpan.FromMilliseconds(visible ? 200 : 160),
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            widthAnimation.Completed += (_, _) => TrimBtnHost.MaxWidth = visible ? _trimButtonExpandedWidth : 0;

            var opacityAnimation = new DoubleAnimation
            {
                From = visible ? 0 : 1,
                To = visible ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(visible ? 180 : 140),
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            };
            opacityAnimation.Completed += (_, _) => TrimBtnHost.Opacity = visible ? 1 : 0;

            TrimBtn.IsEnabled = visible;
            TrimBtnHost.BeginAnimation(FrameworkElement.MaxWidthProperty, widthAnimation);
            TrimBtnHost.BeginAnimation(UIElement.OpacityProperty, opacityAnimation);
        }
        
        private void TitleBar_CloseRequested(object? sender, EventArgs e)
        {
            Close();
        }

        private void TitleBar_PinRequested(object? sender, EventArgs e)
        {
            _isPinned = !_isPinned;
            TrimmerTitleBar.IsPinActive = _isPinned;
            Topmost = _isPinned;
        }
        
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            Activate();
            Focus();
            
            // Set source and call Pause() to kick-start the Media Foundation
            // pipeline.  With LoadedBehavior="Manual", Pause() is required to
            // trigger async media loading — without it, MediaOpened never fires.
            MediaPlayer.Source = new Uri(_mediaFilePath, UriKind.Absolute);
            MediaPlayer.Pause();
        }
        
        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        }
        
        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            CompositionTarget.Rendering -= OnRendering;
            _audioPersistTimer.Stop();
            if (_audioPersistPending && Application.Current is App app)
                app.PersistVideoTrimmerAudio(VolumeControl.Volume, VolumeControl.IsExportMuted);
            MediaPlayer.Close();
        }
        
        private async void TrimBtn_Click(object sender, RoutedEventArgs e)
        {
            string ext = Path.GetExtension(_mediaFilePath);
            string tempOut = Path.Combine(Path.GetDirectoryName(_mediaFilePath) ?? string.Empty, 
                                          $"trim_temp_{Guid.NewGuid()}{ext}");
            
            bool success = await RunFfmpegTrimAsync(_mediaFilePath, tempOut, _startTimeSeconds, _endTimeSeconds);
            if (success)
            {
                try
                {
                    CompositionTarget.Rendering -= OnRendering;
                    MediaPlayer.Close();
                    
                    // Give player time to release lock
                    System.Threading.Thread.Sleep(200);
                    
                    File.Delete(_mediaFilePath);
                    File.Move(tempOut, _mediaFilePath);
                    
                    // Reload original path
                    MediaPlayer.Source = new Uri(_mediaFilePath, UriKind.Absolute);
                    MediaPlayer.Play();
                    PlayPauseIconText.Text = "\uE769"; // Reset to Pause glyph as it plays immediately
                    UpdatePlayPauseToolTip();
                    CompositionTarget.Rendering += OnRendering;
                    
                    string lang = _settingsService.Settings.InterfaceLanguage;
                    ToastWindow.Show(
                        LocalizationService.Translate(lang, "Video trimmed"),
                        LocalizationService.Translate(lang, "Original file overwritten successfully."),
                        _mediaFilePath
                    );
                }
                catch (Exception ex)
                {
                    AppDiagnostics.LogError("trim.overwrite", ex);
                    string lang = _settingsService.Settings.InterfaceLanguage;
                    string errMsg = LocalizationService.Translate(lang, "Failed to overwrite original file: ");
                    MessageBox.Show($"{errMsg}{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        
        private async void SaveAsNewBtn_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.GetDirectoryName(_mediaFilePath) ?? string.Empty;
            string nameWithoutExt = Path.GetFileNameWithoutExtension(_mediaFilePath);
            string ext = Path.GetExtension(_mediaFilePath);
            string newPath = Path.Combine(dir, $"{nameWithoutExt}_edited{ext}");
            
            // Resolve conflict if file exists
            int counter = 1;
            while (File.Exists(newPath))
            {
                newPath = Path.Combine(dir, $"{nameWithoutExt}_edited_{counter}{ext}");
                counter++;
            }
            
            bool success = await RunFfmpegTrimAsync(_mediaFilePath, newPath, _startTimeSeconds, _endTimeSeconds);
            if (success)
            {
                string lang = _settingsService.Settings.InterfaceLanguage;
                ToastWindow.Show(
                    LocalizationService.Translate(lang, "Video saved"),
                    LocalizationService.Translate(lang, "Trimmed copy saved successfully."),
                    newPath
                );
            }
        }
        
        private async System.Threading.Tasks.Task<bool> RunFfmpegTrimAsync(string input, string output, double start, double end)
        {
            string lang = _settingsService.Settings.InterfaceLanguage;
            string? ffmpeg = VideoRecorder.FindFfmpeg();
            if (ffmpeg == null)
            {
                MessageBox.Show(
                    LocalizationService.Translate(lang, "FFmpeg binary not found."),
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return false;
            }
            
            ProgressOverlay.Visibility = Visibility.Visible;

            bool isGif = string.Equals(Path.GetExtension(input), ".gif", StringComparison.OrdinalIgnoreCase);
            double exportVolume = VolumeControl.Volume;
            bool exportMuted = VolumeControl.IsExportMuted;
            string args = BuildTrimArguments(input, output, start, end, isGif, _hasAudioTrack, exportVolume, exportMuted);
                
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                
                using (var process = new Process { StartInfo = psi })
                {
                    process.Start();
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode != 0)
                    {
                        string err = await process.StandardError.ReadToEndAsync();
                        AppDiagnostics.LogError("ffmpeg.trim-fail", new Exception(err));
                        MessageBox.Show(
                            $"{LocalizationService.Translate(lang, "Trim failed")} ({process.ExitCode}): {err}",
                            LocalizationService.Translate(lang, "Trim failed"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Error
                        );
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("ffmpeg.trim-exception", ex);
                MessageBox.Show(
                    $"{LocalizationService.Translate(lang, "Error running FFmpeg: ")}{ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
                return false;
            }
            finally
            {
                ProgressOverlay.Visibility = Visibility.Collapsed;
            }
        }
    }
}
