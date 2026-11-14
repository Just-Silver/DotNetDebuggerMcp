using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SharpSight.ChildSession.Ipc;

public sealed record ChildPeer(int ProcessId, long StartedTicks, int SessionId, string Sid, string Image)
{
    public static ChildPeer Current => Capture(Environment.ProcessId);

    public static ChildPeer Capture(int processId)
    {
        using var process = Process.GetProcessById(processId);
        if (!OpenProcessToken(process.Handle, 8, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对管道对端账户。");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return new(processId, process.StartTime.ToUniversalTime().Ticks, process.SessionId,
                identity.User?.Value ?? throw new InvalidOperationException("对端SID不可用。"),
                process.MainModule?.FileName ?? throw new InvalidOperationException("对端映像不可用。"));
    }

    internal bool Matches(ChildPeer other) => ProcessId == other.ProcessId &&
        StartedTicks == other.StartedTicks && SessionId == other.SessionId && Sid == other.Sid &&
        string.Equals(Image, other.Image, StringComparison.OrdinalIgnoreCase);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
}
