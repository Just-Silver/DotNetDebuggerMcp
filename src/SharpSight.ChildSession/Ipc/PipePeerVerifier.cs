using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpSight.ChildSession.Ipc;

internal static class PipePeerVerifier
{
    internal static ChildPeer Read(PipeStream pipe, bool serverSide)
    {
        var succeeded = serverSide
            ? GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)
            : GetNamedPipeServerProcessId(pipe.SafePipeHandle, out pid);
        if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法核对管道进程。");
        return ChildPeer.Capture(checked((int)pid));
    }

    internal static void Require(ChildPeer expected, ChildPeer actual)
    {
        if (!expected.Matches(actual)) throw new InvalidOperationException("管道对端身份不符，拒绝操作。");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
