using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NativeInputSpike;

internal static class Native
{
    [DllImport("wtsapi32.dll", SetLastError = true)] internal static extern bool WTSIsChildSessionsEnabled(out bool enabled);
    [DllImport("wtsapi32.dll", SetLastError = true)] internal static extern bool WTSEnableChildSessions(bool enabled);
    [DllImport("wtsapi32.dll", SetLastError = true)] internal static extern bool WTSGetChildSessionId(out uint id);
    [DllImport("wtsapi32.dll", SetLastError = true)] internal static extern bool WTSLogoffSession(nint server, int id, bool wait);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public nuint ExtraInfo;
    }

    internal static object Probe()
    {
        var ok = WTSIsChildSessionsEnabled(out var enabled);
        var error = Marshal.GetLastWin32Error();
        var hasChild = WTSGetChildSessionId(out var child);
        return new { querySucceeded = ok, enabled, error, hasChild, childId = child,
            sessionId = Process.GetCurrentProcess().SessionId, desktop = DesktopName(GetThreadDesktop(GetCurrentThreadId())) };
    }

    internal static string DesktopName(nint handle)
    {
        var name = new StringBuilder(256);
        return GetUserObjectInformation(handle, 2, name, name.Capacity * 2, out _) ? name.ToString() : "unavailable";
    }

    internal static object Observe()
    {
        GetCursorPos(out var point);
        return new { at = DateTimeOffset.UtcNow, foreground = GetForegroundWindow().ToInt64(), cursor = new { point.X, point.Y } };
    }

    internal static void Post(nint hwnd, uint message, nuint key = 0, int x = 0, int y = 0)
    {
        if (!PostMessage(hwnd, message, key, (nint)((y << 16) | (x & 0xffff))))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
