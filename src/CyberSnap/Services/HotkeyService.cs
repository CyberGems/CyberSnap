using System.Windows.Interop;
using CyberSnap.Native;

namespace CyberSnap.Services;

public sealed class HotkeyService : IDisposable
{
    // Hotkey ID allocation:
    //   9001, 9007-9011  — core capture / tool hotkeys
    //   9002         — screen recorder (MP4)
    //   9003         — screen recorder (GIF)
    //   (9004-9006 retired: overlay utility hotkeys + legacy GIF slot)
    //   9012       — standalone ruler
    //   9013+      — standalone tools; 9016 — repeat last capture area
    private const int HOTKEY_CAPTURE = 9001;
    private const int HOTKEY_RECORD = 9002;
    private const int HOTKEY_RECORD_GIF = 9003;
    private const int HOTKEY_FULLSCREEN = 9007;
    private const int HOTKEY_ACTIVE_WINDOW = 9008;
    private const int HOTKEY_SCROLL_CAPTURE = 9009;
    private const int HOTKEY_CENTER = 9011;
    private const int HOTKEY_STANDALONE_RULER = 9012;
    private const int HOTKEY_STANDALONE_COLOR_PICKER = 9013;
    private const int HOTKEY_STANDALONE_OCR = 9014;
    private const int HOTKEY_STANDALONE_SCAN = 9015;
    private const int HOTKEY_REPEAT_LAST_AREA = 9016;

    private bool _captureRegistered;
    private bool _recordRegistered;
    private bool _recordGifRegistered;
    private bool _fullscreenRegistered;
    private bool _activeWindowRegistered;
    private bool _scrollCaptureRegistered;
    private bool _centerRegistered;
    private bool _standaloneRulerRegistered;
    private bool _standaloneColorPickerRegistered;
    private bool _standaloneOcrRegistered;
    private bool _standaloneScanRegistered;
    private bool _repeatLastAreaRegistered;
    private bool _registered;

    public event Action? HotkeyPressed;
    public event Action? RecordHotkeyPressed;
    public event Action? RecordGifHotkeyPressed;
    public event Action? FullscreenHotkeyPressed;
    public event Action? ActiveWindowHotkeyPressed;
    public event Action? ScrollCaptureHotkeyPressed;
    public event Action? CenterHotkeyPressed;
    public event Action? StandaloneRulerHotkeyPressed;
    public event Action? StandaloneColorPickerHotkeyPressed;
    public event Action? StandaloneOcrHotkeyPressed;
    public event Action? StandaloneScanHotkeyPressed;
    public event Action? RepeatLastAreaHotkeyPressed;

    private void EnsureMessageHook()
    {
        if (_registered)
            return;

        ComponentDispatcher.ThreadPreprocessMessage += OnMsg;
        _registered = true;
    }

    private bool RegisterHotkey(ref bool registeredFlag, int id, uint modifiers, uint key)
    {
        EnsureMessageHook();

        if (registeredFlag)
        {
            User32.UnregisterHotKey(IntPtr.Zero, id);
            registeredFlag = false;
        }

        if (key == 0 || IsUnsafeModifierlessHotkey(modifiers, key))
            return true;

        registeredFlag = User32.RegisterHotKey(
            IntPtr.Zero, id, modifiers | User32.MOD_NOREPEAT, key);
        return registeredFlag;
    }

    private static bool IsUnsafeModifierlessHotkey(uint modifiers, uint key) =>
        modifiers == 0 && key != User32.VK_SNAPSHOT;

    public void UnregisterAll()
    {
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_CAPTURE);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_RECORD);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_RECORD_GIF);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_FULLSCREEN);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_ACTIVE_WINDOW);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_SCROLL_CAPTURE);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_CENTER);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_RULER);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_COLOR_PICKER);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_OCR);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_SCAN);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_REPEAT_LAST_AREA);
        _captureRegistered = false;
        _recordRegistered = false;
        _recordGifRegistered = false;
        _fullscreenRegistered = false;
        _activeWindowRegistered = false;
        _scrollCaptureRegistered = false;
        _centerRegistered = false;
        _standaloneRulerRegistered = false;
        _standaloneColorPickerRegistered = false;
        _standaloneOcrRegistered = false;
        _standaloneScanRegistered = false;
        _repeatLastAreaRegistered = false;
    }

    public bool Register(uint modifiers, uint key) => RegisterHotkey(ref _captureRegistered, HOTKEY_CAPTURE, modifiers, key);
    public bool RegisterRecord(uint modifiers, uint key) => RegisterHotkey(ref _recordRegistered, HOTKEY_RECORD, modifiers, key);
    public bool RegisterRecordGif(uint modifiers, uint key) => RegisterHotkey(ref _recordGifRegistered, HOTKEY_RECORD_GIF, modifiers, key);
    public bool RegisterFullscreen(uint modifiers, uint key) => RegisterHotkey(ref _fullscreenRegistered, HOTKEY_FULLSCREEN, modifiers, key);
    public bool RegisterActiveWindow(uint modifiers, uint key) => RegisterHotkey(ref _activeWindowRegistered, HOTKEY_ACTIVE_WINDOW, modifiers, key);
    public bool RegisterScrollCapture(uint modifiers, uint key) => RegisterHotkey(ref _scrollCaptureRegistered, HOTKEY_SCROLL_CAPTURE, modifiers, key);
    public bool RegisterCenter(uint modifiers, uint key) => RegisterHotkey(ref _centerRegistered, HOTKEY_CENTER, modifiers, key);
    public bool RegisterStandaloneRuler(uint modifiers, uint key) => RegisterHotkey(ref _standaloneRulerRegistered, HOTKEY_STANDALONE_RULER, modifiers, key);
    public bool RegisterStandaloneColorPicker(uint modifiers, uint key) => RegisterHotkey(ref _standaloneColorPickerRegistered, HOTKEY_STANDALONE_COLOR_PICKER, modifiers, key);
    public bool RegisterStandaloneOcr(uint modifiers, uint key) => RegisterHotkey(ref _standaloneOcrRegistered, HOTKEY_STANDALONE_OCR, modifiers, key);
    public bool RegisterStandaloneScan(uint modifiers, uint key) => RegisterHotkey(ref _standaloneScanRegistered, HOTKEY_STANDALONE_SCAN, modifiers, key);
    public bool RegisterRepeatLastArea(uint modifiers, uint key) => RegisterHotkey(ref _repeatLastAreaRegistered, HOTKEY_REPEAT_LAST_AREA, modifiers, key);

    public void Unregister()
    {
        if (_captureRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_CAPTURE); _captureRegistered = false; }
        if (_recordRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_RECORD); _recordRegistered = false; }
        if (_recordGifRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_RECORD_GIF); _recordGifRegistered = false; }
        if (_fullscreenRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_FULLSCREEN); _fullscreenRegistered = false; }
        if (_activeWindowRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_ACTIVE_WINDOW); _activeWindowRegistered = false; }
        if (_scrollCaptureRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_SCROLL_CAPTURE); _scrollCaptureRegistered = false; }
        if (_centerRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_CENTER); _centerRegistered = false; }
        if (_standaloneRulerRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_RULER); _standaloneRulerRegistered = false; }
        if (_standaloneColorPickerRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_COLOR_PICKER); _standaloneColorPickerRegistered = false; }
        if (_standaloneOcrRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_OCR); _standaloneOcrRegistered = false; }
        if (_standaloneScanRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_SCAN); _standaloneScanRegistered = false; }
        if (_repeatLastAreaRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_REPEAT_LAST_AREA); _repeatLastAreaRegistered = false; }
        if (_registered)
        {
            ComponentDispatcher.ThreadPreprocessMessage -= OnMsg;
            _registered = false;
        }
    }

    private void OnMsg(ref MSG msg, ref bool handled)
    {
        if (msg.message != User32.WM_HOTKEY) return;
        int id = (int)msg.wParam;
        if (id == HOTKEY_CAPTURE) { InvokeHandlersSafely(HotkeyPressed, "hotkey.capture"); handled = true; }
        else if (id == HOTKEY_RECORD) { InvokeHandlersSafely(RecordHotkeyPressed, "hotkey.record"); handled = true; }
        else if (id == HOTKEY_RECORD_GIF) { InvokeHandlersSafely(RecordGifHotkeyPressed, "hotkey.record-gif"); handled = true; }
        else if (id == HOTKEY_FULLSCREEN) { InvokeHandlersSafely(FullscreenHotkeyPressed, "hotkey.fullscreen"); handled = true; }
        else if (id == HOTKEY_ACTIVE_WINDOW) { InvokeHandlersSafely(ActiveWindowHotkeyPressed, "hotkey.active-window"); handled = true; }
        else if (id == HOTKEY_SCROLL_CAPTURE) { InvokeHandlersSafely(ScrollCaptureHotkeyPressed, "hotkey.scroll-capture"); handled = true; }
        else if (id == HOTKEY_CENTER) { InvokeHandlersSafely(CenterHotkeyPressed, "hotkey.center"); handled = true; }
        else if (id == HOTKEY_STANDALONE_RULER) { InvokeHandlersSafely(StandaloneRulerHotkeyPressed, "hotkey.standalone-ruler"); handled = true; }
        else if (id == HOTKEY_STANDALONE_COLOR_PICKER) { InvokeHandlersSafely(StandaloneColorPickerHotkeyPressed, "hotkey.standalone-colorpicker"); handled = true; }
        else if (id == HOTKEY_STANDALONE_OCR) { InvokeHandlersSafely(StandaloneOcrHotkeyPressed, "hotkey.standalone-ocr"); handled = true; }
        else if (id == HOTKEY_STANDALONE_SCAN) { InvokeHandlersSafely(StandaloneScanHotkeyPressed, "hotkey.standalone-scan"); handled = true; }
        else if (id == HOTKEY_REPEAT_LAST_AREA) { InvokeHandlersSafely(RepeatLastAreaHotkeyPressed, "hotkey.repeat-last-area"); handled = true; }
    }

    private static void InvokeHandlersSafely(Action? handlers, string context)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { AppDiagnostics.LogError(context, ex); }
        }
    }

    public void Dispose() => Unregister();
}
