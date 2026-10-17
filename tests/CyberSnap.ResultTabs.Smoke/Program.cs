using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using CyberSnap.Services;
using CyberSnap.UI;
using ZXing;

// Run on Windows: dotnet run --project tests/CyberSnap.ResultTabs.Smoke -c Release
// Uses real WPF windows and temporary settings, without starting tray/IPC/hotkeys,
// calling translation providers, or changing the clipboard.
internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Real result windows request foreground. Isolate this automated fixture
        // from physical input on the shared desktop; all test edits are explicit.
        InputManager.Current.PreProcessInput += (_, e) =>
        {
            if (e.StagingItem.Input is KeyEventArgs or TextCompositionEventArgs or MouseButtonEventArgs or MouseWheelEventArgs)
                e.Cancel();
        };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppResources.xaml"))
            .Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "ui", "clr-namespace:CyberSnap.UI;assembly=CyberSnap"),
            resources.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        string temp = Path.Combine(Path.GetTempPath(), "CyberSnap-ResultTabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using var settings = new SettingsService(Path.Combine(temp, "settings.json"));
        settings.Settings.InterfaceLanguage = "en";
        try
        {
            CheckOcr(settings, args.FirstOrDefault());
            CheckQr(settings, args.FirstOrDefault());
            Console.WriteLine($"PASS: {_checks} result-tab integration checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            foreach (Window window in app.Windows.Cast<Window>().ToArray())
            {
                if (window.GetType().Name is "OcrResultWindow" or "QrResultWindow")
                {
                    object host = Field(window, "_tabs");
                    while (window.IsVisible)
                        Call(host, "CloseActive");
                }
            }
            settings.Dispose();
            Directory.Delete(temp, recursive: true);
            app.Shutdown();
        }
    }

    private static void CheckOcr(SettingsService settings, string? renderDirectory)
    {
        var preview = new DrawingImage(new GeometryDrawing(Brushes.Cyan, null, new RectangleGeometry(new Rect(0, 0, 120, 80))));
        var window = OcrResultWindow.ShowResult("first OCR text", settings, preview, "first OCR text");
        Pump();
        var strip = Element<PreviewTabStrip>(window, "ResultTabStrip");
        var text = Element<TextBox>(window, "OcrTextBox");
        var search = Element<TextBox>(window, "SearchTextBox");
        var translated = Element<TextBox>(window, "TranslatedTextBox");
        object host = Field(window, "_tabs");
        object first = Property(host, "Active");
        Assert(strip.Visibility == Visibility.Collapsed, "OCR hides tabs for a single result");
        Assert(Element<TextBlock>(window, "CopyBtnText").Text == "Close", "OCR preserves auto-copy delivery state");
        text.Text = "edited OCR text with match and match";
        search.Text = "match";
        Call(window, "MoveToSearchMatch", 1);
        int matchIndex = (int)Field(window, "_currentMatchIndex");
        text.Select(7, 3);
        Call(window, "SetTranslationPanelExpanded", true, false);
        translated.Text = "traducción conservada";
        SelectTag(Element<ComboBox>(window, "FromLanguageCombo"), "en");
        translated.Text = "traducción conservada";

        var same = OcrResultWindow.ShowResult("second OCR", settings);
        Pump();
        Assert(ReferenceEquals(window, same), "OCR reuses its window");
        Assert(strip.Visibility == Visibility.Visible && text.Text == "second OCR", $"OCR adds and selects the new result (actual: {text.Text}, {strip.Visibility})");
        Assert(search.Text == "" && translated.Text == "", "new OCR result starts with isolated state");
        SelectTag(Element<ComboBox>(window, "FromLanguageCombo"), "es");
        Call(host, "ActivateAt", 0);
        Pump();
        Assert(text.Text == "edited OCR text with match and match", $"OCR restores edited text (actual: {text.Text})");
        Assert(text.SelectionStart == 7 && text.SelectionLength == 3, "OCR restores selection");
        Assert(search.Text == "match" && (int)Field(window, "_currentMatchIndex") == matchIndex, "OCR restores search and match");
        Assert(translated.Text == "traducción conservada", "OCR restores translation");
        Assert(ReferenceEquals(Element<System.Windows.Controls.Image>(window, "OcrPreviewImage").Source, preview), "OCR restores source preview");
        Assert(((ComboBoxItem)Element<ComboBox>(window, "FromLanguageCombo").SelectedItem).Tag as string == "en", "OCR restores language choice");
        Assert(settings.Settings.OcrDefaultTranslateFrom == "es", "tab switching does not change saved defaults");
        Assert(!text.CanUndo, "undo cannot restore text from another result");

        // Complete an inactive result without a network request. The same UI
        // apply path is used by asynchronous translation completion.
        Call(host, "ActivateAt", 1);
        SetProperty(first, "TranslatedText", "background translation");
        Call(window, "ApplyTranslationSession", first);
        Assert(translated.Text == "", "background translation cannot alter the selected result");
        Call(host, "ActivateAt", 0);
        Assert(translated.Text == "background translation", "background result is available when its tab is selected");
        using var pending = new CancellationTokenSource();
        SetProperty(first, "TranslationCts", pending);
        SetProperty(first, "CheckingTranslation", true);
        Call(host, "ActivateAt", 1);
        Assert(!pending.IsCancellationRequested, "switching tabs keeps translation alive");
        Call(host, "CloseAt", 0);
        Assert(pending.IsCancellationRequested, "closing an inactive tab cancels its translation");
        Assert(text.Text == "second OCR" && strip.Visibility == Visibility.Collapsed, "closing inactive OCR preserves selected result");
        Call(host, "Reopen");
        Assert(text.Text.StartsWith("edited OCR") && translated.Text == "background translation", "OCR can reopen its last closed result");

        settings.Settings.InterfaceLanguage = "es";
        window.RefreshLocalization();
        Pump();
        Assert(text.Text == "edited OCR text with match and match", $"localization refresh preserves recognized text (actual: {text.Text})");
        Assert(translated.Text == "background translation", "localization refresh preserves the current translation");
        Assert(LocalizationService.Translate("Close all results?") == "¿Cerrar todos los resultados?", "Spanish close-all text is available");
        Render(window, renderDirectory, "ocr-tabs-es.png");
        RespondToConfirmation(false);
        window.Close();
        Assert(window.IsVisible && !(bool)Property(host, "IsClosing"), "canceling close-all keeps the OCR window usable");
        Assert(ReferenceEquals(window, OcrResultWindow.ShowResult("third OCR", settings)), "OCR still accepts results after canceling close-all");
        RespondToConfirmation(true);
        window.Close();
        Assert(!window.IsVisible && (bool)Property(host, "IsClosing"), "confirming close-all closes OCR");
        window = OcrResultWindow.ShowResult("fresh OCR", settings);
        Assert(!ReferenceEquals(window, same), "OCR creates a fresh window after the last one closes");
        Call(Field(window, "_tabs"), "CloseActive");
    }

    private static void CheckQr(SettingsService settings, string? renderDirectory)
    {
        settings.Settings.InterfaceLanguage = "en";
        var window = QrResultWindow.ShowResult("https://example.com", BarcodeFormat.QR_CODE, settings, alreadyCopiedText: "https://example.com");
        Pump();
        object host = Field(window, "_tabs");
        var text = Element<TextBox>(window, "ContentTextBox");
        Assert(Element<PreviewTabStrip>(window, "ResultTabStrip").Visibility == Visibility.Collapsed, "QR hides tabs for one result");
        Assert(Element<TextBlock>(window, "CopyButtonText").Text == "Close", "QR preserves copied state");
        text.Select(3, 4);
        Assert(ReferenceEquals(window, QrResultWindow.ShowResult("12345", BarcodeFormat.CODE_128, settings)), "QR and barcodes reuse one window");
        Assert(Element<TextBlock>(window, "FormatText").Text == "Code 128" && text.Text == "12345", "QR switches format with content");
        Call(host, "ActivateAt", 0);
        Assert(text.Text == "https://example.com" && text.SelectionStart == 3 && text.SelectionLength == 4, "QR restores content and selection");
        Assert(Element<TextBlock>(window, "CopyButtonText").Text == "Close", "QR restores copied status");
        Call(host, "CloseActive");
        Assert(window.IsVisible && text.Text == "12345", "QR closes only the active result");
        Call(host, "Reopen");
        window.WindowState = WindowState.Minimized;
        QrResultWindow.ShowResult("aztec", BarcodeFormat.AZTEC, settings);
        Assert(window.WindowState == WindowState.Normal && text.Text == "aztec", "new QR results restore a minimized window");
        for (int i = 0; i < 12; i++)
            QrResultWindow.ShowResult("code " + i, BarcodeFormat.EAN_13, settings);
        Pump();
        Render(window, renderDirectory, "qr-tabs-en.png");
        Assert(Application.Current.Windows.OfType<QrResultWindow>().Count() == 1, "many scans still use one QR window");
        RespondToConfirmation(true);
        window.Close();
        Assert(!window.IsVisible, "QR close-all closes every result");
    }

    private static void SelectTag(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(item => item.Tag as string == tag);

    private static T Element<T>(Window window, string name) where T : class => (T)window.FindName(name);
    private static object Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object Property(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
    private static void SetProperty(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);

    private static void RespondToConfirmation(bool confirm)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        int ticks = 0;
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.GetType().Name == "ThemedConfirmDialog");
            if (dialog is not null)
            {
                timer.Stop();
                // Exercise the dialog's real result contract.
                dialog.GetType().GetField("_confirmed", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dialog, confirm);
                dialog.DialogResult = confirm;
            }
            else if (++ticks > 100)
            {
                timer.Stop();
                throw new TimeoutException("The close-all confirmation did not appear.");
            }
        };
        timer.Start();
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Render(Window window, string? directory, string name)
    {
        if (directory is null)
            return;
        Pump();
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
        _checks++;
        Console.WriteLine("PASS: " + message);
    }

}
