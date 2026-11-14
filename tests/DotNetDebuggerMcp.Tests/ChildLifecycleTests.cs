using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildLifecycleTests
{
    [Fact]
    public async Task 并发创建只得到一个实例()
    {
        using var setup = new Setup();
        var calls = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => setup.Call("start")));
        Assert.All(calls, c => Assert.Equal(calls[0].Instance, c.Instance));
        Assert.NotEmpty(calls[0].Instance);
        Assert.Equal("Starting", calls[0].Data.GetProperty("state").GetString());
    }

    [Fact]
    public async Task 外部子会话不能被接管或注销()
    {
        using var setup = new Setup();
        setup.Platform.ChildId = 7;
        var start = await setup.Call("start");
        var stop = await setup.Call("stop", "foreign");
        Assert.Equal("not-executed", start.Status);
        Assert.Equal("not-executed", stop.Status);
        Assert.Equal(7, setup.Platform.ChildId);
    }

    [Fact]
    public async Task worker已退出仍可按有效持有器所有权停止()
    {
        using var setup = new Setup();
        var start = await setup.Call("start");
        var state = await setup.Store.ReadAsync(start.Instance, TestContext.Current.CancellationToken);
        setup.Platform.ChildId = 7;
        await setup.Store.WriteAsync(state with
        {
            Identity = state.Identity with { ChildSessionId = 7, WorkerPid = 999 },
            State = "Faulted", Reason = "worker已退出"
        }, TestContext.Current.CancellationToken);
        var stopped = await setup.Call("stop", start.Instance);
        Assert.Equal("Stopped", stopped.Data.GetProperty("state").GetString());
        Assert.Null(setup.Platform.ChildId);
    }

    [Fact]
    public async Task 存在任何child或建立中的实例时禁用被拒绝()
    {
        using var setup = new Setup();
        setup.Platform.ChildId = 7;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Coordinator.ChangeSettingAsync(false, TestContext.Current.CancellationToken));
        Assert.True(setup.Platform.Enabled);
        setup.Platform.ChildId = null;
        await setup.Call("start");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Coordinator.ChangeSettingAsync(false, TestContext.Current.CancellationToken));
        Assert.True(setup.Platform.Enabled);
    }

    private sealed class Setup : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "child-life-" + Guid.NewGuid().ToString("N"));
        internal TestChildPlatform Platform { get; } = new();
        internal ChildInstanceStore Store { get; }
        internal ChildCoordinator Coordinator { get; }
        internal Setup()
        {
            Store = new(_root);
            Coordinator = new(Store, Platform);
        }
        internal Task<ChildReply> Call(string action, string instance = "")
            => Coordinator.HandleAsync(new(instance, Guid.NewGuid().ToString("N"), "child_session",
                JsonSerializer.SerializeToElement(new { action }), DateTimeOffset.UtcNow.AddSeconds(5)),
                TestContext.Current.CancellationToken);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    internal sealed class TestChildPlatform : IChildSessionPlatform
    {
        public ChildPeer Current { get; } = ChildPeer.Current;
        internal bool Enabled = true;
        internal int? ChildId;
        private ChildPeer? _holder;
        public ChildSystemStatus Probe() => new(Enabled, Current.SessionId, ChildId, true);
        public ChildPeer StartHolder(string root, string instance)
            => _holder = Current with { ProcessId = 12345, StartedTicks = 123456 };
        public bool IsAlive(ChildPeer peer) => _holder == peer;
        public void RequestLogoff(int childSessionId)
        {
            if (ChildId == childSessionId) ChildId = null;
        }
        public void CloseHolder(ChildPeer holder) { if (holder == _holder) _holder = null; }
        public void SetEnabled(bool enabled) => Enabled = enabled;
    }
}
