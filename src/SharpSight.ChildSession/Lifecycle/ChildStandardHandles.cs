using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SharpSight.ChildSession.Lifecycle;

internal static class ChildStandardHandles
{
    internal static void PreventInheritance()
    {
        // 后台持有器比调用端长寿，不能继承调用端的协议/输出管道写端并阻止读者收到EOF。
        foreach (var kind in new[] { -10, -11, -12 })
        {
            var handle = GetStdHandle(kind);
            if (handle == 0 || handle == -1) continue;
            if (!GetHandleInformation(handle, out var flags))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 6) continue;
                throw new Win32Exception(error, "无法核对标准句柄继承属性。");
            }
            if ((flags & 1) != 0 && !SetHandleInformation(handle, 1, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法隔离调用端标准句柄。");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int kind);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}
