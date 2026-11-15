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

    [Fact]
    public async Task 持有器失效且无子会话时结束旧实例并重建()
    {
        using var setup = new Setup();
        var ct = TestContext.Current.CancellationToken;
        var first = await setup.Call("start");
        var firstState = await setup.Store.ReadAsync(first.Instance, ct);
        setup.Platform.DeadHolders.Add(firstState.Holder.ProcessId);
        var restarted = await setup.Call("start");
        Assert.Equal("completed", restarted.Status);
        Assert.NotEqual(first.Instance, restarted.Instance);
        var retired = await setup.Store.ReadAsync(first.Instance, ct);
        Assert.Equal("Stopped", retired.State);
        Assert.Contains("重建", retired.Reason);
        Assert.Equal(2, setup.Platform.StartHolderCalls);
    }

    [Fact]
    public async Task 持有器失效但自有子会话仍在时重连同一实例()
    {
        using var setup = new Setup();
        var ct = TestContext.Current.CancellationToken;
        var first = await setup.Call("start");
        var state = await setup.Store.ReadAsync(first.Instance, ct);
        await setup.Store.WriteAsync(state with
        {
            Identity = state.Identity with { ChildSessionId = 7 },
            State = "Ready",
            Reason = "已就绪"
        }, ct);
        setup.Platform.ChildId = 7;
        setup.Platform.DeadHolders.Add(state.Holder.ProcessId);
        var resumed = await setup.Call("start");
        Assert.Equal("completed", resumed.Status);
        Assert.Equal(first.Instance, resumed.Instance);
        Assert.Equal("Starting", resumed.Data.GetProperty("state").GetString());
        Assert.Contains("重连", resumed.Data.GetProperty("reason").GetString());
        Assert.Equal(2, setup.Platform.StartHolderCalls);
    }

    [Fact]
    public async Task 持有器失效且子会话归属不明时拒绝接管()
    {
        using var setup = new Setup();
        var ct = TestContext.Current.CancellationToken;
        var first = await setup.Call("start");
        var state = await setup.Store.ReadAsync(first.Instance, ct);
        await setup.Store.WriteAsync(state with
        {
            Identity = state.Identity with { ChildSessionId = 7 },
            State = "Ready"
        }, ct);
        setup.Platform.ChildId = 8;
        setup.Platform.DeadHolders.Add(state.Holder.ProcessId);
        var refused = await setup.Call("start");
        Assert.Equal("not-executed", refused.Status);
        Assert.Contains("归属不明", refused.Message);
        Assert.Equal("Ready", (await setup.Store.ReadAsync(first.Instance, ct)).State);
        Assert.Equal(1, setup.Platform.StartHolderCalls);
    }

    [Fact]
    public async Task 状态分开报告持有器worker会话与GUI可交互()
    {
        using var setup = new Setup();
        var ct = TestContext.Current.CancellationToken;
        var first = await setup.Call("start");
        var state = await setup.Store.ReadAsync(first.Instance, ct);
        var worker = setup.Platform.Current with { ProcessId = 45678, StartedTicks = 87654 };
        setup.Platform.KnownWorkers.Add(worker.ProcessId);
        setup.Platform.ChildId = 7;
        await setup.Store.WriteAsync(state with
        {
            Identity = state.Identity with { ChildSessionId = 7, WorkerPid = worker.ProcessId, WorkerStartedTicks = worker.StartedTicks },
            Worker = worker,
            State = "Ready",
            Reason = "已就绪"
        }, ct);
        var healthy = await setup.Call("status", first.Instance);
        var health = healthy.Data.GetProperty("health");
        Assert.True(health.GetProperty("holderAlive").GetBoolean());
        Assert.True(health.GetProperty("workerAlive").GetBoolean());
        Assert.True(health.GetProperty("childSessionMatches").GetBoolean());
        Assert.True(health.GetProperty("childSessionActive").GetBoolean());
        Assert.True(health.GetProperty("guiInteractive").GetBoolean());
        setup.Platform.DeadWorkers.Add(worker.ProcessId);
        var degraded = await setup.Call("status", first.Instance);
        var degradedHealth = degraded.Data.GetProperty("health");
        Assert.False(degradedHealth.GetProperty("workerAlive").GetBoolean());
        Assert.False(degradedHealth.GetProperty("guiInteractive").GetBoolean());
    }

    [Fact]
    public async Task 持有器退出后带不带实例的状态查询都不再报就绪()
    {
        using var setup = new Setup();
        var ct = TestContext.Current.CancellationToken;
        var first = await setup.Call("start");
        var state = await setup.Store.ReadAsync(first.Instance, ct);
        setup.Platform.DeadHolders.Add(state.Holder.ProcessId);
        var byInstance = await setup.Call("status", first.Instance);
        Assert.Equal("Faulted", byInstance.Data.GetProperty("state").GetString());
        var current = await setup.Call("status");
        Assert.Equal("Faulted", current.Data.GetProperty("state").GetString());
        Assert.False(current.Data.GetProperty("health").GetProperty("guiInteractive").GetBoolean());
        Assert.Equal("Starting", (await setup.Store.ReadAsync(first.Instance, ct)).State);
    }

    [Fact]
    public async Task 持有器失效且无子会话时可直接停止实例记录()
    {
        using var setup = new Setup();
        var ct = TestContext.Current.CancellationToken;
        var first = await setup.Call("start");
        var state = await setup.Store.ReadAsync(first.Instance, ct);
        setup.Platform.DeadHolders.Add(state.Holder.ProcessId);
        var stopped = await setup.Call("stop", first.Instance);
        Assert.Equal("completed", stopped.Status);
        Assert.Equal("Stopped", (await setup.Store.ReadAsync(first.Instance, ct)).State);
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
        internal readonly HashSet<int> DeadHolders = [];
        internal readonly HashSet<int> DeadWorkers = [];
        internal readonly HashSet<int> KnownWorkers = [];
        internal int StartHolderCalls { get; private set; }
        private ChildPeer? _holder;
        private readonly HashSet<int> _holders = [];
        public ChildSystemStatus Probe() => new(Enabled, Current.SessionId, ChildId, true);
        public ChildPeer StartHolder(string root, string instance)
        {
            _holder = Current with { ProcessId = 12345 + ++StartHolderCalls, StartedTicks = 123456 };
            _holders.Add(_holder.ProcessId);
            return _holder;
        }
        public bool IsAlive(ChildPeer peer) => !DeadHolders.Contains(peer.ProcessId) &&
            !DeadWorkers.Contains(peer.ProcessId) &&
            (_holders.Contains(peer.ProcessId) || KnownWorkers.Contains(peer.ProcessId));
        public bool? IsActive(int sessionId) => ChildId == sessionId ? true : null;
        public void RequestLogoff(int childSessionId)
        {
            if (ChildId == childSessionId) ChildId = null;
        }
        public void CloseHolder(ChildPeer holder) { if (holder == _holder) _holder = null; }
        public void SetEnabled(bool enabled) => Enabled = enabled;
    }
}
