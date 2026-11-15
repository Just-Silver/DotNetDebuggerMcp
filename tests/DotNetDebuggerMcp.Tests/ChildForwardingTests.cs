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
        var root = TempRoot();
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
        var root = TempRoot();
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct);
            var forwarder = await CreateForwarderAsync(host, root, ct,
                ensure: _ => throw new InvalidOperationException("断连不应尝试还原连接窗。"));
            var reply = await forwarder.ForwardAsync(host.Request("ui_input", new { value = "不应写入" }), ct);
            Assert.Equal("not-executed", reply.Status);
            Assert.False(File.Exists(host.OutputPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 连接窗最小化时GUI请求先还原再执行()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TempRoot();
        var forwardedBeforeRestore = false;
        var restoreChecked = false;
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct);
            var forwarder = await CreateForwarderAsync(host, root, ct, ready: true,
                ensure: _ =>
                {
                    restoreChecked = true;
                    forwardedBeforeRestore = File.Exists(host.OutputPath);
                    return Task.FromResult(true);
                });
            var reply = await forwarder.ForwardAsync(host.Request("ui_input", new { value = "还原后写入" }), ct);
            Assert.Equal("completed", reply.Status);
            Assert.True(restoreChecked);
            Assert.False(forwardedBeforeRestore);
            Assert.Equal("还原后写入", await File.ReadAllTextAsync(host.OutputPath, ct));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 连接窗无法还原时GUI请求不执行且无业务副作用()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TempRoot();
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct);
            var forwarder = await CreateForwarderAsync(host, root, ct, ready: true,
                ensure: _ => Task.FromResult(false));
            var reply = await forwarder.ForwardAsync(host.Request("ui_input", new { value = "不应写入" }), ct);
            Assert.Equal("not-executed", reply.Status);
            Assert.False(File.Exists(host.OutputPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 控制查询与进程启动不依赖连接窗还原()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TempRoot();
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct);
            var forwarder = await CreateForwarderAsync(host, root, ct, ready: true,
                ensure: _ => Task.FromResult(false));
            foreach (var action in new[] { "list", "status", "output", "cancel" })
            {
                var reply = await forwarder.ForwardAsync(
                    host.Request("child_process", new { action, taskId = "task-1", value = "process-" + action }), ct);
                Assert.Equal("completed", reply.Status);
            }
            var session = await forwarder.ForwardAsync(host.Request("child_session", new { action = "requests" }), ct);
            Assert.Equal("completed", session.Status);
            var start = await forwarder.ForwardAsync(host.Request("child_process", new { action = "start", value = "process-start" }), ct);
            Assert.Equal("completed", start.Status);
            Assert.Equal("process-start", await File.ReadAllTextAsync(host.OutputPath, ct));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 连接窗还原异常时GUI请求不执行且保留原因()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = TempRoot();
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct);
            var forwarder = await CreateForwarderAsync(host, root, ct, ready: true,
                ensure: _ => throw new InvalidOperationException("窗口句柄已销毁"));
            var reply = await forwarder.ForwardAsync(host.Request("ui_input", new { value = "不应写入" }), ct);
            Assert.Equal("not-executed", reply.Status);
            Assert.Contains("窗口句柄已销毁", reply.Message);
            Assert.False(File.Exists(host.OutputPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string TempRoot()
        => Path.Combine(Path.GetTempPath(), "child-forward-" + Guid.NewGuid().ToString("N"));

    private static async Task<ChildForwarder> CreateForwarderAsync(ChildTestHost host, string root, CancellationToken ct,
        bool ready = false, Func<CancellationToken, Task<bool>>? ensure = null)
    {
        var store = new ChildInstanceStore(root);
        var peer = ChildPeer.Current;
        await store.WriteAsync(new ChildBootstrap(
            new ChildIdentity("test-instance", peer.Sid, peer.SessionId, 7,
                peer.ProcessId, peer.StartedTicks, peer.ProcessId, peer.StartedTicks),
            "test-holder", host.PipeName, ChildTestHost.Secret, peer, peer,
            ready ? "Ready" : "Disconnected", ready ? "子会话worker已就绪。" : "连接已断开"), ct);
        return new ChildForwarder(store, _ => Task.FromResult(ready), () => ready ? 7 : null, _ => ready,
            ensure ?? (_ => Task.FromResult(true)));
    }
}
