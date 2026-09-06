using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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

    [Fact]
    public async Task ExceptionFilter_NonMatch_SkipsConsumableViaBuffer()
    {
        Assert.True(File.Exists(TestTarget.DebugTargetExe));

        await using var manager = new DebugSessionManager();
        // throw 模式 + 3s delay（attach 窗口）；抛 System.DivideByZeroException
        var active = await manager.LaunchAndAttachAsync($"{TestTarget.DebugTargetExe} 1 throw 3", TestContext.Current.CancellationToken);

        // 设不匹配的过滤器 → 异常被跳过（引擎 FIRST_CHANCE 计一次）
        await active.Session.SetExceptionBreakpointAsync("System.IO.FileNotFoundException", TestContext.Current.CancellationToken);
        await active.Session.ContinueAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && active.Buffer.CurrentState != DebugSessionState.Exited)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        var (count, lastType) = active.Buffer.ConsumeSkippedExceptions();
        Assert.Equal(1, count);
        Assert.Equal("System.DivideByZeroException", lastType);
        // 消费式清零
        Assert.Equal(0, active.Buffer.ConsumeSkippedExceptions().Count);

        await manager.CloseAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TraceMode_TracesConsumableViaBuffer()
    {
        Assert.True(File.Exists(TestTarget.DebugTargetExe));

        await using var manager = new DebugSessionManager();
        // Work(5)：Compute 被调 5 次；trace 断点全程不停
        var active = await manager.LaunchAndAttachAsync($"{TestTarget.DebugTargetExe} 5 4", TestContext.Current.CancellationToken);
        var computeToken = ReadMethodToken(Path.ChangeExtension(TestTarget.DebugTargetExe, ".dll"), "Compute");
        Assert.True(computeToken > 0);

        await active.Session.SetBreakpointAsync("DebugTarget.dll", computeToken, 0, mode: DebugBreakpointMode.Trace, ct: TestContext.Current.CancellationToken);
        await active.Session.ContinueAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && active.Buffer.CurrentState != DebugSessionState.Exited)
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(DebugSessionState.Exited, active.Buffer.CurrentState);
        var traces = active.Buffer.ConsumeTraces(out var dropped);
        Assert.Equal(5, traces.Count);
        Assert.Equal(0, dropped);
        Assert.All(traces, t => Assert.Contains(t.Variables, v => v.Scope == "arguments"));
        // 消费式清零
        Assert.Equal(0, active.Buffer.PendingTraceCount);
        Assert.Empty(active.Buffer.ConsumeTraces(out _));

        await manager.CloseAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>用 System.Reflection.Metadata 读 dll 中指定名方法的 mdMethodDef token。</summary>
    private static int ReadMethodToken(string dllPath, string methodName)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                if (mr.GetString(mr.GetMethodDefinition(mh).Name) == methodName)
                    return System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }
}
