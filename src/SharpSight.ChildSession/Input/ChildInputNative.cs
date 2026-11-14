using System.Runtime.InteropServices;
using System.Text;

namespace SharpSight.ChildSession.Input;

internal static class ChildInputNative
{
    internal const uint InputMouse = 0;
    internal const uint InputKeyboard = 1;

    internal const uint MouseMove = 0x0001;
    internal const uint MouseLeftDown = 0x0002;
    internal const uint MouseLeftUp = 0x0004;
    internal const uint MouseRightDown = 0x0008;
    internal const uint MouseRightUp = 0x0010;
    internal const uint MouseMiddleDown = 0x0020;
    internal const uint MouseMiddleUp = 0x0040;
    internal const uint MouseWheel = 0x0800;
    internal const uint MouseVirtualDesk = 0x4000;
    internal const uint MouseAbsolute = 0x8000;

    internal const uint KeyExtended = 0x0001;
    internal const uint KeyUp = 0x0002;
    internal const uint KeyUnicode = 0x0004;

    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    internal const int SwRestore = 9;
    internal const uint GaRoot = 2;

    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    static ChildInputNative()
    {
        // x64 的 dwExtraInfo 把 INPUT 对齐到 8 字节；布局漂移必须先失败，不能照旧注入。
        var expected = IntPtr.Size == 8 ? 40 : 28;
        if (Marshal.SizeOf<Input>() != expected ||
            Marshal.OffsetOf<Input>(nameof(Input.Data)).ToInt32() != (IntPtr.Size == 8 ? 8 : 4))
            throw new InvalidOperationException("INPUT 原生布局与当前进程位数不符，拒绝注入输入。");
    }

    internal static bool Submit(Input input)
        => SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;

    internal static nint ForegroundWindow() => GetForegroundWindow();

    internal static bool IsWindowAlive(nint window) => window != 0 && IsWindow(window);

    internal static bool ShowRestore(nint window) => ShowWindowAsync(window, SwRestore);

    internal static bool BringForeground(nint window) => SetForegroundWindow(window);

    internal static bool IsMinimized(nint window) => IsIconic(window);

    internal static int WindowOwner(nint window)
    {
        GetWindowThreadProcessId(window, out var pid);
        return (int)pid;
    }

    internal static nint RootOfPoint(int x, int y)
    {
        var hit = WindowFromPoint(new NativePoint { X = x, Y = y });
        return hit == 0 ? 0 : GetAncestor(hit, GaRoot);
    }

    internal static (int Left, int Top, int Width, int Height) VirtualScreenMetrics()
    {
        // 与窗口矩形同一 DPI 上下文读取，否则非 100% 缩放下会得到被虚拟化的逻辑像素。
        var previous = SetThreadDpiAwarenessContext(new(-4));
        try
        {
            return (GetSystemMetrics(SmXVirtualScreen), GetSystemMetrics(SmYVirtualScreen),
                GetSystemMetrics(SmCxVirtualScreen), GetSystemMetrics(SmCyVirtualScreen));
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    internal static bool KeyDown(ushort key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    internal static bool IsExtendedKey(ushort key) => key switch
    {
        // 扫描码带 0xE0 前缀的键；仅这些键在 VK 注入时才需要 KEYEVENTF_EXTENDEDKEY。
        0x03 or 0x0C or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5 => true,
        _ => false
    };

    internal static bool InputDesktopMatchesThread()
    {
        var input = OpenInputDesktop(0, false, DesktopReadObjects);
        if (input == 0) return false;
        try
        {
            var inputName = ObjectName(input);
            var threadName = ObjectName(GetThreadDesktop(GetCurrentThreadId()));
            return inputName is not null && threadName is not null &&
                string.Equals(inputName, threadName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // 只关闭本次 OpenInputDesktop 打开的句柄；GetThreadDesktop 返回的句柄归线程所有。
            CloseDesktop(input);
        }
    }

    internal static bool InWinSta0()
        => string.Equals(ObjectName(GetProcessWindowStation()), "WinSta0", StringComparison.OrdinalIgnoreCase);

    private static string? ObjectName(nint handle)
    {
        if (handle == 0) return null;
        var name = new StringBuilder(64);
        return GetUserObjectInformation(handle, UoiName, name, name.Capacity * 2, out _) ? name.ToString() : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int Dx;
        internal int Dy;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort Vk;
        internal ushort Scan;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern nint GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder value, int length, out int needed);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
