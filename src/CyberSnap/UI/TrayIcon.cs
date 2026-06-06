using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Forms;
using CyberSnap.Helpers;
using CyberSnap.Models;
using CyberSnap.Services;

namespace CyberSnap.UI;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly AppSettings? _settings;
    private Icon? _defaultIcon;
    private Icon? _recordingIcon;
    private ContextMenuStrip? _menu;
    private ToolStripMenuItem? _recordItem;
    private bool _isShowingRecording;

    public event Action? OnCapture;
    public event Action? OnOcr;
    public event Action? OnColorPicker;
    public event Action? OnScrollCapture;
    public event Action? OnRuler;
    public event Action? OnAnnotationEditor;
    public event Action? OnSettings;
    public event Action? OnHistory;
    public event Action? OnQuit;
    public event Action<RecordingFormat>? OnRecordRequested;

    public TrayIcon(AppSettings? settings = null)
    {
        _settings = settings;
        Theme.Refresh();
        _defaultIcon = CreateDefaultIcon();
        _notifyIcon = new NotifyIcon
        {
            Text = T("CyberSnap - Left-click to activate / Right-click for menu"),
            Icon = _defaultIcon,
            Visible = true
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (Capture.RecordingForm.Current != null)
                    Capture.RecordingForm.Current.RequestStop();
                else
                    OnCapture?.Invoke();
            }
            else if (e.Button == MouseButtons.Right)
                ShowMenu();
        };

        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OnCapture?.Invoke();
        };

        _menu = CreateThemedMenu();
        _notifyIcon.ContextMenuStrip = _menu;
    }

    public void UpdateRecordingState(bool isRecording)
    {
        if (isRecording == _isShowingRecording) return;
        _isShowingRecording = isRecording;
        if (isRecording)
        {
            _recordingIcon ??= CreateRecordingIcon();
            _notifyIcon.Icon = _recordingIcon;
            _notifyIcon.Text = T("CyberSnap recording - click to stop, right-click for menu");
        }
        else
        {
            _notifyIcon.Icon = _defaultIcon;
            _notifyIcon.Text = T("CyberSnap - Left-click to activate / Right-click for menu");
        }
    }

    public void RefreshLocalization()
    {
        _notifyIcon.Text = _isShowingRecording
            ? T("CyberSnap recording - click to stop, right-click for menu")
            : T("CyberSnap - Left-click to activate / Right-click for menu");

        var oldMenu = _menu;
        _menu = CreateThemedMenu();
        _notifyIcon.ContextMenuStrip = _menu;
        oldMenu?.Dispose();
    }

    private ContextMenuStrip CreateThemedMenu()
    {
        Theme.Refresh();
        var menu = WindowsMenuRenderer.Create(showImages: true, minWidth: WindowsMenuRenderer.DefaultWidth);
        bool isRec = Capture.RecordingForm.Current != null;

        var captureItem  = WindowsMenuRenderer.Item(T("Start capture"), HotkeyHint("rect"), "rect");
        var scrollItem   = WindowsMenuRenderer.Item(T("Scroll capture"), HotkeyHint("_scrollCapture"), "scrollCapture");
        var ocrItem      = WindowsMenuRenderer.Item(T("Text extraction (OCR)"), HotkeyHint("ocr"), "ocr");
        var pickerItem   = WindowsMenuRenderer.Item(T("Color picker"), HotkeyHint("picker"), "picker");
        var rulerItem    = WindowsMenuRenderer.Item(T("Ruler"), HotkeyHint("ruler"), "ruler");

        ToolStripMenuItem? recordItem = null;
        ToolStripMenuItem? recordGifItem = null;

        if (isRec)
        {
            recordItem = WindowsMenuRenderer.Item(T("Stop recording"), null, "play", active: true, danger: true);
            recordItem.Click += (_, _) =>
            {
                if (Capture.RecordingForm.Current != null)
                    Capture.RecordingForm.Current.RequestStop();
            };
            _recordItem = recordItem;
        }
        else
        {
            recordItem = WindowsMenuRenderer.Item(T("Screen Recorder (MP4)"), HotkeyHint("_record"), "record");
            recordItem.Click += (_, _) => OnRecordRequested?.Invoke(RecordingFormat.MP4);
            _recordItem = recordItem;

            recordGifItem = WindowsMenuRenderer.Item(T("Screen Recorder (GIF)"), null, "recordGif");
            recordGifItem.Click += (_, _) => OnRecordRequested?.Invoke(RecordingFormat.GIF);
        }

        var settingsItem       = WindowsMenuRenderer.Item(T("Settings"), iconId: "gear");
        var historyItem        = WindowsMenuRenderer.Item(T("Capture History"), iconId: "folder");
        var annotationEditorItem = WindowsMenuRenderer.Item(T("Annotations Editor"), iconId: "draw");
        var quitItem           = WindowsMenuRenderer.Item(T("Shutdown"), iconId: "close", danger: true);

        captureItem.Click += (_, _) => OnCapture?.Invoke();
        ocrItem.Click     += (_, _) => OnOcr?.Invoke();
        pickerItem.Click  += (_, _) => OnColorPicker?.Invoke();
        rulerItem.Click   += (_, _) => OnRuler?.Invoke();
        scrollItem.Click   += (_, _) => OnScrollCapture?.Invoke();
        settingsItem.Click         += (_, _) => OnSettings?.Invoke();
        historyItem.Click          += (_, _) => OnHistory?.Invoke();
        annotationEditorItem.Click += (_, _) => OnAnnotationEditor?.Invoke();
        quitItem.Click             += (_, _) => OnQuit?.Invoke();

        // ── App header ──
        var headerLabel = new ToolStripLabel($"CyberSnap  {Services.UpdateService.GetCurrentVersionLabel()}")
        {
            ForeColor = UiChrome.SurfaceTextMuted,
            Font = UiChrome.ChromeFont(8.5f),
            Padding = new System.Windows.Forms.Padding(10, 12, 0, 2),
            AutoSize = true,
        };
        menu.Items.Add(headerLabel);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(captureItem);
        menu.Items.Add(scrollItem);
        menu.Items.Add(recordItem);
        if (recordGifItem != null)
        {
            menu.Items.Add(recordGifItem);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(ocrItem);
        menu.Items.Add(pickerItem);
        menu.Items.Add(rulerItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(historyItem);
        menu.Items.Add(annotationEditorItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        WindowsMenuRenderer.NormalizeItemWidths(menu);
        return menu;
    }

    private string? HotkeyHint(string toolId)
    {
        if (_settings == null) return null;
        var (mod, key) = _settings.GetToolHotkey(toolId);
        if (key == 0) return null;
        var parts = new System.Text.StringBuilder();
        if ((mod & Native.User32.MOD_CONTROL) != 0) parts.Append("Ctrl+");
        if ((mod & Native.User32.MOD_ALT) != 0) parts.Append("Alt+");
        if ((mod & Native.User32.MOD_SHIFT) != 0) parts.Append("Shift+");
        if ((mod & Native.User32.MOD_WIN) != 0) parts.Append("Win+");
        var keyName = ((System.Windows.Forms.Keys)key).ToString();
        keyName = keyName switch
        {
            "Oemtilde" or "OemTilde" => "`",
            "OemMinus" => "-",
            "Oemplus" or "OemPlus" => "=",
            "Snapshot" => "PrtSc",
            "Pause" => "Pause",
            "Cancel" => "Break",
            _ => keyName.Replace("Oem", "")
        };
        parts.Append(keyName);
        return parts.ToString();
    }

    private void ShowMenu()
    {
        var oldMenu = _menu;
        _menu = CreateThemedMenu();
        _notifyIcon.ContextMenuStrip = _menu;
        oldMenu?.Dispose();

        var showMethod = typeof(NotifyIcon).GetMethod("ShowContextMenu",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        showMethod?.Invoke(_notifyIcon, null);
    }

    private static string T(string text) => LocalizationService.Translate(text);

    // â”€â”€ Tray icon â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static Icon CreateDefaultIcon()
    {
        Theme.Refresh();
        return CreateLogoIcon(recording: false);
    }

    private static Icon CreateRecordingIcon()
    {
        Theme.Refresh();
        return CreateLogoIcon(recording: true);
    }

    private static Icon CreateLogoIcon(bool recording)
    {
        try
        {
            using var source = LoadLogoBitmap();
            var icon = CreateOwnedIcon(source);
            return recording ? OverlayRecordingDot(icon) : icon;
        }
        catch
        {
            return CreateFallbackIcon(recording);
        }
    }

    private static Bitmap LoadLogoBitmap()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/CyberSnap_square.png", UriKind.Absolute));
        if (info == null)
            throw new InvalidOperationException("CyberSnap logo resource was not found.");

        var decoder = BitmapDecoder.Create(info.Stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converted.CopyPixels(pixels, stride, 0);

        var bitmap = new Bitmap(frame.PixelWidth, frame.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < frame.PixelHeight; y++)
        {
            for (int x = 0; x < frame.PixelWidth; x++)
            {
                int i = y * stride + x * 4;
                bitmap.SetPixel(x, y, Color.FromArgb(pixels[i + 3], pixels[i + 2], pixels[i + 1], pixels[i]));
            }
        }

        return bitmap;
    }

    private static Icon OverlayRecordingDot(Icon baseIcon)
    {
        using var baseBmp = baseIcon.ToBitmap();
        var bmp = new Bitmap(baseBmp.Width, baseBmp.Height);
        using var g = Graphics.FromImage(bmp);
        g.DrawImage(baseBmp, 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int d = Math.Max(8, bmp.Width / 3);
        int x = bmp.Width - d - 1, y = bmp.Height - d - 1;
        using var white = new SolidBrush(Color.White);
        g.FillEllipse(white, x - 1, y - 1, d + 2, d + 2);
        using var red = new SolidBrush(Color.FromArgb(239, 68, 68));
        g.FillEllipse(red, x, y, d, d);
        var result = CreateOwnedIcon(bmp);
        baseIcon.Dispose();
        return result;
    }

    private static Icon CreateFallbackIcon(bool recording)
    {
        var strokeColor = Theme.IsDark ? Color.White : Color.Black;
        var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0, 0, 0, 0));
        using var pen = new Pen(strokeColor, 3f);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawLine(pen, 6, 4, 16, 16);
        g.DrawLine(pen, 26, 4, 16, 16);
        g.DrawLine(pen, 16, 16, 16, 28);
        if (recording)
        {
            using var halo = new SolidBrush(strokeColor);
            g.FillEllipse(halo, 20, 21, 12, 12);
            using var red = new SolidBrush(Color.FromArgb(239, 68, 68));
            g.FillEllipse(red, 21, 22, 10, 10);
        }
        return CreateOwnedIcon(bmp);
    }

    private void RefreshTrayIconTheme()
    {
        _defaultIcon?.Dispose();
        _recordingIcon?.Dispose();
        _defaultIcon = CreateDefaultIcon();
        _recordingIcon = null;
        _notifyIcon.Icon = _isShowingRecording ? (_recordingIcon = CreateRecordingIcon()) : _defaultIcon;
    }

    private static Icon CreateOwnedIcon(Bitmap bitmap)
    {
        var handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            Native.User32.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip = null;
        _menu?.Dispose();
        _notifyIcon.Dispose();
        _defaultIcon?.Dispose();
        _recordingIcon?.Dispose();
    }
}
