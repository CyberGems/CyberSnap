using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CyberSnap.Helpers;
using CyberSnap.Native;
using CyberSnap.Services;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using ProgressBar = System.Windows.Controls.ProgressBar;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace CyberSnap.UI;

/// <summary>
/// CyberGems-styled Update Dialog for CyberSnap: presents current vs latest version cards,
/// rich scrollable changelog/release notes preview (CyberPaste/CyberLauncher model),
/// "View on GitHub" action, and integrated live download progress.
/// </summary>
public sealed class ThemedUpdateDialog : Window
{
    private const double GlowMargin = 16;
    private const double PanelWidth = 480;

    private readonly UpdateCheckResult _result;
    private bool _downloadStarted;
    private CancellationTokenSource? _downloadCts;

    // UI elements needed for state transitions
    private Border _buttonsRow = null!;
    private StackPanel _progressPanel = null!;
    private ProgressBar _progressBar = null!;
    private TextBlock _progressText = null!;
    private TextBlock _errorText = null!;
    private Border _errorActionsRow = null!;

    public static bool Show(Window? owner, UpdateCheckResult result)
    {
        var dialog = new ThemedUpdateDialog(result);
        AssignDialogOwner(dialog, owner, IntPtr.Zero);
        return dialog.ShowDialog() == true;
    }

    public static bool Show(IntPtr ownerHandle, UpdateCheckResult result)
    {
        var dialog = new ThemedUpdateDialog(result);
        AssignDialogOwner(dialog, null, ownerHandle);
        return dialog.ShowDialog() == true;
    }

    private ThemedUpdateDialog(UpdateCheckResult result)
    {
        _result = result;
        Theme.Refresh();

        Title = LocalizationService.Translate("Update available");
        Width = PanelWidth + (GlowMargin * 2);
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new WpfFontFamily(UiChrome.PreferredFamilyName);
        Foreground = Theme.Brush(Theme.TextPrimary);

        Theme.ApplyTo(Resources);
        try
        {
            var xaml = @"
<ResourceDictionary xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
                    xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"">
    <Style x:Key=""SettingsScrollThumbStyle"" TargetType=""{x:Type Thumb}"">
        <Setter Property=""OverridesDefaultStyle"" Value=""True""/>
        <Setter Property=""Focusable"" Value=""False""/>
        <Setter Property=""Opacity"" Value=""0.35""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type Thumb}"">
                    <Border CornerRadius=""3"" Background=""{DynamicResource ThemeTextSecondaryBrush}"" Margin=""2,2,2,2""/>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
                <Setter Property=""Opacity"" Value=""1""/>
            </Trigger>
            <Trigger Property=""IsDragging"" Value=""True"">
                <Setter Property=""Opacity"" Value=""1""/>
            </Trigger>
        </Style.Triggers>
    </Style>
    <Style x:Key=""SettingsScrollBarStyle"" TargetType=""{x:Type ScrollBar}"">
        <Setter Property=""Width"" Value=""8""/>
        <Setter Property=""Background"" Value=""Transparent""/>
        <Setter Property=""Opacity"" Value=""0.6""/>
        <Setter Property=""Template"">
            <Setter.Value>
                <ControlTemplate TargetType=""{x:Type ScrollBar}"">
                    <Grid Background=""Transparent"" Width=""{TemplateBinding Width}"">
                        <Track x:Name=""PART_Track"" IsDirectionReversed=""True"" Margin=""0,2,0,2"">
                            <Track.DecreaseRepeatButton>
                                <RepeatButton Command=""ScrollBar.PageUpCommand"" Background=""Transparent"" BorderThickness=""0"" Focusable=""False"" IsTabStop=""False""/>
                            </Track.DecreaseRepeatButton>
                            <Track.Thumb>
                                <Thumb Style=""{StaticResource SettingsScrollThumbStyle}""/>
                            </Track.Thumb>
                            <Track.IncreaseRepeatButton>
                                <RepeatButton Command=""ScrollBar.PageDownCommand"" Background=""Transparent"" BorderThickness=""0"" Focusable=""False"" IsTabStop=""False""/>
                            </Track.IncreaseRepeatButton>
                        </Track>
                    </Grid>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
        <Style.Triggers>
            <Trigger Property=""IsMouseOver"" Value=""True"">
                <Setter Property=""Opacity"" Value=""1""/>
            </Trigger>
        </Style.Triggers>
    </Style>
    <Style TargetType=""{x:Type ScrollBar}"" BasedOn=""{StaticResource SettingsScrollBarStyle}""/>
</ResourceDictionary>";
            var dict = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml);
            Resources.MergedDictionaries.Add(dict);
        }
        catch { }

        var content = BuildContent();
        Content = content;
        UiScale.ApplyToWindow(this, content, scaleWindowBounds: true);
        ClearValue(MinWidthProperty);
        ClearValue(MinHeightProperty);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_downloadStarted)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private FrameworkElement BuildContent()
    {
        var accent = Theme.Accent;

        var shell = new Border
        {
            Margin = new Thickness(GlowMargin),
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(PanelBackground),
            BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)70 : (byte)50)),
            BorderThickness = new Thickness(1),
            Effect = Glow(accent, Theme.IsDark ? 12 : 8, Theme.IsDark ? 0.20 : 0.12)
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });      // 0: accent bar
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // 1: header (icon + title)
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // 2: version tiles
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // 3: changelog / release notes
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // 4: progress panel (initially hidden)
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });      // 5: separator
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });        // 6: buttons footer

        // Row 0: Top accent strip
        var accentBar = new Border
        {
            CornerRadius = new CornerRadius(topLeft: 10, topRight: 10, bottomRight: 0, bottomLeft: 0),
            Background = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)160 : (byte)120))
        };
        Grid.SetRow(accentBar, 0);
        root.Children.Add(accentBar);

        // Row 1: Header (Icon, Title, Subtitle, Close Button)
        var headerPanel = BuildHeader(accent);
        Grid.SetRow(headerPanel, 1);
        root.Children.Add(headerPanel);

        // Close button top-right
        var closeBtn = BuildClose();
        closeBtn.Margin = new Thickness(0, 10, 10, 0);
        Grid.SetRow(closeBtn, 1);
        root.Children.Add(closeBtn);

        // Row 2: Version Comparison Badges
        var versionTiles = BuildVersionTiles(accent);
        Grid.SetRow(versionTiles, 2);
        root.Children.Add(versionTiles);

        // Row 3: Release Notes Panel
        var changelogPanel = BuildChangelogPanel(accent);
        Grid.SetRow(changelogPanel, 3);
        root.Children.Add(changelogPanel);

        // Row 4: Download Progress Panel (hidden until download starts)
        _progressPanel = BuildProgressPanel(accent);
        Grid.SetRow(_progressPanel, 4);
        root.Children.Add(_progressPanel);

        // Row 5: Thin separator
        var separator = new Border
        {
            Height = 1,
            Background = Theme.Brush(WithAlpha(Colors.White, Theme.IsDark ? (byte)15 : (byte)25)),
            Margin = new Thickness(0, 4, 0, 0)
        };
        Grid.SetRow(separator, 5);
        root.Children.Add(separator);

        // Row 6: Buttons Footer
        _buttonsRow = BuildButtonsRow(accent);
        Grid.SetRow(_buttonsRow, 6);
        root.Children.Add(_buttonsRow);

        shell.Child = root;

        shell.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                try { DragMove(); } catch { }
            }
        };

        return shell;
    }

    private FrameworkElement BuildHeader(WpfColor accent)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24, 18, 24, 10),
            HorizontalAlignment = WpfHorizontalAlignment.Center
        };

        // Icon badge with glowing ring
        var iconRing = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(23),
            Background = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)24 : (byte)16)),
            BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)95 : (byte)60)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = WpfHorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
            Effect = Glow(accent, 8, Theme.IsDark ? 0.25 : 0.15),
            Child = new Image
            {
                Source = FluentIcons.RenderWpf("download", ToDrawingColor(accent, 240), 22),
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        panel.Children.Add(iconRing);

        // Title
        var titleBlock = new TextBlock
        {
            Text = LocalizationService.Translate("Update available"),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextPrimary),
            TextAlignment = TextAlignment.Center
        };
        panel.Children.Add(titleBlock);

        // Subtitle
        var subtitleBlock = new TextBlock
        {
            Text = LocalizationService.Translate("A new version of CyberSnap is available."),
            FontSize = 11.5,
            Foreground = Theme.Brush(Theme.TextSecondary),
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        panel.Children.Add(subtitleBlock);

        return panel;
    }

    private FrameworkElement BuildVersionTiles(WpfColor accent)
    {
        var grid = new Grid
        {
            Margin = new Thickness(24, 4, 24, 10)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) }); // spacer
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left tile: Current version
        var currentCard = new Border
        {
            Background = Theme.Brush(WithAlpha(Colors.White, Theme.IsDark ? (byte)8 : (byte)18)),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8)
        };
        var currentStack = new StackPanel();
        currentStack.Children.Add(new TextBlock
        {
            Text = LocalizationService.Translate("Current version").ToUpperInvariant(),
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted),
            Margin = new Thickness(0, 0, 0, 3)
        });
        var curVer = _result.CurrentVersion;
        var curLabel = curVer != null && curVer.Major > 0
            ? (curVer.Revision > 0 ? $"v{curVer}" : $"v{curVer.Major}.{curVer.Minor}.{curVer.Build}")
            : UpdateService.GetCurrentVersionLabel();

        currentStack.Children.Add(new TextBlock
        {
            Text = curLabel,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(Theme.TextSecondary)
        });
        currentCard.Child = currentStack;
        Grid.SetColumn(currentCard, 0);
        grid.Children.Add(currentCard);

        // Right tile: Latest version
        var latestCard = new Border
        {
            Background = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)16 : (byte)12)),
            BorderBrush = Theme.Brush(WithAlpha(accent, Theme.IsDark ? (byte)80 : (byte)60)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8)
        };
        var latestStack = new StackPanel();

        var latestHeaderRow = new Grid();
        latestHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        latestHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var latestHeader = new TextBlock
        {
            Text = LocalizationService.Translate("Latest version").ToUpperInvariant(),
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(accent),
            Margin = new Thickness(0, 0, 0, 3)
        };
        Grid.SetColumn(latestHeader, 0);
        latestHeaderRow.Children.Add(latestHeader);

        var newBadge = new Border
        {
            Background = Theme.Brush(accent),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = LocalizationService.Translate("NEW"),
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.Brush(Theme.IsDark ? Colors.Black : Colors.White)
            }
        };
        Grid.SetColumn(newBadge, 1);
        latestHeaderRow.Children.Add(newBadge);
        latestStack.Children.Add(latestHeaderRow);

        latestStack.Children.Add(new TextBlock
        {
            Text = _result.LatestVersionLabel,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Theme.Brush(accent)
        });
        latestCard.Child = latestStack;
        Grid.SetColumn(latestCard, 2);
        grid.Children.Add(latestCard);

        return grid;
    }

    private FrameworkElement BuildChangelogPanel(WpfColor accent)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24, 2, 24, 10)
        };

        var labelRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = LocalizationService.Translate("What's New in this Version").ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.TextMuted)
        };
        Grid.SetColumn(label, 0);
        labelRow.Children.Add(label);

        // "View on GitHub" inline button
        var ghLink = new TextBlock
        {
            Text = $"🔗 {LocalizationService.Translate("View on GitHub")}",
            FontSize = 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = Theme.Brush(accent),
            Cursor = WpfCursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        ghLink.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            OpenUrl(_result.ReleaseUrl);
        };
        ghLink.MouseEnter += (_, _) => ghLink.TextDecorations = TextDecorations.Underline;
        ghLink.MouseLeave += (_, _) => ghLink.TextDecorations = null;
        Grid.SetColumn(ghLink, 1);
        labelRow.Children.Add(ghLink);

        panel.Children.Add(labelRow);

        // Scrollable notes box
        var notesBorder = new Border
        {
            Background = Theme.Brush(WithAlpha(Colors.Black, Theme.IsDark ? (byte)45 : (byte)15)),
            BorderBrush = Theme.Brush(Theme.BorderSubtle),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10)
        };

        var scrollViewer = new ScrollViewer
        {
            MaxHeight = 210,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false
        };

        var notesContent = BuildReleaseNotesContent(_result.ReleaseNotes, accent);
        scrollViewer.Content = notesContent;
        notesBorder.Child = scrollViewer;

        panel.Children.Add(notesBorder);

        return panel;
    }

    private FrameworkElement BuildReleaseNotesContent(string? body, WpfColor accent)
    {
        var container = new StackPanel();

        if (string.IsNullOrWhiteSpace(body))
        {
            container.Children.Add(new TextBlock
            {
                Text = LocalizationService.Translate("No release notes provided for this version."),
                FontSize = 12,
                Foreground = Theme.Brush(Theme.TextMuted),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 8, 0, 8)
            });
            return container;
        }

        var lines = body
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n")
            .Split('\n');

        bool hasItems = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Noise filters
            if (line.StartsWith("---") || line.StartsWith("***") || line.StartsWith("___")) continue;
            if (line.StartsWith("|") || line.StartsWith("<!--") || line.StartsWith("-->")) continue;
            if (line.StartsWith("![") || line.StartsWith("<")) continue;
            if (line.StartsWith("VirusTotal", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Full Changelog", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Crafted with", StringComparison.OrdinalIgnoreCase)) continue;

            // Heading (#, ##, ###)
            if (line.StartsWith("#"))
            {
                int hashes = 0;
                while (hashes < line.Length && line[hashes] == '#') hashes++;
                var headingText = line.Substring(hashes).Trim();
                if (string.IsNullOrWhiteSpace(headingText)) continue;
                if (headingText.StartsWith("Release Notes", StringComparison.OrdinalIgnoreCase)) continue;

                var h = new TextBlock
                {
                    FontSize = hashes <= 2 ? 12.5 : 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.Brush(accent),
                    Margin = new Thickness(0, hasItems ? 10 : 2, 0, 3),
                    TextWrapping = TextWrapping.Wrap
                };
                PopulateInlines(h, headingText, accent);
                container.Children.Add(h);
                hasItems = true;
                continue;
            }

            // Bullet item (- , * , • )
            if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
            {
                var bulletText = line.Substring(2).Trim();
                if (string.IsNullOrWhiteSpace(bulletText)) continue;

                int leadingSpaces = 0;
                while (leadingSpaces < rawLine.Length && char.IsWhiteSpace(rawLine[leadingSpaces])) leadingSpaces++;
                bool isSubBullet = leadingSpaces >= 2;
                double leftIndent = isSubBullet ? 14 : 2;

                var row = new Grid { Margin = new Thickness(leftIndent, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new TextBlock
                {
                    Text = isSubBullet ? "◦" : "•",
                    FontSize = isSubBullet ? 11 : 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = Theme.Brush(WithAlpha(accent, isSubBullet ? (byte)180 : (byte)255)),
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, isSubBullet ? 1 : -1, 0, 0)
                };
                Grid.SetColumn(dot, 0);
                row.Children.Add(dot);

                var content = new TextBlock
                {
                    FontSize = 11.5,
                    Foreground = Theme.Brush(Theme.TextPrimary),
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 16
                };
                PopulateInlines(content, bulletText, accent);
                Grid.SetColumn(content, 1);
                row.Children.Add(content);

                container.Children.Add(row);
                hasItems = true;
                continue;
            }

            // Regular paragraph line
            var p = new TextBlock
            {
                FontSize = 11.5,
                Foreground = Theme.Brush(Theme.TextSecondary),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16,
                Margin = new Thickness(0, 2, 0, 4)
            };
            PopulateInlines(p, line, accent);
            container.Children.Add(p);
            hasItems = true;
        }

        if (!hasItems)
        {
            container.Children.Add(new TextBlock
            {
                Text = LocalizationService.Translate("No release notes provided for this version."),
                FontSize = 12,
                Foreground = Theme.Brush(Theme.TextMuted),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 8, 0, 8)
            });
        }

        return container;
    }

    private static void PopulateInlines(TextBlock target, string text, WpfColor accent)
    {
        // Split by markdown bold (**text**) and inline code (`code`)
        var parts = Regex.Split(text, @"(\*\*[^*]+\*\*|`[^`]+`)");
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;

            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
            {
                target.Inlines.Add(new Run(part.Substring(2, part.Length - 4))
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.Brush(Theme.TextPrimary)
                });
            }
            else if (part.StartsWith("`") && part.EndsWith("`") && part.Length > 2)
            {
                target.Inlines.Add(new Run($" {part.Substring(1, part.Length - 2)} ")
                {
                    FontFamily = new WpfFontFamily("Consolas, Cascadia Code, Segoe UI Mono"),
                    FontSize = 10.5,
                    Foreground = Theme.Brush(accent)
                });
            }
            else
            {
                target.Inlines.Add(new Run(part));
            }
        }
    }

    private StackPanel BuildProgressPanel(WpfColor accent)
    {
        var panel = new StackPanel
        {
            Margin = new Thickness(24, 6, 24, 12),
            Visibility = Visibility.Collapsed
        };

        _progressText = new TextBlock
        {
            Text = LocalizationService.Translate("Downloading update (0.0%)..."),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = Theme.Brush(Theme.TextPrimary),
            Margin = new Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(_progressText);

        _progressBar = new ProgressBar
        {
            Height = 6,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Foreground = Theme.Brush(accent),
            Background = Theme.Brush(WithAlpha(Colors.White, Theme.IsDark ? (byte)20 : (byte)35)),
            BorderThickness = new Thickness(0)
        };
        panel.Children.Add(_progressBar);

        _errorText = new TextBlock
        {
            FontSize = 11,
            Foreground = Theme.Brush(Theme.IsDark ? WpfColor.FromRgb(255, 100, 120) : WpfColor.FromRgb(210, 40, 40)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(_errorText);

        _errorActionsRow = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        var errorBtns = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right
        };
        var browserBtn = BuildButton(LocalizationService.Translate("Open Browser"), isPrimary: true, accent, () =>
        {
            OpenUrl(_result.ReleaseUrl);
            Close();
        });
        browserBtn.Margin = new Thickness(0, 0, 8, 0);
        errorBtns.Children.Add(browserBtn);

        var closeErrBtn = BuildButton(LocalizationService.Translate("Close"), isPrimary: false, accent, () => Close());
        errorBtns.Children.Add(closeErrBtn);

        _errorActionsRow.Child = errorBtns;
        panel.Children.Add(_errorActionsRow);

        return panel;
    }

    private Border BuildButtonsRow(WpfColor accent)
    {
        var border = new Border
        {
            Padding = new Thickness(24, 12, 24, 14),
            Background = Theme.Brush(WithAlpha(Colors.Black, Theme.IsDark ? (byte)25 : (byte)10))
        };

        var stack = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right
        };

        var laterBtn = BuildButton(LocalizationService.Translate("Later"), isPrimary: false, accent, () => Close());
        laterBtn.Margin = new Thickness(0, 0, 10, 0);
        stack.Children.Add(laterBtn);

        var downloadBtn = BuildButton(LocalizationService.Translate("Download and Install"), isPrimary: true, accent, async () =>
        {
            await StartDownloadAsync();
        });
        stack.Children.Add(downloadBtn);

        border.Child = stack;
        return border;
    }

    private async Task StartDownloadAsync()
    {
        if (_downloadStarted) return;
        _downloadStarted = true;

        // Switch dialog into downloading mode
        _buttonsRow.Visibility = Visibility.Collapsed;
        _progressPanel.Visibility = Visibility.Visible;
        _errorText.Visibility = Visibility.Collapsed;
        _errorActionsRow.Visibility = Visibility.Collapsed;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var updatesFolder = Path.Combine(appData, "CyberSnap", "Updates");
        var filename = _result.AssetName ?? $"cybersnap_setup_{UpdateService.GetRuntimeChannel()}.exe";
        var installerPath = Path.Combine(updatesFolder, filename);

        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<double>(val =>
        {
            _progressBar.Value = val;
            _progressText.Text = string.Format(LocalizationService.Translate("Downloading update ({0:F1}%)..."), val);
        });

        try
        {
            if (string.IsNullOrEmpty(_result.DownloadUrl))
                throw new InvalidOperationException("Direct download link is not available for this release.");

            await UpdateService.DownloadUpdateAsync(_result.DownloadUrl, installerPath, progress, _downloadCts.Token);

            _progressText.Text = LocalizationService.Translate("Download completed. Launching installer...");
            await Task.Delay(500);

            UpdateService.LaunchInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            _downloadStarted = false;
            _progressText.Text = LocalizationService.Translate("Download failed");
            _errorText.Text = ex.Message;
            _errorText.Visibility = Visibility.Visible;
            _errorActionsRow.Visibility = Visibility.Visible;
        }
    }

    private FrameworkElement BuildClose()
    {
        var close = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = WpfBrushes.Transparent,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = WpfCursors.Hand,
            Child = new Image
            {
                Source = FluentIcons.RenderWpf("close", ToDrawingColor(Theme.TextSecondary, 240), 14),
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = WpfHorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        close.MouseEnter += (_, _) => close.Background = Theme.Brush(Theme.TabHoverBg);
        close.MouseLeave += (_, _) => close.Background = WpfBrushes.Transparent;
        close.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (!_downloadStarted)
                Close();
        };
        return close;
    }

    private Button BuildButton(string label, bool isPrimary, WpfColor accent, Action onClick)
    {
        var btn = new Button
        {
            Content = label,
            Cursor = WpfCursors.Hand,
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0),
            FontSize = 12,
            FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal
        };

        if (isPrimary)
        {
            btn.Background = Theme.Brush(accent);
            btn.Foreground = Theme.Brush(Theme.IsDark ? Colors.Black : Colors.White);
            btn.BorderBrush = Theme.Brush(accent);
            btn.BorderThickness = new Thickness(1);
            btn.Effect = Glow(accent, 6, 0.25);
            btn.MouseEnter += (_, _) =>
            {
                btn.Background = Theme.Brush(WithAlpha(accent, 225));
                btn.Effect = Glow(accent, 10, 0.40);
            };
            btn.MouseLeave += (_, _) =>
            {
                btn.Background = Theme.Brush(accent);
                btn.Effect = Glow(accent, 6, 0.25);
            };
        }
        else
        {
            btn.Background = Theme.Brush(SecondaryButtonBg);
            btn.Foreground = Theme.Brush(Theme.TextPrimary);
            btn.BorderBrush = Theme.Brush(SecondaryButtonBorder);
            btn.BorderThickness = new Thickness(1);
            btn.MouseEnter += (_, _) =>
            {
                btn.Background = Theme.Brush(Theme.TabHoverBg);
                btn.BorderBrush = Theme.Brush(Theme.BorderSubtle);
            };
            btn.MouseLeave += (_, _) =>
            {
                btn.Background = Theme.Brush(SecondaryButtonBg);
                btn.BorderBrush = Theme.Brush(SecondaryButtonBorder);
            };
        }

        btn.Template = CreateButtonTemplate();
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static ControlTemplate CreateButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(Button.Background)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Button.BorderBrush)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding(nameof(Button.BorderThickness)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding(nameof(Button.Padding)) { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, WpfHorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private static void AssignDialogOwner(ThemedUpdateDialog dialog, Window? owner, IntPtr ownerHandle)
    {
        if (owner is { IsVisible: true })
        {
            if (owner.WindowState == WindowState.Minimized)
                owner.WindowState = WindowState.Normal;
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            if (owner.Topmost)
                dialog.Topmost = true;
            return;
        }

        if (ownerHandle == IntPtr.Zero)
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        if (User32.IsIconic(ownerHandle))
            User32.ShowWindow(ownerHandle, User32.SW_RESTORE);

        new WindowInteropHelper(dialog).Owner = ownerHandle;

        int exStyle = User32.GetWindowLongA(ownerHandle, User32.GWL_EXSTYLE);
        if ((exStyle & User32.WS_EX_TOPMOST) != 0)
            dialog.Topmost = true;

        dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
    }

    private static DropShadowEffect Glow(WpfColor color, double blur, double opacity) => new()
    {
        Color = color,
        ShadowDepth = 0,
        BlurRadius = blur,
        Opacity = opacity
    };

    private static WpfColor WithAlpha(WpfColor c, byte alpha) => WpfColor.FromArgb(alpha, c.R, c.G, c.B);

    private static System.Drawing.Color ToDrawingColor(WpfColor color, byte alpha) =>
        System.Drawing.Color.FromArgb(alpha, color.R, color.G, color.B);

    private static WpfColor PanelBackground =>
        Theme.IsDark ? WpfColor.FromRgb(15, 17, 26) : WpfColor.FromRgb(245, 246, 248);

    private static WpfColor SecondaryButtonBg =>
        Theme.IsDark ? WpfColor.FromRgb(26, 30, 46) : WpfColor.FromRgb(249, 249, 249);

    private static WpfColor SecondaryButtonBorder =>
        Theme.IsDark ? WpfColor.FromArgb(32, 255, 255, 255) : WpfColor.FromArgb(26, 0, 0, 0);
}
