using System.Diagnostics;
using SharpSight.ChildSession.Ipc;

namespace SharpSight.ChildSession.Lifecycle;

public sealed class ChildSessionPlatform(ChildHostCommand command) : IChildSessionPlatform
{
    public ChildPeer Current => ChildPeer.Current;

    public ChildSystemStatus Probe() => new(WtsNative.Enabled(), Current.SessionId, WtsNative.ChildId(),
        Type.GetTypeFromCLSID(new Guid("A0C63C30-F08D-4AB4-907C-34905D770C7D")) is not null);

    public ChildPeer StartHolder(string root, string instance)
    {
        ChildStandardHandles.PreventInheritance();
        // 不重定向 stdio：持有器写实例目录日志，宿主退出或短命 CLI 退出都不会留下无人读取的管道。
        var info = command.StartInfo("--child-internal", "holder", root, instance);
        var process = Process.Start(info) ?? throw new InvalidOperationException("连接持有器未启动。");
        var peer = ChildPeer.Capture(process.Id);
        _ = WaitAndDisposeAsync(process);
        return peer;
    }

    public bool IsAlive(ChildPeer peer)
    {
        try { return peer.Matches(ChildPeer.Capture(peer.ProcessId)); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public bool? IsActive(int sessionId)
    {
        try { return WtsNative.Active(sessionId); }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public void RequestLogoff(int childSessionId) => WtsNative.Logoff(childSessionId);
    public void SetEnabled(bool enabled) => WtsNative.SetEnabled(enabled);

    public void CloseHolder(ChildPeer holder)
    {
        if (!IsAlive(holder)) return;
        using var process = Process.GetProcessById(holder.ProcessId);
        process.CloseMainWindow();
    }

    private static async Task WaitAndDisposeAsync(Process process)
    {
        try { await process.WaitForExitAsync(); }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
}
