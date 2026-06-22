using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CyberSnap.Models;
using CyberSnap.Services;
using MediaColor = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using HAlign = System.Windows.HorizontalAlignment;

namespace CyberSnap.UI;

// Achievements tab: a gamified surface with the animated milestone rail (hero), Duolingo-style
// statistics cards, and a medal grid grouped by category. The rail itself lives in
// SettingsWindow.Celebrations.cs and is hosted here via MilestoneRailHost; this file builds the
// statistics and medal sections that surround it.
public partial class SettingsWindow
{
    private static readonly string GlyphCamera = ((char)0xE7C2).ToString(); // Camera (Capture)
    private static readonly string GlyphStar = ((char)0xE735).ToString();   // FavoriteStarFill
    private static readonly string GlyphLock = ((char)0xE72E).ToString();   // Lock

    // Icon font with MDL2 fallback — matches the rest of the app (see SettingIconGlyph style).
    // Without the fallback, glyphs render as empty squares on systems lacking Segoe Fluent Icons.
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // (Re)builds the statistics cards and medal grid from the live settings. Called when the
    // Achievements tab is selected and after the Celebrations toggle changes.
    private void RefreshAchievements()
    {
        if (AchievementsStatsHost is null || AchievementsMedalsHost is null || _settingsService is null)
            return;

        var s = _settingsService.Settings;
        BuildStatsCards(s);
        BuildMedalGrid(s);
    }

    // 2×2 grid of summary stat cards: total captures, current streak, longest streak, milestones reached.
    private void BuildStatsCards(AppSettings s)
    {
        var host = AchievementsStatsHost!;
        host.Children.Clear();
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        for (int i = 0; i < 2; i++)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        int count = s.CelebrationCaptureCount;
        int reached = CelebrationMilestones.Values.Count(v => count >= v);
        int total = CelebrationMilestones.Values.Length;

        var stats = new (string Glyph, string Value, string LabelKey, MediaColor Accent)[]
        {
            (GlyphCamera, count.ToString("N0"), "Total captures", RailCyan),
            ("🔥", s.CurrentStreak.ToString("N0"), "Current streak", MediaColor.FromRgb(0xFF, 0x9A, 0x3D)),
            (GlyphStar, s.LongestStreak.ToString("N0"), "Longest streak", MediaColor.FromRgb(0xFF, 0xC1, 0x07)),
            (GlyphStar, $"{reached}/{total}", "Milestones reached", RailPurple),
        };

        for (int i = 0; i < stats.Length; i++)
        {
            var (glyph, value, labelKey, accent) = stats[i];
            var card = MakeStatCard(glyph, value, LocalizationService.Translate(labelKey), accent);
            card.Margin = new Thickness(3);
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            host.Children.Add(card);
        }
    }

    // A single stat card: glyph accent + big number + small label, in a framed tile.
    private Border MakeStatCard(string glyph, string value, string label, MediaColor accent)
    {
        bool isEmoji = glyph.Length > 1 && char.IsSurrogatePair(glyph, 0);

        var glyphBlock = new TextBlock
        {
            Text = glyph,
            FontSize = 20,
            FontFamily = isEmoji ? new FontFamily("Segoe UI Emoji") : IconFont,
            Foreground = new SolidColorBrush(accent),
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 0, 0, 6),
            Effect = new DropShadowEffect { Color = accent, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.5 }
        };

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            Foreground = (Brush?)TryFindResource("ThemeTextPrimaryBrush") ?? Brushes.White,
            HorizontalAlignment = HAlign.Center
        };

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Opacity = 0.6,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            Foreground = (Brush?)TryFindResource("ThemeTextPrimaryBrush") ?? Brushes.White,
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var panel = new StackPanel();
        panel.Children.Add(glyphBlock);
        panel.Children.Add(valueBlock);
        panel.Children.Add(labelBlock);

        return new Border
        {
            Padding = new Thickness(14, 16, 14, 14),
            CornerRadius = new CornerRadius(8),
            Background = (Brush?)TryFindResource("ThemeInputBackgroundBrush")
                         ?? new SolidColorBrush(MediaColor.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = (Brush?)TryFindResource("ThemeInputBorderBrush")
                          ?? new SolidColorBrush(MediaColor.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = panel
        };
    }

    // Medal grid: one section per category, each with a WrapPanel of medal tiles.
    private void BuildMedalGrid(AppSettings s)
    {
        var host = AchievementsMedalsHost!;
        host.Children.Clear();

        var achievements = AchievementCatalog.Build(s, LocalizationService.Translate);
        AddMedalSection(host, "Capture milestones",
            achievements.Where(a => a.Kind == AchievementKind.CaptureMilestone));
        AddMedalSection(host, "Day streaks",
            achievements.Where(a => a.Kind == AchievementKind.Streak));
        AddMedalSection(host, "First time",
            achievements.Where(a => a.Kind == AchievementKind.FirstTime));
    }

    private void AddMedalSection(StackPanel host, string categoryKey, IEnumerable<Achievement> medals)
    {
        var label = new TextBlock
        {
            Text = LocalizationService.Translate(categoryKey),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.7,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            Foreground = (Brush?)TryFindResource("ThemeTextPrimaryBrush") ?? Brushes.White,
            Margin = new Thickness(0, 10, 0, 6)
        };
        host.Children.Add(label);

        var panel = new WrapPanel { ItemWidth = 92, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var a in medals)
            panel.Children.Add(MakeMedalTile(a));
        host.Children.Add(panel);
    }

    // A single medal tile: circular badge with glyph + glow (unlocked) or dim + lock (locked),
    // title underneath, optional "cur/target" progress on locked tiles.
    private static Border MakeMedalTile(Achievement a)
    {
        var color = TierColor(a.Kind, a.Tier);
        const double badgeSize = 52;

        var glyphBlock = new TextBlock
        {
            Text = a.Glyph,
            FontSize = 22,
            FontFamily = IconFont,
            Foreground = a.Unlocked
                ? new SolidColorBrush(color)
                : new SolidColorBrush(MediaColor.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var badge = new Border
        {
            Width = badgeSize,
            Height = badgeSize,
            CornerRadius = new CornerRadius(badgeSize / 2),
            Background = a.Unlocked
                ? new SolidColorBrush(MediaColor.FromArgb(0x33, color.R, color.G, color.B))
                : new SolidColorBrush(MediaColor.FromArgb(0x0C, 0xFF, 0xFF, 0xFF)),
            BorderBrush = a.Unlocked
                ? new SolidColorBrush(color)
                : new SolidColorBrush(MediaColor.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(a.Unlocked ? 2 : 1),
            Child = glyphBlock
        };

        if (a.Unlocked)
            badge.Effect = new DropShadowEffect { Color = color, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 };

        // Lock overlay for locked medals, anchored to the bottom-right of the badge.
        var badgeGrid = new Grid { Width = badgeSize, Height = badgeSize, HorizontalAlignment = HAlign.Center };
        badgeGrid.Children.Add(badge);
        if (!a.Unlocked)
        {
            badgeGrid.Children.Add(new TextBlock
            {
                Text = GlyphLock,
                FontSize = 12,
                FontFamily = IconFont,
                Foreground = new SolidColorBrush(MediaColor.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HAlign.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            });
        }

        var titleBlock = new TextBlock
        {
            Text = a.Title,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            Foreground = (Brush?)Application.Current.TryFindResource("ThemeTextPrimaryBrush") ?? Brushes.White,
            Opacity = a.Unlocked ? 0.9 : 0.45,
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var panel = new StackPanel { HorizontalAlignment = HAlign.Center };
        panel.Children.Add(badgeGrid);
        panel.Children.Add(titleBlock);

        // Progress hint on locked tiles that have a target.
        if (!a.Unlocked && a.Progress is (int cur, int target))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{cur.ToString("N0")}/{target.ToString("N0")}",
                FontSize = 9,
                Opacity = 0.4,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                Foreground = (Brush?)Application.Current.TryFindResource("ThemeTextPrimaryBrush") ?? Brushes.White,
                HorizontalAlignment = HAlign.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }

        return new Border
        {
            Child = panel,
            ToolTip = a.Description,
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 0, 0, 6)
        };
    }

    // Neon color ramp by tier: capture milestones go cyan→blue→purple→magenta→gold;
    // streaks use warm flame tones; first-time achievements use cyan (tier 0).
    private static MediaColor TierColor(AchievementKind kind, int tier)
    {
        if (kind == AchievementKind.Streak)
        {
            return tier switch
            {
                0 => MediaColor.FromRgb(0xFF, 0xB3, 0x47),
                1 => MediaColor.FromRgb(0xFF, 0x8C, 0x1A),
                2 => MediaColor.FromRgb(0xFF, 0x57, 0x22),
                3 => MediaColor.FromRgb(0xE0, 0x33, 0x00),
                _ => MediaColor.FromRgb(0xFF, 0xD7, 0x00)
            };
        }
        return tier switch
        {
            0 => RailCyan,
            1 => MediaColor.FromRgb(0x3B, 0x82, 0xF6),
            2 => RailPurple,
            3 => RailMagenta,
            _ => MediaColor.FromRgb(0xFF, 0xC1, 0x07)
        };
    }
}
