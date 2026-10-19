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
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int size, out int needed);
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);

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
        try { return Process.GetProcessById(result.processId); }
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
    public static string[] VisibleWindows(int processId)
    {
        var titles = new List<string>();
        EnumWindows((window, parameter) => {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner == processId && IsWindowVisible(window))
            {
                var title = new StringBuilder(512);
                GetWindowText(window, title, title.Capacity);
                titles.Add(title.ToString());
            }
            return true;
        }, IntPtr.Zero);
        return titles.ToArray();
    }
}
