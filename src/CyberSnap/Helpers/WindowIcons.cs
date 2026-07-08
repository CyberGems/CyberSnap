using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;

namespace CyberSnap.Helpers;

public enum WindowIconKind
{
    Main,
    Settings,
    History,
    Ocr,
    Trimmer,
    Editor
}

public static class WindowIcons
{
    private static readonly IReadOnlyDictionary<WindowIconKind, string> FileNames = new Dictionary<WindowIconKind, string>
    {
        [WindowIconKind.Main] = "CyberSnap.ico",
        [WindowIconKind.Settings] = "Configuration.ico",
        [WindowIconKind.History] = "Gallery.ico",
        [WindowIconKind.Ocr] = "OCR.ico",
        [WindowIconKind.Trimmer] = "Trimmer.ico",
        [WindowIconKind.Editor] = "Editor.ico",
    };

    public static ImageSource Wpf(WindowIconKind kind, int size = 32)
    {
        using var stream = OpenIconStream(kind);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        buffer.Position = 0;

        var decoder = BitmapDecoder.Create(buffer, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames
            .OrderBy(f => Math.Abs(f.PixelWidth - size))
            .ThenByDescending(f => f.PixelWidth)
            .First();
        frame.Freeze();
        return frame;
    }

    public static DrawingIcon WinForms(WindowIconKind kind)
    {
        var path = ResolveIconPath(kind);
        if (path != null)
            return new DrawingIcon(path);

        using var stream = OpenPackResourceStream(kind);
        return new DrawingIcon(stream);
    }

    public static string FilePath(WindowIconKind kind) =>
        ResolveIconPath(kind) ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", FileNames[kind]);

    public static string ResourceUri(WindowIconKind kind) =>
        $"pack://application:,,,/Assets/Icons/{FileNames[kind]}";

    private static Stream OpenIconStream(WindowIconKind kind)
    {
        var path = ResolveIconPath(kind);
        if (path != null)
            return File.OpenRead(path);

        return OpenPackResourceStream(kind);
    }

    private static Stream OpenPackResourceStream(WindowIconKind kind)
    {
        var uri = new Uri(ResourceUri(kind), UriKind.Absolute);
        var info = Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException($"Window icon resource not found: {FileNames[kind]}");
        return info.Stream;
    }

    private static string? ResolveIconPath(WindowIconKind kind)
    {
        var fileName = FileNames[kind];
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "Icons", fileName)),
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
