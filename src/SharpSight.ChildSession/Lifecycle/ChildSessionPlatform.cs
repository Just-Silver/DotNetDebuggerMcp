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
        var info = command.StartInfo("--child-internal", "holder", root, instance);
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException("连接持有器未启动。");
        var peer = ChildPeer.Capture(process.Id);
        _ = DrainAndDisposeAsync(process, Path.Combine(root, instance, "holder.log"));
        return peer;
    }

    public bool IsAlive(ChildPeer peer)
    {
        try { return peer.Matches(ChildPeer.Capture(peer.ProcessId)); }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public void RequestLogoff(int childSessionId) => WtsNative.Logoff(childSessionId);
    public void SetEnabled(bool enabled) => WtsNative.SetEnabled(enabled);

    public void CloseHolder(ChildPeer holder)
    {
        if (!IsAlive(holder)) return;
        using var process = Process.GetProcessById(holder.ProcessId);
        process.CloseMainWindow();
    }

    private static async Task DrainAndDisposeAsync(Process process, string path)
    {
        try
        {
            await using var log = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var gate = new SemaphoreSlim(1);
            await Task.WhenAll(DrainAsync(process.StandardOutput, log, gate), DrainAsync(process.StandardError, log, gate));
            await process.WaitForExitAsync();
        }
        catch (Exception ex) { Console.Error.WriteLine($"持有器输出读取失败：{ex.Message}"); }
        finally { process.Dispose(); }
    }

    private static async Task DrainAsync(StreamReader reader, FileStream file, SemaphoreSlim gate)
    {
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            await gate.WaitAsync();
            try
            {
                if (file.Length < 65536)
                    await file.WriteAsync(System.Text.Encoding.UTF8.GetBytes(buffer.AsSpan(0, count).ToString()));
            }
            finally { gate.Release(); }
        }
    }
}
