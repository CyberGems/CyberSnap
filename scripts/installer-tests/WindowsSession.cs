using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Collections.Generic;

public static class InstallerTestSession
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string reserved, desktop, title;
        public int x, y, xSize, ySize, xCountChars, yCountChars, fillAttribute, flags;
        public short showWindow, reserved2Size;
        public IntPtr reserved2, stdInput, stdOutput, stdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr process, thread; public int processId, threadId; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessWithLogonW(string user, string domain, string password,
        int logonFlags, string application, StringBuilder command, int creationFlags,
        IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectSecurity(IntPtr handle, ref int info, byte[] descriptor, uint size, out uint needed);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetUserObjectSecurity(IntPtr handle, ref int info, byte[] descriptor);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int size, out int needed);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);

    // Only grant the disposable account access to the disposable runner's desktop.
    public static void GrantDesktop(string sid)
    {
        Grant(GetProcessWindowStation(), sid, 0xF037F);
        Grant(GetThreadDesktop(GetCurrentThreadId()), sid, 0xF01FF);
    }
    private static void Grant(IntPtr handle, string sid, int access)
    {
        int info = 4;
        uint needed;
        GetUserObjectSecurity(handle, ref info, null, 0, out needed);
        var descriptor = new byte[needed];
        if (!GetUserObjectSecurity(handle, ref info, descriptor, needed, out needed))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var raw = new RawSecurityDescriptor(descriptor, 0);
        raw.DiscretionaryAcl.InsertAce(raw.DiscretionaryAcl.Count,
            new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, access, new SecurityIdentifier(sid), false, null));
        descriptor = new byte[raw.BinaryLength];
        raw.GetBinaryForm(descriptor, 0);
        if (!SetUserObjectSecurity(handle, ref info, descriptor))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static Process Start(string user, string password, string executable, string arguments, string directory)
    {
        var startup = new StartupInfo { cb = Marshal.SizeOf(typeof(StartupInfo)), desktop = @"winsta0\default" };
        ProcessInformation result;
        // LOGON_WITH_PROFILE, null environment: HKCU and special folders belong to this account.
        if (!CreateProcessWithLogonW(user, ".", password, 1, executable,
            new StringBuilder("\"" + executable + "\" " + arguments), 0x400, IntPtr.Zero, directory, ref startup, out result))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var process = Process.GetProcessById(result.processId);
            // Retain a process handle before it exits, including its eventual exit code.
            var retainedHandle = process.Handle;
            return process;
        }
        finally { CloseHandle(result.thread); CloseHandle(result.process); }
    }
    public static bool IsElevated(Process process)
    {
        IntPtr token;
        if (!OpenProcessToken(process.Handle, 8, out token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            int elevated, needed;
            if (!GetTokenInformation(token, 20, out elevated, sizeof(int), out needed))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return elevated != 0;
        }
        finally { CloseHandle(token); }
    }
    public static string Manifest(string path)
    {
        // LOAD_LIBRARY_AS_DATAFILE: inspect resources without running installer code.
        var module = LoadLibraryEx(path, IntPtr.Zero, 2);
        if (module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var resource = FindResource(module, new IntPtr(1), new IntPtr(24));
            if (resource == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[SizeofResource(module, resource)];
            Marshal.Copy(LockResource(LoadResource(module, resource)), bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
        }
        finally { FreeLibrary(module); }
    }
    public static string[] VisibleWindows(int processId)
    {
        var titles = new List<string>();
        EnumWindows((window, parameter) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if ((processId == 0 || owner == processId) && IsWindowVisible(window))
            {
                var title = new StringBuilder(512);
                GetWindowText(window, title, title.Capacity);
                titles.Add(title.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return titles.ToArray();
    }
    public static string[] DialogMessages(int processId)
    {
        var messages = new List<string>();
        EnumWindows((window, parameter) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if ((processId == 0 || owner == processId) && IsWindowVisible(window))
                EnumChildWindows(window, (child, ignored) => {
                    var type = new StringBuilder(128);
                    GetClassName(child, type, type.Capacity);
                    // Never read credential edit controls.
                    if (type.ToString() == "Static" || type.ToString() == "Button")
                    {
                        var text = new StringBuilder(2048);
                        GetWindowText(child, text, text.Capacity);
                        if (text.Length > 0) messages.Add(text.ToString());
                    }
                    return true;
                }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        return messages.ToArray();
    }
    public static bool ClickDialogButton(int processId, string label)
    {
        bool clicked = false;
        EnumWindows((window, parameter) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == processId && IsWindowVisible(window))
                EnumChildWindows(window, (child, ignored) => {
                    var type = new StringBuilder(128);
                    GetClassName(child, type, type.Capacity);
                    var text = new StringBuilder(256);
                    GetWindowText(child, text, text.Capacity);
                    if (type.ToString() == "Button" && IsWindowEnabled(child) &&
                        string.Equals(text.ToString().Replace("&", ""), label, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!PostMessage(child, 0xF5, IntPtr.Zero, IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        clicked = true;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);
            return !clicked;
        }, IntPtr.Zero);
        return clicked;
    }
}
