using System.ComponentModel;
using SharpSight.ChildSession.Connection;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildConnectionReadinessTests
{
    private const string Instance = "readiness";

    [Fact]
    public async Task 已连接但会话尚不可查询时继续等待并保留自有会话编号()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await WriteAwaitingAsync(store, childSessionId: 0, ct);
            var waiting = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                new RdpReadiness(true, true, true, null), () => 7,
                _ => throw new Win32Exception(2, "会话尚不可查询"), false, false, ct);
            Assert.Equal(ConnectionRound.Hold, waiting);
            var state = await store.ReadAsync(Instance, ct);
            Assert.Equal(7, state.Identity.ChildSessionId);
            Assert.Equal("AwaitingAuthentication", state.State);
            Assert.Null(state.Worker);
            var ready = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                new RdpReadiness(true, true, true, null), () => 7, _ => true, false, false, ct);
            Assert.Equal(ConnectionRound.LaunchWorker, ready);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 登录事件尚未确认时不查询无权限的会话也不投递()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await WriteAwaitingAsync(store, childSessionId: 0, ct);
            var round = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                new RdpReadiness(true, true, false, null), () => 7,
                _ => throw new Win32Exception(5, "登录过程暂不可查询"), false, false, ct);
            Assert.Equal(ConnectionRound.Hold, round);
            var state = await store.ReadAsync(Instance, ct);
            Assert.Equal(7, state.Identity.ChildSessionId);
            Assert.Equal("AwaitingAuthentication", state.State);
            Assert.Null(state.Worker);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 登录确认后仍以WTS会话身份决定投递且拒绝不被吞掉()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await WriteAwaitingAsync(store, childSessionId: 0, ct);
            var confirmed = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                new RdpReadiness(true, true, true, null), () => 7, _ => true, false, false, ct);
            Assert.Equal(ConnectionRound.LaunchWorker, confirmed);
            var denied = await Assert.ThrowsAsync<Win32Exception>(() =>
                ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                    new RdpReadiness(true, true, true, null), () => 7,
                    _ => throw new Win32Exception(5, "登录后仍拒绝访问"), false, false, ct));
            Assert.Equal(5, denied.NativeErrorCode);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 会话编号变化不能被当成本实例的新目标()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await WriteAwaitingAsync(store, childSessionId: 7, ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                    new RdpReadiness(true, true, true, null), () => 8,
                    _ => throw new InvalidOperationException("编号变化时不应查询WTS会话"), false, false, ct));
            Assert.Equal(7, (await store.ReadAsync(Instance, ct)).Identity.ChildSessionId);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 状态读取超时只按本轮未确认处理而不判故障()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var never = new TaskCompletionSource<RdpReadiness>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readiness = await ChildHolderRuntime.AwaitReadinessAsync(never.Task, TimeSpan.FromMilliseconds(50), ct);
            Assert.False(readiness.Observed);
            var round = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance, readiness,
                () => throw new InvalidOperationException("未确认时不应查询子会话编号"),
                _ => throw new InvalidOperationException("未确认时不应查询WTS会话"), false, false, ct);
            Assert.Equal(ConnectionRound.Hold, round);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 登录事件订阅失败时明确拒绝而不是无限等待认证()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                    new RdpReadiness(true, true, false, "COMException：0x80004005"),
                    () => throw new InvalidOperationException("订阅失败后不应查询子会话编号"),
                    _ => throw new InvalidOperationException("订阅失败后不应查询WTS会话"), false, false, ct));
            Assert.Contains("登录事件", failure.Message);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 关窗后不再按过期的已连接登录快照投递worker()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await WriteAwaitingAsync(store, childSessionId: 0, ct);
            var round = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                new RdpReadiness(true, true, true, null),
                () => throw new InvalidOperationException("关窗后不应查询子会话编号"),
                _ => throw new InvalidOperationException("关窗后不应查询WTS会话"), false, true, ct);
            Assert.Equal(ConnectionRound.Disconnected, round);
            var state = await store.ReadAsync(Instance, ct);
            Assert.Equal(0, state.Identity.ChildSessionId);
            Assert.Null(state.Worker);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 关窗且状态读取未确认时直接判断连而不继续等待()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var round = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance, RdpReadiness.Unconfirmed,
                () => throw new InvalidOperationException("关窗后不应查询子会话编号"),
                _ => throw new InvalidOperationException("关窗后不应查询WTS会话"), false, true, ct);
            Assert.Equal(ConnectionRound.Disconnected, round);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 已投送的worker在窗口仍连接时不会再次投递()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var round = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                new RdpReadiness(true, true, true, null),
                () => throw new InvalidOperationException("已投送后不应再次查询子会话编号"),
                _ => throw new InvalidOperationException("已投送后不应再次查询WTS会话"), true, false, ct);
            Assert.Equal(ConnectionRound.Hold, round);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task 已投送与未投送的连接消失分别映射为断连与等待认证()
    {
        var root = NewRoot();
        var store = new ChildInstanceStore(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var waiting = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                new RdpReadiness(true, false, false, null),
                () => throw new InvalidOperationException("未连接时不应查询子会话编号"),
                _ => throw new InvalidOperationException("未连接时不应查询WTS会话"), false, false, ct);
            Assert.Equal(ConnectionRound.AwaitingAuthentication, waiting);
            var dropped = await ChildHolderRuntime.EvaluateConnectionRoundAsync(store, Instance,
                new RdpReadiness(true, false, false, null),
                () => throw new InvalidOperationException("未连接时不应查询子会话编号"),
                _ => throw new InvalidOperationException("未连接时不应查询WTS会话"), true, false, ct);
            Assert.Equal(ConnectionRound.Disconnected, dropped);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData("awaiting", false, false, 12, true)]
    [InlineData("awaiting", false, false, 20, true)]
    [InlineData("awaiting", false, false, 5, false)]
    [InlineData("awaiting", true, false, 20, false)]
    [InlineData("awaiting", false, true, 20, false)]
    [InlineData("disconnected", false, false, 20, false)]
    [InlineData("hold", false, false, 20, false)]
    public void 认证停住时只补配一次凭据(string roundName, bool prompted, bool closed,
        int elapsedSeconds, bool expected)
    {
        var round = roundName switch
        {
            "awaiting" => ConnectionRound.AwaitingAuthentication,
            "disconnected" => ConnectionRound.Disconnected,
            _ => ConnectionRound.Hold,
        };
        var start = DateTimeOffset.UtcNow;
        Assert.Equal(expected, ChildHolderRuntime.ShouldPromptCredential(
            round, prompted, closed, start.AddSeconds(12), start.AddSeconds(elapsedSeconds)));
    }

    private static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "child-readiness-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private static Task WriteAwaitingAsync(ChildInstanceStore store, int childSessionId, CancellationToken ct)
    {
        var peer = ChildPeer.Current;
        var identity = new ChildIdentity(Instance, peer.Sid, peer.SessionId, childSessionId,
            peer.ProcessId, peer.StartedTicks, 0, 0);
        return store.WriteAsync(new(identity, "", "", "", peer, null, "AwaitingAuthentication", "等待认证"), ct);
    }
}
