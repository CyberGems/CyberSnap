using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CyberSnap.Models;
using CyberSnap.Services;
using CyberSnap.UI;
using CyberSnap.UI.Controls;
using ZXing;
using Color = System.Drawing.Color;

internal static partial class Program
{
    /// <summary>
    /// Suite gesture: F1 opens About from the branding on every window that hosts the
    /// shared title bar, and the About window leaves the key unhandled instead of
    /// reopening itself. Each window also advertises the chip on that branding.
    /// </summary>
    private static void CheckSharedTitleAboutGestures(CyberSnap.App app, SettingsService settings, string temp)
    {
        using var history = new HistoryService();
        using var search = new ImageSearchIndexService();
        SetZoomAppField(app, "_historyService", history);
        SetZoomAppField(app, "_imageSearchIndexService", search);
        settings.Settings.AutoIndexImages = false;
        settings.Settings.CapturePreviewTimeoutSeconds = 0;

        // A one-frame GIF keeps the Trimmer fixture independent from FFmpeg.
        string gifPath = Path.Combine(temp, "title-bar-about.gif");
        using (var frame = new Bitmap(64, 48))
        {
            using (var graphics = Graphics.FromImage(frame))
                graphics.Clear(Color.SteelBlue);
            using var gif = AnimatedGif.AnimatedGif.Create(gifPath, 100);
            gif.AddFrame(frame, delay: -1, quality: AnimatedGif.GifQuality.Bit8);
        }

        var windows = new (string Name, Func<Window> Open)[]
        {
            ("Settings", () => new SettingsWindow(settings, history, search)),
            ("OCR", () => OcrResultWindow.ShowResult("title bar F1", settings)),
            ("QR", () => QrResultWindow.ShowResult("title bar F1", BarcodeFormat.QR_CODE, settings)),
            ("Trimmer", () => VideoTrimmerWindow.ShowFile(gifPath, settings)),
            ("Preview", () => new CapturePreviewDialog(new Bitmap(64, 48), settings)),
            ("Achievements", () => new AchievementsWindow(settings, history))
        };

        foreach (var (name, open) in windows)
        {
            Window? window = null;
            try
            {
                window = open();
                if (!window.IsVisible)
                    window.Show();
                if (window is CapturePreviewDialog preview)
                    Call(preview, "SetAutoCloseEnabled", false);
                Pump();
                AwaitCondition(() => window.IsVisible && window.Opacity == 1, $"{name} did not finish opening", 10000);

                var titleBar = Descendants(window).OfType<CyberSnapTitleBar>().FirstOrDefault()
                    ?? throw new InvalidOperationException($"{name} has no shared title bar");
                // A window can be visible before its title bar raises Loaded and hooks
                // PreviewKeyDown, so the F1 raise must wait for that subscription instead
                // of racing it (the Trimmer intermittently lost the key on slow runners).
                AwaitCondition(() => ReferenceEquals(TitleBarSubscribedWindow.GetValue(titleBar), window),
                    $"{name} did not attach its title-bar shortcut handler");
                var aboutButton = (Button)titleBar.FindName("TitleAboutButton");
                Assert(aboutButton.IsHitTestVisible && TextOf(aboutButton.ToolTip).Contains("F1", StringComparison.Ordinal),
                    $"{name} advertises F1 on its title-bar branding");

                var f1 = TitleBarKey(window, Key.F1);
                window.RaiseEvent(f1);
                Pump();
                AwaitCondition(() => ZoomAppField(app, "_aboutWindow") is AboutWindow { IsVisible: true },
                    $"{name} did not open About with F1", 5000);
                Assert(f1.Handled, $"{name} consumes F1 after opening About");

                var about = (AboutWindow)ZoomAppField(app, "_aboutWindow")!;
                var f1OnAbout = TitleBarKey(about, Key.F1);
                about.RaiseEvent(f1OnAbout);
                Pump();
                Assert(!f1OnAbout.Handled && ReferenceEquals(ZoomAppField(app, "_aboutWindow"), about),
                    $"{name}: the About window ignores F1 instead of reopening itself");

                about.Close();
                AwaitCondition(() => ZoomAppField(app, "_aboutWindow") is null,
                    $"Closing About from {name} did not release it", 5000);
            }
            finally
            {
                (ZoomAppField(app, "_aboutWindow") as AboutWindow)?.Close();
                window?.Close();
                Pump();
            }
        }

        SetZoomAppField(app, "_historyService", null);
        SetZoomAppField(app, "_imageSearchIndexService", null);
    }

    private static readonly FieldInfo TitleBarSubscribedWindow =
        typeof(CyberSnapTitleBar).GetField("_subscribedWindow", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static KeyEventArgs TitleBarKey(Window window, Key key) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };

    /// <summary>Logical + visual walk, so tooltip content that is not in a live visual tree
    /// yet still contributes its label and keycap.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (object? child in LogicalTreeHelper.GetChildren(current))
                if (child is DependencyObject logical)
                {
                    yield return logical;
                    pending.Push(logical);
                }
            if (current is not (Visual or System.Windows.Media.Media3D.Visual3D))
                continue;
            int count = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < count; index++)
            {
                var visual = VisualTreeHelper.GetChild(current, index);
                yield return visual;
                pending.Push(visual);
            }
        }
    }

    private static string TextOf(object? content) => content switch
    {
        null => string.Empty,
        string text => text,
        DependencyObject element => string.Join(" ", new[] { element }.Concat(Descendants(element))
            .OfType<TextBlock>().Select(block => block.Text)),
        _ => content.ToString() ?? string.Empty
    };
}
