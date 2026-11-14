using SharpSight.ChildSession.Connection;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildForwardingTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("list")]
    [InlineData("status")]
    [InlineData("output")]
    public async Task 断连仍可向实例投送进程控制请求(string action)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "child-forward-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct);
            var forwarder = await CreateForwarderAsync(host, root, ct);
            var expected = "process-" + action;
            var reply = await forwarder.ForwardAsync(
                host.Request("child_process", new { action, taskId = "task-1", value = expected }), ct);
            Assert.Equal("completed", reply.Status);
            Assert.Equal(expected, await File.ReadAllTextAsync(host.OutputPath, ct));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 断连时GUI请求被拒绝且不写入()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "child-forward-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct);
            var forwarder = await CreateForwarderAsync(host, root, ct);
            var reply = await forwarder.ForwardAsync(host.Request("ui_input", new { value = "不应写入" }), ct);
            Assert.Equal("not-executed", reply.Status);
            Assert.False(File.Exists(host.OutputPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<ChildForwarder> CreateForwarderAsync(ChildTestHost host, string root, CancellationToken ct)
    {
        var store = new ChildInstanceStore(root);
        var peer = ChildPeer.Current;
        await store.WriteAsync(new ChildBootstrap(
            new ChildIdentity("test-instance", peer.Sid, peer.SessionId, 7,
                peer.ProcessId, peer.StartedTicks, peer.ProcessId, peer.StartedTicks),
            "test-holder", host.PipeName, ChildTestHost.Secret, peer, peer, "Disconnected", "连接已断开"), ct);
        return new ChildForwarder(store, _ => Task.FromResult(false), () => null, _ => false);
    }
}
