using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using CyberSnap.Native;

namespace CyberSnap.Capture;

internal sealed class CaptureEscapeKeyHook : IDisposable
{
    private readonly Control _target;
    private readonly Action _onEscape;
    private readonly Func<int, bool>? _tryHandleKey;
    private readonly Func<bool>? _consumeTransportKeys;
    private readonly User32.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private int _posted;
    private int _spaceHeld;
    private int _enterHeld;

    private CaptureEscapeKeyHook(
        Control target,
        Action onEscape,
        Func<int, bool>? tryHandleKey,
        Func<bool>? consumeTransportKeys)
    {
        _target = target;
        _onEscape = onEscape;
        _tryHandleKey = tryHandleKey;
        _consumeTransportKeys = consumeTransportKeys;
        _proc = HookProc;
    }

    public static CaptureEscapeKeyHook? Install(Control target, Action onEscape)
        => Install(target, onEscape, tryHandleKey: null);

    public static CaptureEscapeKeyHook? Install(Control target, Action onEscape, Func<int, bool>? tryHandleKey)
        => Install(target, onEscape, tryHandleKey, consumeTransportKeys: null);

    public static CaptureEscapeKeyHook? Install(
        Control target,
        Action onEscape,
        Func<int, bool>? tryHandleKey,
        Func<bool>? consumeTransportKeys)
    {
        if (target.IsDisposed || !target.IsHandleCreated)
            return null;

        var hook = new CaptureEscapeKeyHook(target, onEscape, tryHandleKey, consumeTransportKeys);
        hook.Install();
        return hook._hook == IntPtr.Zero ? null : hook;
    }

    private void Install()
    {
        IntPtr moduleHandle = IntPtr.Zero;
        try
        {
            string? moduleName = Process.GetCurrentProcess().MainModule?.ModuleName;
            if (!string.IsNullOrWhiteSpace(moduleName))
                moduleHandle = Kernel32.GetModuleHandle(moduleName);
        }
        catch
        {
            moduleHandle = IntPtr.Zero;
        }

        _hook = User32.SetWindowsHookEx(User32.WH_KEYBOARD_LL, _proc, moduleHandle, 0);
    }

    private bool ShouldConsumeTransportKeys()
    {
        if (_tryHandleKey == null)
            return false;
        try
        {
            return _consumeTransportKeys?.Invoke() ?? true;
        }
        catch
        {
            return false;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool isDown = msg == User32.WM_KEYDOWN || msg == User32.WM_SYSKEYDOWN;
            bool isUp = msg == User32.WM_KEYUP || msg == User32.WM_SYSKEYUP;

            if (isDown || isUp)
            {
                int vkCode = Marshal.ReadInt32(lParam);
                if (isDown && vkCode == (int)User32.VK_ESCAPE)
                {
                    PostEscape();
                    return 1;
                }

                if (ShouldConsumeTransportKeys() &&
                    (vkCode == (int)User32.VK_SPACE || vkCode == (int)User32.VK_RETURN))
                {
                    if (isDown)
                    {
                        bool firstPress = vkCode == (int)User32.VK_SPACE
                            ? Interlocked.Exchange(ref _spaceHeld, 1) == 0
                            : Interlocked.Exchange(ref _enterHeld, 1) == 0;
                        if (firstPress)
                            PostHotkey(vkCode);
                    }
                    else
                    {
                        if (vkCode == (int)User32.VK_SPACE)
                            Volatile.Write(ref _spaceHeld, 0);
                        else
                            Volatile.Write(ref _enterHeld, 0);
                    }

                    // Swallow so the window under the capture overlay never sees the key.
                    // During recording this is true only while the bar/overlay is focused,
                    // so typing in other apps still works.
                    return 1;
                }
            }
        }

        return User32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void PostEscape()
    {
        if (_target.IsDisposed || _target.Disposing)
            return;

        if (Interlocked.Exchange(ref _posted, 1) == 1)
            return;

        try
        {
            _target.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!_target.IsDisposed && !_target.Disposing)
                        _onEscape();
                }
                finally
                {
                    Volatile.Write(ref _posted, 0);
                }
            }));
        }
        catch
        {
            Volatile.Write(ref _posted, 0);
        }
    }

    private void PostHotkey(int vkCode)
    {
        if (_tryHandleKey == null || _target.IsDisposed || _target.Disposing)
            return;

        try
        {
            _target.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!_target.IsDisposed && !_target.Disposing)
                        _tryHandleKey(vkCode);
                }
                catch
                {
                    // Hotkey handlers must not tear down the hook.
                }
            }));
        }
        catch
        {
            // Target may already be disposing.
        }
    }

    public void Dispose()
    {
        var hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
        if (hook != IntPtr.Zero)
            User32.UnhookWindowsHookEx(hook);
    }
}
