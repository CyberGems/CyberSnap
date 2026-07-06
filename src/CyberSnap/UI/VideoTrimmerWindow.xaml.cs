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
using System.Windows.Controls.Primitives;

namespace CyberSnap.UI
{
    public partial class VideoTrimmerWindow : Window
    {
        private string _mediaFilePath;
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
        private bool _suppressLoadBanner;
        private readonly DispatcherTimer _audioPersistTimer;
        private bool _audioPersistPending;
        private bool _trimButtonVisible;
        private double _trimButtonExpandedWidth;
        private bool _detailedTimeDisplay = true;

        private static readonly SolidColorBrush GreenLedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
        private static readonly SolidColorBrush GreenLedAuraBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 34, 197, 94));
        private static readonly SolidColorBrush OrangeLedBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
        private static readonly SolidColorBrush OrangeLedAuraBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 245, 158, 11));

        private DispatcherTimer? _bannerTimer;
        private int _bannerTicks;

        private double _userTargetSeconds = -1;
        private DateTime _lastUserDragTime = DateTime.MinValue;
        private DateTime _gifPlayAnchorUtc;
        private double _gifPlayAnchorPosition;
        private double _gifPausedTimelineSeconds;
        private bool _gifTimelinePaused;
        private GifFrameSequence? _gifSequence;
        private int _gifDisplayedFrameIndex = -1;

        public VideoTrimmerWindow(string filePath, SettingsService settingsService)
        {
            _mediaFilePath = Path.GetFullPath(filePath);
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

            // Register with handledEventsToo=true so these handlers fire even
            // when the Slider's IsMoveToPointEnabled class handler marks the
            // tunneling PreviewMouseDown event as Handled (which suppresses
            // normal XAML-registered instance handlers).
            TimeSlider.AddHandler(UIElement.PreviewMouseDownEvent,
                new MouseButtonEventHandler(TimeSlider_PreviewMouseDown), true);
            TimeSlider.AddHandler(UIElement.PreviewMouseUpEvent,
                new MouseButtonEventHandler(TimeSlider_PreviewMouseUp), true);

            CyberSnapWindowChrome.Apply(this);
            UiScale.Set(settingsService.Settings.UiScale);
            UiScale.ApplyToWindow(this, RootBorder, scaleWindowBounds: true);
            
            Theme.Refresh();
            ApplyTheme();
            
            LocalizationService.ApplyCurrentCulture(settingsService.Settings.InterfaceLanguage);
            LocalizationService.ApplyTo(this, settingsService.Settings.InterfaceLanguage);
            TrimmerTitleBar.Title = LocalizationService.Translate("Video & GIF Trimmer");
            
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

            // Setup dynamic colors for the action banner overlay to match the Annotation Editor style precisely
            var accentBrush = Theme.Brush(Theme.Accent) as SolidColorBrush;
            var cardBrush = Theme.Brush(Theme.BgCard) as SolidColorBrush;
            if (accentBrush != null && cardBrush != null)
            {
                BannerBorder.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, cardBrush.Color.R, cardBrush.Color.G, cardBrush.Color.B));
                BannerBorder.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, accentBrush.Color.R, accentBrush.Color.G, accentBrush.Color.B));
                
                var shadow = BannerBorder.Effect as System.Windows.Media.Effects.DropShadowEffect;
                if (shadow != null)
                {
                    shadow.Color = accentBrush.Color;
                }
            }
        }

        private void TitleBar_DragWindow(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                if (e.ClickCount == 2)
                {
                    this.WindowState = this.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                }
                else
                {
                    this.DragMove();
                }
            }
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
                bool isPlaying = PlayPauseIconText.Text == "\uE769";

                if (_isGif)
                {
                    if (isPlaying)
                    {
                        double timeline = GetGifTimelinePosition();

                        if (timeline >= _endTimeSeconds - 0.02)
                        {
                            if (Math.Abs(_lastTargetSeekSeconds - _startTimeSeconds) > 0.01)
                            {
                                RestartGifLoop();
                                return;
                            }
                        }
                        else if (timeline < _endTimeSeconds - 0.2)
                        {
                            _lastTargetSeekSeconds = -1;
                        }

                        UpdateGifFrameDisplay(timeline);
                        TimeSlider.Value = timeline;
                        UpdateTimeStatus();
                    }

                    return;
                }

                double current = MediaPlayer.Position.TotalSeconds;

                // Prevent jump-back glitches during asynchronous media seek operations
                if (_userTargetSeconds >= 0)
                {
                    if (Math.Abs(current - _userTargetSeconds) < 0.25 || (now - _lastUserDragTime).TotalMilliseconds > 500)
                    {
                        _userTargetSeconds = -1;
                    }
                    else
                    {
                        return;
                    }
                }

                if (isPlaying && _endTimeSeconds > 0 && current >= _endTimeSeconds)
                {
                    if (Math.Abs(_lastTargetSeekSeconds - _startTimeSeconds) > 0.01)
                    {
                        _lastTargetSeekSeconds = _startTimeSeconds;
                        MediaPlayer.Position = TimeSpan.FromSeconds(_startTimeSeconds);
                        Dispatcher.BeginInvoke(new Action(() => MediaPlayer.Play()), DispatcherPriority.Background);
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
        
        private void DisposeGifSequence()
        {
            _gifSequence?.Dispose();
            _gifSequence = null;
            _gifDisplayedFrameIndex = -1;
        }

        private void LoadGifPreview()
        {
            DisposeGifSequence();

            try
            {
                _gifSequence = GifFrameSequence.Open(_mediaFilePath, Math.Max(1, (int)Math.Round(1000.0 / _fps)));
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogError("trimmer.gif-load", ex);
                ToastWindow.ShowError("Media load failed", ex.Message);
                return;
            }

            MediaPlayer.Visibility = Visibility.Collapsed;
            GifPreviewImage.Visibility = Visibility.Visible;

            double duration = _gifSequence.TotalDurationSeconds;
            if (duration <= 0.05)
                duration = ResolveGifDuration(0);
            else
            {
                double metadataDuration = ResolveGifDuration(0);
                if (metadataDuration > duration)
                    duration = metadataDuration;
            }

            _videoDurationSeconds = duration;
            TimeSlider.Maximum = _videoDurationSeconds;
            _startTimeSeconds = 0;
            _endTimeSeconds = _videoDurationSeconds;

            SetStartBtn.IsChecked = false;
            SetEndBtn.IsChecked = false;

            RefreshTimeDisplay();
            UpdateMarkerLabels();
            EvaluateCropState();

            ShowBanner(LocalizationService.Translate(_settingsService.Settings.InterfaceLanguage, "GIF loaded"));

            ResetGifPlayAnchor(0);
            _gifTimelinePaused = false;
            UpdateGifFrameDisplay(0, force: true);
            PlayPauseIconText.Text = "\uE769";
            UpdatePlayPauseToolTip();
        }

        private void UpdateGifFrameDisplay(double seconds, bool force = false)
        {
            if (_gifSequence == null)
                return;

            int frameIndex = _gifSequence.GetFrameIndexAt(seconds);
            if (!force && frameIndex == _gifDisplayedFrameIndex)
                return;

            GifPreviewImage.Source = _gifSequence.GetFrameSource(frameIndex);
            _gifDisplayedFrameIndex = frameIndex;
        }

        private void RestartGifLoop()
        {
            _lastTargetSeekSeconds = _startTimeSeconds;
            ResetGifPlayAnchor(_startTimeSeconds);
            UpdateGifFrameDisplay(_startTimeSeconds, force: true);
        }

        private void ResetGifPlayAnchor(double timelineSeconds)
        {
            _gifPlayAnchorUtc = DateTime.UtcNow;
            _gifPlayAnchorPosition = timelineSeconds;
            _gifPausedTimelineSeconds = timelineSeconds;
        }

        private double GetGifTimelinePosition()
        {
            if (_gifTimelinePaused || PlayPauseIconText.Text == "\uE768")
                return _gifPausedTimelineSeconds;

            double elapsed = (DateTime.UtcNow - _gifPlayAnchorUtc).TotalSeconds;
            return Math.Min(_gifPlayAnchorPosition + elapsed, _endTimeSeconds);
        }

        private double GetCurrentTimelineSeconds()
        {
            return _isGif ? GetGifTimelinePosition() : MediaPlayer.Position.TotalSeconds;
        }

        private void SyncGifTimelineAfterSeek(double timelineSeconds)
        {
            timelineSeconds = Math.Clamp(timelineSeconds, 0, _videoDurationSeconds);
            _gifPausedTimelineSeconds = timelineSeconds;
            _gifPlayAnchorPosition = timelineSeconds;
            _gifPlayAnchorUtc = DateTime.UtcNow;
        }

        private double ResolveGifDuration(double naturalDurationSeconds)
        {
            double ffprobeDuration = GetMediaDurationViaFfprobe(_mediaFilePath);
            if (ffprobeDuration > 0.05)
                return ffprobeDuration;

            double frameDuration = GetGifDurationFromFrameCount(_mediaFilePath, _fps);
            if (frameDuration > 0.05)
                return frameDuration;

            try
            {
                double gdiDuration = GetGifDuration(_mediaFilePath);
                if (gdiDuration > 0.05)
                    return gdiDuration;
            }
            catch (Exception ex)
            {
                AppDiagnostics.LogWarning("trimmer.gif-duration", $"Failed to read GIF duration via GDI+: {ex.Message}", ex);
            }

            if (naturalDurationSeconds > 0.05)
                return naturalDurationSeconds;

            return 0;
        }

        private static double GetGifDurationFromFrameCount(string filePath, double fps)
        {
            if (fps <= 0)
                return 0;

            int frameCount = GetGifFrameCount(filePath);
            return frameCount > 0 ? frameCount / fps : 0;
        }

        private static int GetGifFrameCount(string filePath)
        {
            if (!File.Exists(filePath))
                return 0;

            try
            {
                using var img = System.Drawing.Image.FromFile(filePath);
                var dimension = new System.Drawing.Imaging.FrameDimension(img.FrameDimensionsList[0]);
                return img.GetFrameCount(dimension);
            }
            catch
            {
                return 0;
            }
        }

        private static double GetMediaDurationViaFfprobe(string mediaPath)
        {
            string? ffmpeg = VideoRecorder.FindFfmpeg();
            if (ffmpeg == null)
                return 0;

            string? directory = Path.GetDirectoryName(ffmpeg);
            string ffprobePath = directory == null
                ? "ffprobe.exe"
                : Path.Combine(directory, "ffprobe.exe");

            if (!File.Exists(ffprobePath))
            {
                if (ffmpeg.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    ffprobePath = ffmpeg[..^"ffmpeg.exe".Length] + "ffprobe.exe";
                else if (ffmpeg.EndsWith("ffmpeg", StringComparison.OrdinalIgnoreCase))
                    ffprobePath = ffmpeg[..^"ffmpeg".Length] + "ffprobe";
            }

            if (!File.Exists(ffprobePath))
                return 0;

            try
            {
                double fromFrames = TryGetFfprobeFrameDuration(ffprobePath, mediaPath);
                if (fromFrames > 0.05)
                    return fromFrames;

                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{mediaPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                });

                if (process == null)
                    return 0;

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);

                return double.TryParse(output, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds)
                    ? seconds
                    : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double TryGetFfprobeFrameDuration(string ffprobePath, string mediaPath)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v error -select_streams v:0 -show_entries stream=nb_frames,r_frame_rate -of csv=p=0 \"{mediaPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                });

                if (process == null)
                    return 0;

                string output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5000);
                if (string.IsNullOrWhiteSpace(output))
                    return 0;

                string[] parts = output.Split(',');
                if (parts.Length < 2)
                    return 0;

                if (!int.TryParse(parts[0].Trim(), out int frames) || frames <= 0)
                    return 0;

                double fps = ParseFfprobeFrameRate(parts[1].Trim());
                return fps > 0 ? frames / fps : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static double ParseFfprobeFrameRate(string rate)
        {
            string[] segments = rate.Split('/');
            if (segments.Length == 2
                && double.TryParse(segments[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double numerator)
                && double.TryParse(segments[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double denominator)
                && denominator > 0)
            {
                return numerator / denominator;
            }

            return double.TryParse(rate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double direct)
                ? direct
                : 0;
        }

        private void MediaPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (_isGif)
                return;

            double duration = 0;
            bool gotDuration = false;

            if (MediaPlayer.NaturalDuration.HasTimeSpan)
            {
                duration = MediaPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                gotDuration = true;
            }

            if (gotDuration)
            {
                _videoDurationSeconds = duration;
                TimeSlider.Maximum = _videoDurationSeconds;
                
                // Initialize range markers to full duration
                _startTimeSeconds = 0;
                _endTimeSeconds = _videoDurationSeconds;
                
                SetStartBtn.IsChecked = false;
                SetEndBtn.IsChecked = false;

                RefreshTimeDisplay();
                UpdateMarkerLabels();
                EvaluateCropState();
                
                if (!_suppressLoadBanner)
                {
                    ShowBanner(LocalizationService.Translate(_settingsService.Settings.InterfaceLanguage, "MP4 loaded"));
                }
                _suppressLoadBanner = false;
                
                MediaPlayer.Position = TimeSpan.Zero;
                Dispatcher.BeginInvoke(new Action(() => MediaPlayer.Play()), DispatcherPriority.Background);
            }
            else
            {
                // Fallback to start playback anyway
                Dispatcher.BeginInvoke(new Action(() => MediaPlayer.Play()), DispatcherPriority.Background);
            }
        }

        private static double GetGifDuration(string filePath)
        {
            if (!File.Exists(filePath))
                return 0;

            try
            {
                using var img = System.Drawing.Image.FromFile(filePath);
                var dimension = new System.Drawing.Imaging.FrameDimension(img.FrameDimensionsList[0]);
                int frameCount = img.GetFrameCount(dimension);
                if (frameCount <= 0)
                    return 0;

                int durationMs = 0;
                const int PropertyTagFrameDelay = 0x5100;
                
                bool hasDelayProp = false;
                foreach (int id in img.PropertyIdList)
                {
                    if (id == PropertyTagFrameDelay)
                    {
                        hasDelayProp = true;
                        break;
                    }
                }

                if (hasDelayProp)
                {
                    var propItem = img.GetPropertyItem(PropertyTagFrameDelay);
                    if (propItem != null && propItem.Value != null)
                    {
                        byte[] bytes = propItem.Value;
                        for (int i = 0; i < frameCount; i++)
                        {
                            if (i * 4 + 3 < bytes.Length)
                            {
                                int centiseconds = BitConverter.ToInt32(bytes, i * 4);
                                // Netscape extension treats 0 as ~10 ms; matches browser/MF timing better.
                                int delay = centiseconds <= 0 ? 10 : centiseconds * 10;
                                durationMs += delay;
                            }
                        }
                    }
                }

                if (durationMs <= 0)
                {
                    durationMs = frameCount * 100; // default 100ms per frame
                }

                return durationMs / 1000.0;
            }
            catch
            {
                return 0;
            }
        }
        
        private void MediaPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            if (_isGif)
                return;

            MediaPlayer.Position = TimeSpan.FromSeconds(_startTimeSeconds);
            Dispatcher.BeginInvoke(new Action(() => MediaPlayer.Play()), DispatcherPriority.Background);
        }

        private void MediaPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            var msg = e.ErrorException?.Message ?? "Unknown error";
            AppDiagnostics.LogError("trimmer.media-failed", e.ErrorException ?? new Exception(msg));
            ToastWindow.ShowError("Media load failed", msg);
        }
        
        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            if (PlayPauseIconText.Text == "\uE768") // Play glyph
            {
                if (_videoDurationSeconds > 0 && GetCurrentTimelineSeconds() >= _endTimeSeconds - 0.1)
                {
                    if (_isGif)
                    {
                        RestartGifLoop();
                    }
                    else
                    {
                        MediaPlayer.Position = TimeSpan.FromSeconds(_startTimeSeconds);
                        Dispatcher.BeginInvoke(new Action(() => MediaPlayer.Play()), DispatcherPriority.Background);
                    }
                }
                else
                {
                    if (_isGif)
                    {
                        _gifTimelinePaused = false;
                        _gifPlayAnchorUtc = DateTime.UtcNow;
                        _gifPlayAnchorPosition = _gifPausedTimelineSeconds;
                    }
                    else
                    {
                        MediaPlayer.Play();
                    }
                }
                PlayPauseIconText.Text = "\uE769"; // Pause glyph
            }
            else
            {
                PausePlayback();
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

            if (!this.IsLoaded)
                return;

            string lang = _settingsService.Settings.InterfaceLanguage;
            string msg = VolumeControl.IsExportMuted
                ? LocalizationService.Translate(lang, "Muted")
                : LocalizationService.Translate(lang, "Unmuted");
            ShowBanner(msg);
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
                return $"-y -ss {cultureStart} -to {cultureEnd} -i \"{input}\" -loop 0 \"{output}\"";

            if (!hasAudio || exportMuted || volume <= 0.001)
                return $"-y -ss {cultureStart} -to {cultureEnd} -i \"{input}\" -c:v copy -an \"{output}\"";

            if (Math.Abs(volume - 1.0) < 0.001)
                return $"-y -ss {cultureStart} -to {cultureEnd} -i \"{input}\" -c copy \"{output}\"";

            string cultureVolume = volume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return $"-y -ss {cultureStart} -to {cultureEnd} -i \"{input}\" -map 0:v -map 0:a? -c:v copy -af \"volume={cultureVolume}\" -c:a aac -b:a 192k \"{output}\"";
        }

        private void StepBackBtn_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();

            double target = GetCurrentTimelineSeconds() - (1.0 / _fps);
            if (target < 0) target = 0;

            SeekMediaTo(target);
            TimeSlider.Value = target;
            UpdateTimeStatus();
        }

        private void StepForwardBtn_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();

            double target = GetCurrentTimelineSeconds() + (1.0 / _fps);
            if (target > _videoDurationSeconds) target = _videoDurationSeconds;

            SeekMediaTo(target);
            TimeSlider.Value = target;
            UpdateTimeStatus();
        }

        private void PausePlayback()
        {
            if (_isGif)
            {
                _gifPausedTimelineSeconds = GetGifTimelinePosition();
                _gifTimelinePaused = true;
            }
            else
            {
                MediaPlayer.Pause();
            }

            PlayPauseIconText.Text = "\uE768";
            UpdatePlayPauseToolTip();
        }

        private void SeekMediaTo(double seconds)
        {
            if (_isGif)
            {
                SyncGifTimelineAfterSeek(seconds);
                UpdateGifFrameDisplay(seconds, force: true);
                return;
            }

            MediaPlayer.Position = TimeSpan.FromSeconds(seconds);
        }

        private void StartSeekBtn_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();
            SeekMediaTo(_startTimeSeconds);
            TimeSlider.Value = _startTimeSeconds;
            UpdateTimeStatus();
        }

        private void EndSeekBtn_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();
            SeekMediaTo(_endTimeSeconds);
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

            // IsMoveToPointEnabled="True" causes the Slider's class handler
            // (OnPreviewMouseLeftButtonDown) to position the Thumb via
            // Track.ValueFromPoint BEFORE this instance handler fires.
            // Just seek the media to the already-correct slider value.
            SeekMediaTo(TimeSlider.Value);
            _userTargetSeconds = TimeSlider.Value;
            _lastUserDragTime = DateTime.UtcNow;
            UpdateTimeStatus();
        }
        
        private void TimeSlider_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isSliderDragging)
            {
                _isSliderDragging = false;
                SeekMediaTo(TimeSlider.Value);
                _userTargetSeconds = TimeSlider.Value;
                _lastUserDragTime = DateTime.UtcNow;
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
                SeekMediaTo(TimeSlider.Value);
                _userTargetSeconds = TimeSlider.Value;
                _lastUserDragTime = DateTime.UtcNow;
                UpdateTimeStatus();
            }
        }

        private void TimeSlider_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            TimelineHoverTooltip.Visibility = Visibility.Visible;
        }

        private void TimeSlider_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            TimelineHoverTooltip.Visibility = Visibility.Hidden;
        }

        private void TimeSlider_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (_videoDurationSeconds <= 0 || TimeSlider.ActualWidth <= 0)
                return;

            System.Windows.Point mousePos = e.GetPosition(TimeSlider);
            double percent = mousePos.X / TimeSlider.ActualWidth;
            percent = Math.Clamp(percent, 0.0, 1.0);

            double hoverTimeSeconds = percent * _videoDurationSeconds;

            HoverTooltipText.Text = _detailedTimeDisplay 
                ? FormatTime(hoverTimeSeconds) 
                : FormatSimpleTime(hoverTimeSeconds);

            // Force layout update so TimelineHoverTooltip.ActualWidth gets calculated correctly based on the new text
            TimelineHoverTooltip.UpdateLayout();

            double tooltipX = mousePos.X - (TimelineHoverTooltip.ActualWidth / 2);
            double maxX = TimeSlider.ActualWidth - TimelineHoverTooltip.ActualWidth;

            if (tooltipX < 0) tooltipX = 0;
            if (tooltipX > maxX) tooltipX = maxX;

            HoverTooltipTransform.X = tooltipX;
        }
        
        private void UpdateTimeStatus()
        {
            RefreshTimeDisplay();
        }

        private void RefreshTimeDisplay()
        {
            double currentSeconds = GetCurrentTimelineSeconds();
            if (_detailedTimeDisplay)
            {
                TimeStatusText.Text = FormatTime(currentSeconds);
                TimeStatusSeparator.Visibility = Visibility.Visible;
                DurationStatusText.Visibility = Visibility.Visible;
                DurationStatusText.Text = FormatTime(_videoDurationSeconds);
            }
            else
            {
                TimeStatusText.Text = FormatSimpleTime(currentSeconds);
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
            string lang = _settingsService.Settings.InterfaceLanguage;
            if (SetStartBtn.IsChecked == true)
            {
                double current = GetCurrentTimelineSeconds();
                if (current < _endTimeSeconds)
                {
                    _startTimeSeconds = current;
                    ShowBanner(LocalizationService.Translate(lang, "Start position set"));
                }
                else
                {
                    SetStartBtn.IsChecked = false;
                }
            }
            else
            {
                _startTimeSeconds = 0;
                ShowBanner(LocalizationService.Translate(lang, "Start position cleared"));
            }
            UpdateMarkerLabels();
            EvaluateCropState();
        }
        
        private void SetEndBtn_Click(object sender, RoutedEventArgs e)
        {
            PausePlayback();
            string lang = _settingsService.Settings.InterfaceLanguage;
            if (SetEndBtn.IsChecked == true)
            {
                double current = GetCurrentTimelineSeconds();
                if (current > _startTimeSeconds)
                {
                    _endTimeSeconds = current;
                    ShowBanner(LocalizationService.Translate(lang, "End position set"));
                }
                else
                {
                    SetEndBtn.IsChecked = false;
                }
            }
            else
            {
                _endTimeSeconds = _videoDurationSeconds;
                ShowBanner(LocalizationService.Translate(lang, "End position cleared"));
            }
            UpdateMarkerLabels();
            EvaluateCropState();
        }
        
        private void UpdateMarkerLabels()
        {
            StartPointText.Text = FormatTime(_startTimeSeconds);
            EndPointText.Text = FormatTime(_endTimeSeconds);

            string lang = _settingsService.Settings.InterfaceLanguage;
            SetStartBtn.ToolTip = SetStartBtn.IsChecked == true
                ? LocalizationService.Translate(lang, "Clear crop start position")
                : LocalizationService.Translate(lang, "Set the current time as the crop start position");
            SetEndBtn.ToolTip = SetEndBtn.IsChecked == true
                ? LocalizationService.Translate(lang, "Clear crop end position")
                : LocalizationService.Translate(lang, "Set the current time as the crop end position");

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
            UpdateTitleState(isModified);
        }

        private void UpdateTitleState(bool isModified)
        {
            if (LedCore == null || LedAura == null || TitleFileNameText == null || TitleContainer == null)
                return;

            string lang = _settingsService.Settings.InterfaceLanguage;
            if (isModified)
            {
                LedCore.Fill = OrangeLedBrush;
                LedAura.Fill = OrangeLedAuraBrush;
                TitleContainer.ToolTip = LocalizationService.Translate(lang, "Unsaved changes");
            }
            else
            {
                LedCore.Fill = GreenLedBrush;
                LedAura.Fill = GreenLedAuraBrush;
                TitleContainer.ToolTip = LocalizationService.Translate(lang, "Saved");
            }

            TitleFileNameText.Text = Path.GetFileName(_mediaFilePath);
        }

        private void ShowBanner(string text)
        {
            if (!_settingsService.Settings.ShowToolBanners)
                return;

            if (BannerText == null || BannerBorder == null)
                return;

            BannerText.Text = text;
            BannerBorder.Visibility = Visibility.Visible;

            if (_bannerTimer == null)
            {
                _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _bannerTimer.Tick += BannerTimer_Tick;
            }
            _bannerTimer.Stop();

            BannerBorder.BeginAnimation(UIElement.OpacityProperty, null);

            var fadeIn = new DoubleAnimation
            {
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BannerBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            _bannerTicks = 0;
            _bannerTimer.Start();
        }

        private void BannerTimer_Tick(object? sender, EventArgs e)
        {
            _bannerTicks++;
            if (_bannerTicks >= 40)
            {
                _bannerTimer?.Stop();

                var fadeOut = new DoubleAnimation
                {
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };
                fadeOut.Completed += (s, ev) =>
                {
                    if (BannerBorder != null && BannerBorder.Opacity == 0.0)
                    {
                        BannerBorder.Visibility = Visibility.Collapsed;
                    }
                };
                BannerBorder?.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }
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
            if (_isGif)
            {
                LoadGifPreview();
                return;
            }

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
            // Prompt if there are unsaved changes
            bool isModified = _startTimeSeconds > 0.05 || _endTimeSeconds < (_videoDurationSeconds - 0.05);
            if (isModified)
            {
                string lang = _settingsService.Settings.InterfaceLanguage;
                string template = LocalizationService.Translate(lang, "The changes made will be lost.\nThe original file '{0}' will be kept.");
                string msg = string.Format(template, Path.GetFileName(_mediaFilePath));

                bool discard = ThemedConfirmDialog.Confirm(
                    this,
                    "Discard changes?",
                    msg,
                    "Discard",
                    "Keep editing",
                    danger: false);

                if (!discard)
                {
                    e.Cancel = true;
                    return;
                }
            }

            CompositionTarget.Rendering -= OnRendering;
            _audioPersistTimer.Stop();
            if (_audioPersistPending && Application.Current is App app)
                app.PersistVideoTrimmerAudio(VolumeControl.Volume, VolumeControl.IsExportMuted);
            DisposeGifSequence();
            MediaPlayer.Close();
        }
        
        private async void TrimBtn_Click(object sender, RoutedEventArgs e)
        {
            string lang = _settingsService.Settings.InterfaceLanguage;
            string template = LocalizationService.Translate(lang, "Are you sure you want to overwrite the original file '{0}'?\nThis action cannot be undone.");
            string msg = string.Format(template, Path.GetFileName(_mediaFilePath));

            if (!ThemedConfirmDialog.Confirm(
                this, 
                "Confirm Overwrite", 
                msg,
                "Yes",
                "No",
                danger: false))
            {
                return;
            }

            string ext = Path.GetExtension(_mediaFilePath);
            string tempOut = Path.Combine(Path.GetDirectoryName(_mediaFilePath) ?? string.Empty, 
                                          $"trim_temp_{Guid.NewGuid()}{ext}");
            
            bool success = await RunFfmpegTrimAsync(_mediaFilePath, tempOut, _startTimeSeconds, _endTimeSeconds);
            if (success)
            {
                try
                {
                    CompositionTarget.Rendering -= OnRendering;
                    DisposeGifSequence();
                    MediaPlayer.Close();
                    
                    // Give player time to release lock
                    System.Threading.Thread.Sleep(200);
                    
                    File.Delete(_mediaFilePath);
                    File.Move(tempOut, _mediaFilePath);
                    
                    // Reload original path
                    LoadMediaFile(_mediaFilePath);
                    CompositionTarget.Rendering += OnRendering;
                    
                    ToastWindow.Show(
                        LocalizationService.Translate(lang, "Video trimmed"),
                        LocalizationService.Translate(lang, "Original file overwritten successfully."),
                        _mediaFilePath
                    );
                }
                catch (Exception ex)
                {
                    AppDiagnostics.LogError("trim.overwrite", ex);
                    string errMsg = LocalizationService.Translate(lang, "Failed to overwrite original file: ");
                    ThemedConfirmDialog.Alert(this, "Error", $"{errMsg}{ex.Message}", error: true);
                }
            }
        }
        
        private async void SaveAsNewBtn_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.GetDirectoryName(_mediaFilePath) ?? string.Empty;
            string nameWithoutExt = Path.GetFileNameWithoutExtension(_mediaFilePath);
            string ext = Path.GetExtension(_mediaFilePath);

            var sfd = new Microsoft.Win32.SaveFileDialog
            {
                InitialDirectory = dir,
                FileName = $"{nameWithoutExt}_edited{ext}",
                DefaultExt = ext
            };

            string filterName = string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase) ? "GIF Files" : "Video Files";
            sfd.Filter = $"{filterName} (*{ext})|*{ext}|All Files (*.*)|*.*";

            if (sfd.ShowDialog(this) != true)
                return;

            string newPath = sfd.FileName;
            
            bool success = await RunFfmpegTrimAsync(_mediaFilePath, newPath, _startTimeSeconds, _endTimeSeconds);
            if (success)
            {
                string lang = _settingsService.Settings.InterfaceLanguage;
                ToastWindow.Show(
                    LocalizationService.Translate(lang, "Video saved"),
                    LocalizationService.Translate(lang, "Trimmed copy saved successfully."),
                    newPath
                );

                // Auto-load the new copy in the editor
                CompositionTarget.Rendering -= OnRendering;
                DisposeGifSequence();
                MediaPlayer.Close();

                // Give player time to release lock
                System.Threading.Thread.Sleep(200);

                LoadMediaFile(newPath);
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private void LoadMediaFile(string newPath)
        {
            _mediaFilePath = newPath;

            if (_isGif)
            {
                VolumeControl.SetGifMode();
                LoadGifPreview();
                return;
            }

            GifPreviewImage.Visibility = Visibility.Collapsed;
            MediaPlayer.Visibility = Visibility.Visible;

            _hasAudioTrack = MediaHasAudioTrack(_mediaFilePath);
            if (!_hasAudioTrack)
            {
                VolumeControl.SetNoAudioMode();
            }
            else
            {
                VolumeControl.HasAudioTrack = true;
                SyncMediaAudio();
            }

            MediaPlayer.Source = new Uri(_mediaFilePath, UriKind.Absolute);
            MediaPlayer.Play();
            PlayPauseIconText.Text = "\uE769";
            UpdatePlayPauseToolTip();
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
