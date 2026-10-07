using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CyberSnap.Helpers;
using CyberSnap.Services;
using Brush = System.Windows.Media.Brush;

namespace CyberSnap.UI;

/// <summary>
/// Read-only media facts for the file loaded in the trimmer: file system data,
/// ffprobe stream data (best-effort) and the current trim selection.
/// </summary>
public partial class MediaPropertiesWindow : Window
{
    private readonly List<(string Section, string Label, string Value)> _rows = new();

    private MediaPropertiesWindow(
        string filePath,
        double trimStartSeconds,
        double trimEndSeconds,
        double durationSeconds,
        MediaProbe.MediaInfo? info)
    {
        InitializeComponent();
        ApplyTheme();
        UiScale.ApplyToWindow(this, RootBorder, scaleWindowBounds: false);

        var lang = AppSettingsLanguage();
        PropsTitleBar.Title = LocalizationService.Translate(lang, "Properties");
        Title = LocalizationService.Translate(lang, "Properties");
        CopyAllBtn.Content = LocalizationService.Translate(lang, "Copy");
        CloseBtn.Content = LocalizationService.Translate(lang, "Close");

        BuildRows(filePath, trimStartSeconds, trimEndSeconds, durationSeconds, info, lang);
    }

    /// <summary>Probes (one ffprobe call, best-effort) then shows the modal.</summary>
    public static void Show(
        Window owner,
        string filePath,
        double trimStartSeconds,
        double trimEndSeconds,
        double durationSeconds)
    {
        var info = MediaProbe.TryGetMediaInfo(filePath);
        var window = new MediaPropertiesWindow(filePath, trimStartSeconds, trimEndSeconds, durationSeconds, info)
        {
            Owner = owner
        };
        window.ShowDialog();
    }

    private static string AppSettingsLanguage()
    {
        try
        {
            return SettingsService.LoadStatic()?.InterfaceLanguage
                ?? LocalizationService.DefaultLanguageCode;
        }
        catch
        {
            return LocalizationService.DefaultLanguageCode;
        }
    }

    private void ApplyTheme()
    {
        Theme.Refresh();
        Resources["ThemeTextPrimaryBrush"] = Theme.Brush(Theme.TextPrimary);
        Resources["ThemeTextSecondaryBrush"] = Theme.Brush(Theme.TextSecondary);
        Resources["ThemeCardBrush"] = Theme.Brush(Theme.BgCard);
        Resources["ThemeInputBackgroundBrush"] = Theme.Brush(Theme.BgSecondary);
        Resources["ThemeInputBorderBrush"] = Theme.Brush(Theme.BorderSubtle);
        Resources["ThemeWindowBorderBrush"] = Theme.Brush(Theme.WindowBorder);
        Resources["ThemeAccentBrush"] = Theme.Brush(Theme.Accent);
        Resources["ThemeAccentSubtleBrush"] = Theme.Brush(Theme.AccentSubtle);
        Resources["ThemeSeparatorBrush"] = Theme.Brush(Theme.Separator);
        Foreground = Theme.Brush(Theme.TextPrimary);
    }

    private void BuildRows(
        string filePath,
        double trimStartSeconds,
        double trimEndSeconds,
        double durationSeconds,
        MediaProbe.MediaInfo? info,
        string lang)
    {
        string T(string key) => LocalizationService.Translate(lang, key);
        const string missing = "N/A";

        AddSection(T("File"));
        AddRow(T("File name"), Path.GetFileName(filePath));
        AddRow(T("Location"), Path.GetDirectoryName(filePath) ?? missing);
        try
        {
            var file = new FileInfo(filePath);
            AddRow(T("File size"), FormatBytes(file.Exists ? file.Length : info?.SizeBytes ?? 0));
            AddRow(T("Created"), file.Exists ? file.CreationTime.ToString("g") : missing);
            AddRow(T("Modified"), file.Exists ? file.LastWriteTime.ToString("g") : missing);
        }
        catch
        {
            AddRow(T("File size"), info is not null ? FormatBytes(info.SizeBytes) : missing);
        }
        AddRow(T("Format"), FormatContainer(info?.FormatName));

        AddSection(T("Video"));
        if (info?.Video is { } video)
        {
            AddRow(T("Video codec"), video.Codec.Length > 0 ? video.Codec.ToUpperInvariant() : missing);
            AddRow(T("Resolution"), video.Width > 0 && video.Height > 0
                ? $"{video.Width} × {video.Height}"
                : missing);
            AddRow(T("Frame rate"), video.Fps > 0 ? $"{video.Fps:0.##} FPS" : missing);
        }
        double duration = info is not null && info.DurationSeconds > 0
            ? info.DurationSeconds
            : durationSeconds;
        AddRow(T("Duration"), duration > 0 ? FormatTime(duration) : missing);
        long bitrate = info?.Video is { BitRate: > 0 } v ? v.BitRate : info?.FormatBitRate ?? 0;
        AddRow(T("Bitrate"), bitrate > 0 ? FormatBitrate(bitrate) : missing);

        AddSection(T("Audio"));
        if (info?.Audio is { } audio)
        {
            AddRow(T("Audio codec"), audio.Codec.Length > 0 ? audio.Codec.ToUpperInvariant() : missing);
            AddRow(T("Channels"), audio.Channels > 0 ? audio.Channels.ToString() : missing);
            AddRow(T("Sample rate"), audio.SampleRate > 0 ? $"{audio.SampleRate} Hz" : missing);
        }
        else if (info is not null)
        {
            AddRow(T("Audio"), T("No audio track in this file"));
        }

        AddSection(T("Trim"));
        double start = Math.Max(0, trimStartSeconds);
        double end = Math.Max(start, trimEndSeconds);
        AddRow(T("Trim selection"), $"{FormatTime(start)} → {FormatTime(end)} ({FormatSegmentDuration(end - start)})");

        foreach (var (section, label, value) in _rows)
        {
            if (label.Length == 0)
            {
                RowsHost.Children.Add(new TextBlock
                {
                    Text = section,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("ThemeAccentBrush"),
                    Margin = new Thickness(0, RowsHost.Children.Count == 0 ? 0 : 14, 0, 6)
                });
                continue;
            }

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = (Brush)FindResource("ThemeTextSecondaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 12, 0)
            };
            var valueBlock = new TextBlock
            {
                Text = value,
                FontSize = 12,
                Foreground = (Brush)FindResource("ThemeTextPrimaryBrush"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(valueBlock, 1);
            grid.Children.Add(labelBlock);
            grid.Children.Add(valueBlock);
            RowsHost.Children.Add(grid);
        }
    }

    private void AddSection(string section) => _rows.Add((section, "", ""));

    private void AddRow(string label, string value) => _rows.Add(("", label, value));

    private static string FormatContainer(string? formatName)
    {
        if (string.IsNullOrWhiteSpace(formatName))
            return "N/A";
        int comma = formatName.IndexOf(',');
        string first = (comma >= 0 ? formatName[..comma] : formatName).Trim();
        return first.Length > 0 ? first.ToUpperInvariant() : "N/A";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024)
            return $"{kb:0.0} KB";
        double mb = kb / 1024.0;
        if (mb < 1024)
            return $"{mb:0.0} MB";
        double gb = mb / 1024.0;
        return $"{gb:0.0} GB";
    }

    private static string FormatBitrate(long bitsPerSecond)
    {
        if (bitsPerSecond >= 1_000_000)
            return $"{bitsPerSecond / 1_000_000.0:0.0} Mbps";
        if (bitsPerSecond >= 1_000)
            return $"{bitsPerSecond / 1_000.0:0} kbps";
        return $"{bitsPerSecond} bps";
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        string tenths = $"{t.Milliseconds / 100:D1}";
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}.{tenths}";
        return $"{t.Minutes:D2}:{t.Seconds:D2}.{tenths}";
    }

    private static string FormatSegmentDuration(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{t.TotalSeconds:0.0}s";
    }

    private void CopyAllBtn_Click(object sender, RoutedEventArgs e)
    {
        var lines = new List<string>();
        foreach (var (section, label, value) in _rows)
        {
            lines.Add(label.Length == 0 ? $"[{section}]" : $"{label}: {value}");
        }
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            AppDiagnostics.LogWarning("media-properties.copy", ex.Message, ex);
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_CloseRequested(object? sender, EventArgs e) => Close();
}
