using System.ComponentModel;
using SharpSight.ChildSession.Connection;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildConnectionReadinessTests
{
    [Fact]
    public async Task 已连接但会话尚不可查询时继续等待并保留自有会话编号()
    {
        var root = Path.Combine(Path.GetTempPath(), "child-readiness-" + Guid.NewGuid().ToString("N"));
        var store = new ChildInstanceStore(root);
        try
        {
            var peer = ChildPeer.Current;
            var identity = new ChildIdentity("readiness", peer.Sid, peer.SessionId, 0, peer.ProcessId, peer.StartedTicks, 0, 0);
            await store.WriteAsync(new(identity, "", "", "", peer, null, "AwaitingAuthentication", "等待认证"),
                TestContext.Current.CancellationToken);
            var waiting = await ChildHolderRuntime.ObserveConnectedChildAsync(store, "readiness", () => 7,
                _ => throw new Win32Exception(2, "会话尚不可查询"), TestContext.Current.CancellationToken);
            Assert.Null(waiting);
            var state = await store.ReadAsync("readiness", TestContext.Current.CancellationToken);
            Assert.Equal(7, state.Identity.ChildSessionId);
            Assert.Equal("AwaitingAuthentication", state.State);
            Assert.Null(state.Worker);
            var ready = await ChildHolderRuntime.ObserveConnectedChildAsync(store, "readiness", () => 7,
                _ => true, TestContext.Current.CancellationToken);
            Assert.Equal(7, ready);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 会话编号变化不能被当成本实例的新目标()
    {
        var root = Path.Combine(Path.GetTempPath(), "child-readiness-" + Guid.NewGuid().ToString("N"));
        var store = new ChildInstanceStore(root);
        try
        {
            var peer = ChildPeer.Current;
            var identity = new ChildIdentity("readiness", peer.Sid, peer.SessionId, 7, peer.ProcessId, peer.StartedTicks, 0, 0);
            await store.WriteAsync(new(identity, "", "", "", peer, null, "AwaitingAuthentication", "等待认证"),
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ChildHolderRuntime.ObserveConnectedChildAsync(store, "readiness", () => 8, _ => true,
                    TestContext.Current.CancellationToken));
            Assert.Equal(7, (await store.ReadAsync("readiness", TestContext.Current.CancellationToken)).Identity.ChildSessionId);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
