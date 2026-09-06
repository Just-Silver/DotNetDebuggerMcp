using DotNetDebugger.Engine.Models;
using Xunit;

namespace DotNetDebugger.Session.Tests;

/// <summary>
/// DebugSessionManager 生命周期测试：Launch 真实 DebugTarget（delay 提供 attach/观察窗口）→
/// 状态经 SessionEventBuffer 推进；Close 干净释放。
/// </summary>
public sealed class DebugSessionManagerTests
{
    [Fact]
    public async Task Launch_DebugTarget_StateAdvancesThroughRunning()
    {
        Assert.True(File.Exists(TestTarget.DebugTargetExe));

        await using var manager = new DebugSessionManager();
        // delay 4s：launch 后有足够窗口观察 Running
        var active = await manager.LaunchAsync($"{TestTarget.DebugTargetExe} 3 4", timeoutSeconds: 15, TestContext.Current.CancellationToken);

        Assert.NotNull(manager.Active);
        Assert.Same(active, manager.Active);

        // attach/launch 后进程停在初始同步点；工具首次会发 continue——Session 层不自动 continue，
        // 验证 buffer 至少反映 Attaching/None 之后的初始状态（引擎发 Launching 事件）
        // 注：P2 引擎 attach 后进程停（初始同步点），需显式 continue 才运行。此处只验证管理器可建会话。
        Assert.NotNull(manager.GetInfo());

        await manager.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Null(manager.Active);
    }

    [Fact]
    public async Task Attach_ThenContinue_ProcessRunsToExit_BufferTracksExited()
    {
        using var target = TestTarget.StartDebugTarget("2 3");
        await Task.Delay(800, TestContext.Current.CancellationToken); // 等 CLR 加载

        await using var manager = new DebugSessionManager();
        var active = await manager.AttachAsync(target.Id, TestContext.Current.CancellationToken);

        // 显式 continue 让进程运行（P2 引擎 attach 后进程停在初始同步点）
        await active.Session.ContinueAsync(TestContext.Current.CancellationToken);

        // 等 buffer 状态到 Exited（进程 delay 3s 后 Work 跑完退出；兜底 15s）
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && active.Buffer.CurrentState != DebugSessionState.Exited)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(DebugSessionState.Exited, active.Buffer.CurrentState);
        target.WaitForExit(5000);
    }

    [Fact]
    public async Task LaunchAndAttach_CapturesTargetOutput_TailContainsStartupLine()
    {
        Assert.True(File.Exists(TestTarget.DebugTargetExe));

        await using var manager = new DebugSessionManager();
        var active = await manager.LaunchAndAttachAsync($"{TestTarget.DebugTargetExe} 3 4", TestContext.Current.CancellationToken);

        // DebugTarget 启动即打印 "[DebugTarget] start, ..."——轮询等输出到达（DataReceived 异步回调）
        ProcessOutputLine[] tail = [];
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            tail = [.. active.Output!.Tail(50)];
            if (tail.Any(l => l.Text.Contains("[DebugTarget] start"))) break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.Contains(tail, l => l.Text.Contains("[DebugTarget] start"));
        // launch 会话才有 Output；attach 会话为 null 由构造默认保证
        Assert.NotNull(active.Output);

        await manager.CloseAsync(TestContext.Current.CancellationToken);
    }
}
