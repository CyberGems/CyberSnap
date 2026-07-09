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
    private static readonly string GlyphCamera = ((char)0xE722).ToString(); // Camera (present in both Fluent + MDL2)
    private static readonly string GlyphStar = ((char)0xE735).ToString();   // FavoriteStarFill
    private static readonly string GlyphLock = ((char)0xE72E).ToString();   // Lock

    // Icon font with MDL2 fallback — matches the rest of the app (see SettingIconGlyph style).
    // Without the fallback, glyphs render as empty squares on systems lacking Segoe Fluent Icons.
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static MediaColor ThemeAlpha(byte alpha) => Theme.IsDark
        ? MediaColor.FromArgb(alpha, 255, 255, 255)
        : MediaColor.FromArgb(alpha, 0, 0, 0);

    private static Brush ThemeAlphaBrush(byte alpha) => new SolidColorBrush(ThemeAlpha(alpha));

    private static readonly Brush FallbackTextBrush = Theme.IsDark ? Brushes.White : Brushes.Black;

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

        // IconId, when set, renders the matching Fluent SVG (e.g. the trophy for milestones) instead
        // of a Segoe font glyph — Segoe has no trophy, and vector icons stay crisp on scaled displays.
        var stats = new (string Glyph, string? IconId, string Value, string LabelKey, MediaColor Accent)[]
        {
            (GlyphCamera, null, count.ToString("N0"), "Total captures", RailCyan),
            ("🔥", null, s.CurrentStreak.ToString("N0"), "Current streak", MediaColor.FromRgb(0xFF, 0x9A, 0x3D)),
            (GlyphStar, null, s.LongestStreak.ToString("N0"), "Longest streak", MediaColor.FromRgb(0xFF, 0xC1, 0x07)),
            (GlyphStar, "trophy", $"{reached}/{total}", "Milestones reached", RailPurple),
        };

        for (int i = 0; i < stats.Length; i++)
        {
            var (glyph, iconId, value, labelKey, accent) = stats[i];
            var card = MakeStatCard(glyph, iconId, value, LocalizationService.Translate(labelKey), accent);
            card.Margin = new Thickness(5);
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            host.Children.Add(card);
        }
    }

    // A single stat card: icon accent + big number + small label, in a framed tile. Uses the Fluent
    // SVG icon when iconId is set, otherwise the Segoe font glyph (or emoji).
    private Border MakeStatCard(string glyph, string? iconId, string value, string label, MediaColor accent)
    {
        var glow = new DropShadowEffect { Color = accent, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.5 };

        UIElement iconBlock;
        if (iconId is { Length: > 0 } id && Helpers.FluentIcons.HasIcon(id))
        {
            const int iconDip = 22;
            var drawingColor = System.Drawing.Color.FromArgb(accent.A, accent.R, accent.G, accent.B);
            var img = new System.Windows.Controls.Image
            {
                Source = Helpers.FluentIcons.RenderWpf(id, drawingColor, iconDip * 2),
                Width = iconDip,
                Height = iconDip,
                HorizontalAlignment = HAlign.Center,
                Margin = new Thickness(0, 0, 0, 6),
                Effect = glow
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            iconBlock = img;
        }
        else
        {
            bool isEmoji = glyph.Length > 1 && char.IsSurrogatePair(glyph, 0);
            iconBlock = new TextBlock
            {
                Text = glyph,
                FontSize = 20,
                FontFamily = isEmoji ? new FontFamily("Segoe UI Emoji") : IconFont,
                Foreground = new SolidColorBrush(accent),
                HorizontalAlignment = HAlign.Center,
                Margin = new Thickness(0, 0, 0, 6),
                Effect = glow
            };
        }

        var valueBlock = new TextBlock
        {
            Text = value,
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Display"),
            Foreground = (Brush?)TryFindResource("ThemeTextPrimaryBrush") ?? FallbackTextBrush,
            HorizontalAlignment = HAlign.Center
        };

        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 11,
            Opacity = Theme.IsDark ? 0.6 : 0.7,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            Foreground = (Brush?)TryFindResource("ThemeTextPrimaryBrush") ?? FallbackTextBrush,
            HorizontalAlignment = HAlign.Center,
            Margin = new Thickness(0, 2, 0, 0)
        };

        var panel = new StackPanel();
        panel.Children.Add(iconBlock);
        panel.Children.Add(valueBlock);
        panel.Children.Add(labelBlock);

        var card = new Border
        {
            Padding = new Thickness(14, 16, 14, 14),
            CornerRadius = new CornerRadius(8),
            Background = (Brush?)TryFindResource("ThemeInputBackgroundBrush")
                         ?? ThemeAlphaBrush(0x14),
            BorderBrush = (Brush?)TryFindResource("ThemeInputBorderBrush")
                          ?? ThemeAlphaBrush(0x22),
            BorderThickness = new Thickness(1),
            Child = panel
        };
        // Screen readers otherwise only see the raw number; pair it with its label.
        System.Windows.Automation.AutomationProperties.SetName(card, $"{label}: {value}");
        return card;
    }

    // Medal grid: one section per category, each with a WrapPanel of medal tiles.
    private void BuildMedalGrid(AppSettings s)
    {
        var host = AchievementsMedalsHost!;
        host.Children.Clear();

        var achievements = AchievementCatalog.Build(s, LocalizationService.Translate);
        var groups = new (string Key, AchievementKind Kind)[]
        {
            ("Capture milestones", AchievementKind.CaptureMilestone),
            ("Day streaks", AchievementKind.Streak),
            ("First time", AchievementKind.FirstTime),
        };
        for (int i = 0; i < groups.Length; i++)
            AddMedalSection(host, groups[i].Key,
                achievements.Where(a => a.Kind == groups[i].Kind), first: i == 0);
    }

    private void AddMedalSection(StackPanel host, string categoryKey, IEnumerable<Achievement> medals, bool first)
    {
        // Thin divider above every category but the first, for gentle in-card separation.
        if (!first)
        {
            host.Children.Add(new Border
            {
                Height = 1,
                Background = (Brush?)TryFindResource("ThemeSeparatorBrush") ?? Brushes.Transparent,
                Margin = new Thickness(0, 16, 0, 0)
            });
        }

        var label = new TextBlock
        {
            Text = LocalizationService.Translate(categoryKey),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.7,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            Foreground = (Brush?)TryFindResource("ThemeTextPrimaryBrush") ?? FallbackTextBrush,
            Margin = new Thickness(0, first ? 4 : 14, 0, 10)
        };
        host.Children.Add(label);

        // Fixed item box keeps rows tidy despite tiles varying in height (locked tiles carry a
        // progress line, unlocked ones don't).
        var panel = new WrapPanel { ItemWidth = 94, ItemHeight = 104, Margin = new Thickness(0, 0, 0, 2) };
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

        // Icon tint: the tier color when unlocked (darkened a touch in light mode for contrast),
        // a dim theme wash when locked. Shared by both the vector icon and the glyph fallback.
        var iconColor = a.Unlocked
            ? (Theme.IsDark ? color : MediaColor.FromArgb(255, (byte)Math.Max(color.R - 30, 0), (byte)Math.Max(color.G - 30, 0), (byte)Math.Max(color.B - 30, 0)))
            : ThemeAlpha(0x55);

        // Prefer the tool's own Fluent SVG icon so the medal matches the live toolbar/widget icon;
        // fall back to the Segoe font glyph when no vector icon is available (e.g. milestone stars).
        const int iconDip = 26;
        UIElement iconElement;
        if (a.IconId is { Length: > 0 } iconId && Helpers.FluentIcons.HasIcon(iconId))
        {
            var drawingColor = System.Drawing.Color.FromArgb(iconColor.A, iconColor.R, iconColor.G, iconColor.B);
            // Render at 2× the display size and downscale with HighQuality so the vector icon stays
            // crisp on 125%/150% DPI displays (matches ToolListBuilder's icon handling).
            var img = new System.Windows.Controls.Image
            {
                Source = Helpers.FluentIcons.RenderWpf(iconId, drawingColor, iconDip * 2),
                Width = iconDip,
                Height = iconDip,
                HorizontalAlignment = HAlign.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            iconElement = img;
        }
        else
        {
            iconElement = new TextBlock
            {
                Text = a.Glyph,
                FontSize = 22,
                FontFamily = IconFont,
                Foreground = new SolidColorBrush(iconColor),
                HorizontalAlignment = HAlign.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        var badge = new Border
        {
            Width = badgeSize,
            Height = badgeSize,
            CornerRadius = new CornerRadius(badgeSize / 2),
            Background = a.Unlocked
                ? new SolidColorBrush(MediaColor.FromArgb(Theme.IsDark ? (byte)0x33 : (byte)0x50, color.R, color.G, color.B))
                : ThemeAlphaBrush(0x0C),
            BorderBrush = a.Unlocked
                ? new SolidColorBrush(color)
                : ThemeAlphaBrush(0x1F),
            BorderThickness = new Thickness(a.Unlocked ? 2 : 1),
            Child = iconElement
        };

        if (a.Unlocked)
            badge.Effect = new DropShadowEffect { Color = color, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.6 };

        // Lock overlay for locked medals, anchored to the bottom-right of the badge. A small solid
        // disc backs the padlock so it stays legible over the badge edge and the icon behind it.
        var badgeGrid = new Grid { Width = badgeSize, Height = badgeSize, HorizontalAlignment = HAlign.Center };
        badgeGrid.Children.Add(badge);
        if (!a.Unlocked)
        {
            var lockBacking = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Theme.IsDark ? MediaColor.FromRgb(0x1B, 0x1F, 0x27) : MediaColor.FromRgb(0xEC, 0xEE, 0xF2)),
                HorizontalAlignment = HAlign.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = GlyphLock,
                    FontSize = 10,
                    FontFamily = IconFont,
                    Foreground = ThemeAlphaBrush(0xB0),
                    HorizontalAlignment = HAlign.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            badgeGrid.Children.Add(lockBacking);
        }

        var titleBlock = new TextBlock
        {
            Text = a.Title,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            Foreground = (Brush?)Application.Current.TryFindResource("ThemeTextPrimaryBrush") ?? FallbackTextBrush,
            Opacity = a.Unlocked ? 0.9 : (Theme.IsDark ? 0.45 : 0.6),
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
                Opacity = Theme.IsDark ? 0.4 : 0.55,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                Foreground = (Brush?)Application.Current.TryFindResource("ThemeTextPrimaryBrush") ?? FallbackTextBrush,
                HorizontalAlignment = HAlign.Center,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }

        var tile = new Border
        {
            Child = panel,
            ToolTip = BuildMedalTooltip(a),
            HorizontalAlignment = HAlign.Center,
            VerticalAlignment = VerticalAlignment.Top
        };
        // Show the richer tooltip promptly rather than after the default ~1s hover.
        ToolTipService.SetInitialShowDelay(tile, 200);

        // Screen-reader label: title, unlocked/locked state (with progress when applicable), then
        // the description. Automation names are kept in English to match the rest of the app.
        string state = a.Unlocked
            ? "Unlocked"
            : (a.Progress is (int c, int t) ? $"Locked, {c:N0} of {t:N0}" : "Locked");
        System.Windows.Automation.AutomationProperties.SetName(tile, $"{a.Title}. {state}. {a.Description}");

        return tile;
    }

    // A compact multi-line tooltip for a medal: bold title, description, and a progress line on
    // locked tiles that track toward a target. Uses already-localized content only.
    private static object BuildMedalTooltip(Achievement a)
    {
        var content = new StackPanel { MaxWidth = 240 };
        content.Children.Add(new TextBlock
        {
            Text = a.Title,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new FontFamily("Segoe UI Variable Text")
        });
        content.Children.Add(new TextBlock
        {
            Text = a.Description,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 2, 0, 0),
            FontFamily = new FontFamily("Segoe UI Variable Text")
        });
        if (!a.Unlocked && a.Progress is (int cur, int target))
        {
            content.Children.Add(new TextBlock
            {
                Text = $"{cur:N0} / {target:N0}",
                Opacity = 0.7,
                Margin = new Thickness(0, 4, 0, 0),
                FontFamily = new FontFamily("Segoe UI Variable Text")
            });
        }
        return content;
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
