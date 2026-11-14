using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SharpSight.ChildSession.Lifecycle;

internal static class WtsNative
{
    internal static bool Enabled()
    {
        if (!WTSIsChildSessionsEnabled(out var enabled))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法查询子会话开关。");
        return enabled;
    }

    internal static int? ChildId()
    {
        if (WTSGetChildSessionId(out var id)) return id == uint.MaxValue ? null : checked((int)id);
        var error = Marshal.GetLastWin32Error();
        if (error == 1168) return null;
        throw new Win32Exception(error, "无法核对当前子会话。");
    }

    internal static bool Active(int session)
    {
        if (!WTSQuerySessionInformation(0, session, 8, out var buffer, out var length))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取会话连接状态。");
        try { return length >= 4 && Marshal.ReadInt32(buffer) == 0; }
        finally { WTSFreeMemory(buffer); }
    }

    internal static void SetEnabled(bool enabled)
    {
        if (!WTSEnableChildSessions(enabled))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "修改子会话开关失败，需要明确管理员授权。");
    }

    internal static void Logoff(int session)
    {
        if (!WTSLogoffSession(0, session, false))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "请求注销子会话失败。");
    }

    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSIsChildSessionsEnabled(out bool enabled);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSEnableChildSessions(bool enabled);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSGetChildSessionId(out uint id);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSLogoffSession(nint server, int id, bool wait);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQuerySessionInformation(
        nint server, int id, int infoClass, out nint buffer, out int length);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
}
