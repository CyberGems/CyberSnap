using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;

internal static partial class Program
{
    private static void CheckPreviewEffectStates(CapturePreviewDialog preview, string language, string? renders)
    {
        var checker = Element<ToggleButton>(preview, "PreviewBackgroundBtn");
        var margin = Element<Button>(preview, "PreviewBackgroundColorBtn");
        var outline = Element<Button>(preview, "PreviewOutlineBtn");
        var root = Element<FrameworkElement>(preview, "RootBorder");
        var session = Field(preview, "_active");
        bool originalChecker = checker.IsChecked == true;
        bool originalMargin = (bool)Property(session, "MarginEnabled");
        int originalSize = (int)Property(session, "MarginSize");
        bool originalOutline = (bool)Property(session, "OutlineEnabled");
        int originalThickness = (int)Property(session, "OutlineThickness");
        int originalColor = (int)Property(session, "OutlineColorArgb");
        try
        {
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Grayscale, AppThemeMode.Light })
            {
                Theme.SetMode(theme); preview.ApplyTheme(); Pump();
                for (int flags = 0; flags < 8; flags++)
                {
                    checker.IsChecked = (flags & 1) != 0;
                    Call(preview, "ApplyMarginSettings", (flags & 2) != 0, 12);
                    Call(preview, "ApplyOutlineSettings", (flags & 4) != 0, 3, System.Drawing.Color.Black.ToArgb());
                    Call(preview, "SetZoomOverlayVisibility", true); Pump();
                    MovePickerPointer(root, new Point(4, 4)); preview.UpdateLayout();
                    CheckState(checker, (flags & 1) != 0);
                    CheckState(margin, (flags & 2) != 0);
                    CheckState(outline, (flags & 4) != 0);
                    Assert(AutomationProperties.GetItemStatus(margin) == LocalizationService.Translate(language, (flags & 2) != 0 ? "Active" : "Inactive")
                        && AutomationProperties.GetItemStatus(outline) == LocalizationService.Translate(language, (flags & 4) != 0 ? "Active" : "Inactive"),
                        $"margin and outline report their actual localized state in {language}");
                    if (renders is not null && flags is 0 or 5 or 7)
                    {
                        Call(preview, "CancelZoomHideTimer");
                        Call(preview, "SetZoomOverlayVisibility", true);
                        Thread.Sleep(220); Pump();
                        Render(preview, renders, $"preview-effect-states-{theme}-{language}-{flags}.png");
                    }
                }
                foreach (var button in new ButtonBase[] { checker, margin, outline })
                {
                    MovePickerPointer(button, new Point(button.ActualWidth / 2, button.ActualHeight / 2)); button.UpdateLayout();
                    Assert(button.IsMouseOver && ColorOf(Chrome(button).Background) == Theme.AccentHover,
                        "all active preview controls share the checkerboard hover highlight");
                }
            }
            Call(preview, "ApplyMarginSettings", false, 12); Pump();
            margin.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Assert((bool)margin.Tag == false && !(bool)Property(session, "MarginEnabled"),
                "opening margin options does not mark an unapplied margin as active");
            Call(preview, "ClosePreviewBackgroundColorPicker", false);
            var icon = (BitmapSource)Element<Image>(preview, "PreviewOutlineIcon").Source;
            var crop = FluentIcons.RenderWpf("crop", System.Drawing.Color.FromArgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B), icon.PixelWidth)!;
            byte[] Pixels(BitmapSource image)
            {
                var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
                image.CopyPixels(bytes, image.PixelWidth * 4, 0);
                return bytes;
            }
            Assert(!Pixels(icon).SequenceEqual(Pixels(crop)), "the outline glyph is distinct from the editor's crop icon");
        }
        finally
        {
            Call(preview, "ClosePreviewBackgroundColorPicker", false);
            Call(preview, "ApplyMarginSettings", originalMargin, originalSize);
            Call(preview, "ApplyOutlineSettings", originalOutline, originalThickness, originalColor);
            checker.IsChecked = originalChecker;
            Theme.SetMode(AppThemeMode.Dark); preview.ApplyTheme(); Pump();
        }

        static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
        static Border Chrome(ButtonBase button) => (Border)button.Template.FindName("Bd", button);
        void CheckState(ButtonBase button, bool active)
        {
            Assert(ColorOf(button.Foreground) == (active ? Theme.Accent : Theme.TextSecondary)
                && ColorOf(Chrome(button).Background) == (active ? Theme.AccentSubtle : Theme.BgSecondary),
                $"{button.Name} uses a neutral inactive state and the shared active highlight (active={active}, tag={button.Tag}, foreground={ColorOf(button.Foreground)}, background={ColorOf(Chrome(button).Background)})");
        }
    }
}
