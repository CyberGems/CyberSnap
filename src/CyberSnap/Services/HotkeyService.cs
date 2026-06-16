using System.Windows.Interop;
using CyberSnap.Native;

namespace CyberSnap.Services;

public sealed class HotkeyService : IDisposable
{
    // Hotkey ID allocation:
    //   9001–9011  — core capture / tool hotkeys
    //   9012       — standalone ruler
    //   9013+      — RESERVED for future standalone tools (color picker, protractor, etc.)
    private const int HOTKEY_CAPTURE = 9001;
    private const int HOTKEY_OCR = 9002;
    private const int HOTKEY_PICKER = 9003;
    private const int HOTKEY_SCAN = 9004;
    private const int HOTKEY_RULER = 9005;
    private const int HOTKEY_GIF = 9006;
    private const int HOTKEY_FULLSCREEN = 9007;
    private const int HOTKEY_ACTIVE_WINDOW = 9008;
    private const int HOTKEY_SCROLL_CAPTURE = 9009;
    private const int HOTKEY_CENTER = 9011;
    private const int HOTKEY_STANDALONE_RULER = 9012;
    private const int HOTKEY_STANDALONE_COLOR_PICKER = 9013;
    private const int HOTKEY_STANDALONE_OCR = 9014;

    private bool _captureRegistered;
    private bool _ocrRegistered;
    private bool _pickerRegistered;
    private bool _scanRegistered;
    private bool _rulerRegistered;
    private bool _gifRegistered;
    private bool _fullscreenRegistered;
    private bool _activeWindowRegistered;
    private bool _scrollCaptureRegistered;
    private bool _centerRegistered;
    private bool _standaloneRulerRegistered;
    private bool _standaloneColorPickerRegistered;
    private bool _standaloneOcrRegistered;
    private bool _registered;

    public event Action? HotkeyPressed;
    public event Action? OcrHotkeyPressed;
    public event Action? PickerHotkeyPressed;
    public event Action? ScanHotkeyPressed;
    public event Action? RulerHotkeyPressed;
    public event Action? GifHotkeyPressed;
    public event Action? FullscreenHotkeyPressed;
    public event Action? ActiveWindowHotkeyPressed;
    public event Action? ScrollCaptureHotkeyPressed;
    public event Action? CenterHotkeyPressed;
    public event Action? StandaloneRulerHotkeyPressed;
    public event Action? StandaloneColorPickerHotkeyPressed;
    public event Action? StandaloneOcrHotkeyPressed;

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
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_OCR);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_PICKER);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_SCAN);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_RULER);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_GIF);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_FULLSCREEN);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_ACTIVE_WINDOW);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_SCROLL_CAPTURE);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_CENTER);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_RULER);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_COLOR_PICKER);
        User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_OCR);
        _captureRegistered = false;
        _ocrRegistered = false;
        _pickerRegistered = false;
        _scanRegistered = false;
        _rulerRegistered = false;
        _gifRegistered = false;
        _fullscreenRegistered = false;
        _activeWindowRegistered = false;
        _scrollCaptureRegistered = false;
        _centerRegistered = false;
        _standaloneRulerRegistered = false;
        _standaloneColorPickerRegistered = false;
        _standaloneOcrRegistered = false;
    }

    public bool Register(uint modifiers, uint key) => RegisterHotkey(ref _captureRegistered, HOTKEY_CAPTURE, modifiers, key);
    public bool RegisterOcr(uint modifiers, uint key) => RegisterHotkey(ref _ocrRegistered, HOTKEY_OCR, modifiers, key);
    public bool RegisterPicker(uint modifiers, uint key) => RegisterHotkey(ref _pickerRegistered, HOTKEY_PICKER, modifiers, key);
    public bool RegisterScan(uint modifiers, uint key) => RegisterHotkey(ref _scanRegistered, HOTKEY_SCAN, modifiers, key);
    public bool RegisterRuler(uint modifiers, uint key) => RegisterHotkey(ref _rulerRegistered, HOTKEY_RULER, modifiers, key);
    public bool RegisterGif(uint modifiers, uint key) => RegisterHotkey(ref _gifRegistered, HOTKEY_GIF, modifiers, key);
    public bool RegisterFullscreen(uint modifiers, uint key) => RegisterHotkey(ref _fullscreenRegistered, HOTKEY_FULLSCREEN, modifiers, key);
    public bool RegisterActiveWindow(uint modifiers, uint key) => RegisterHotkey(ref _activeWindowRegistered, HOTKEY_ACTIVE_WINDOW, modifiers, key);
    public bool RegisterScrollCapture(uint modifiers, uint key) => RegisterHotkey(ref _scrollCaptureRegistered, HOTKEY_SCROLL_CAPTURE, modifiers, key);
    public bool RegisterCenter(uint modifiers, uint key) => RegisterHotkey(ref _centerRegistered, HOTKEY_CENTER, modifiers, key);
    public bool RegisterStandaloneRuler(uint modifiers, uint key) => RegisterHotkey(ref _standaloneRulerRegistered, HOTKEY_STANDALONE_RULER, modifiers, key);
    public bool RegisterStandaloneColorPicker(uint modifiers, uint key) => RegisterHotkey(ref _standaloneColorPickerRegistered, HOTKEY_STANDALONE_COLOR_PICKER, modifiers, key);
    public bool RegisterStandaloneOcr(uint modifiers, uint key) => RegisterHotkey(ref _standaloneOcrRegistered, HOTKEY_STANDALONE_OCR, modifiers, key);

    public void Unregister()
    {
        if (_captureRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_CAPTURE); _captureRegistered = false; }
        if (_ocrRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_OCR); _ocrRegistered = false; }
        if (_pickerRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_PICKER); _pickerRegistered = false; }
        if (_scanRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_SCAN); _scanRegistered = false; }
        if (_rulerRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_RULER); _rulerRegistered = false; }
        if (_gifRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_GIF); _gifRegistered = false; }
        if (_fullscreenRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_FULLSCREEN); _fullscreenRegistered = false; }
        if (_activeWindowRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_ACTIVE_WINDOW); _activeWindowRegistered = false; }
        if (_scrollCaptureRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_SCROLL_CAPTURE); _scrollCaptureRegistered = false; }
        if (_centerRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_CENTER); _centerRegistered = false; }
        if (_standaloneRulerRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_RULER); _standaloneRulerRegistered = false; }
        if (_standaloneColorPickerRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_COLOR_PICKER); _standaloneColorPickerRegistered = false; }
        if (_standaloneOcrRegistered) { User32.UnregisterHotKey(IntPtr.Zero, HOTKEY_STANDALONE_OCR); _standaloneOcrRegistered = false; }
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
        else if (id == HOTKEY_OCR) { InvokeHandlersSafely(OcrHotkeyPressed, "hotkey.ocr"); handled = true; }
        else if (id == HOTKEY_PICKER) { InvokeHandlersSafely(PickerHotkeyPressed, "hotkey.picker"); handled = true; }
        else if (id == HOTKEY_SCAN) { InvokeHandlersSafely(ScanHotkeyPressed, "hotkey.scan"); handled = true; }
        else if (id == HOTKEY_RULER) { InvokeHandlersSafely(RulerHotkeyPressed, "hotkey.ruler"); handled = true; }
        else if (id == HOTKEY_GIF) { InvokeHandlersSafely(GifHotkeyPressed, "hotkey.gif"); handled = true; }
        else if (id == HOTKEY_FULLSCREEN) { InvokeHandlersSafely(FullscreenHotkeyPressed, "hotkey.fullscreen"); handled = true; }
        else if (id == HOTKEY_ACTIVE_WINDOW) { InvokeHandlersSafely(ActiveWindowHotkeyPressed, "hotkey.active-window"); handled = true; }
        else if (id == HOTKEY_SCROLL_CAPTURE) { InvokeHandlersSafely(ScrollCaptureHotkeyPressed, "hotkey.scroll-capture"); handled = true; }
        else if (id == HOTKEY_CENTER) { InvokeHandlersSafely(CenterHotkeyPressed, "hotkey.center"); handled = true; }
        else if (id == HOTKEY_STANDALONE_RULER) { InvokeHandlersSafely(StandaloneRulerHotkeyPressed, "hotkey.standalone-ruler"); handled = true; }
        else if (id == HOTKEY_STANDALONE_COLOR_PICKER) { InvokeHandlersSafely(StandaloneColorPickerHotkeyPressed, "hotkey.standalone-colorpicker"); handled = true; }
        else if (id == HOTKEY_STANDALONE_OCR) { InvokeHandlersSafely(StandaloneOcrHotkeyPressed, "hotkey.standalone-ocr"); handled = true; }
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
