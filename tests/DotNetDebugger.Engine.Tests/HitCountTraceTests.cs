using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// P5：断点命中计数（hitCount，第 N 次起生效）与 trace 模式（命中不停、快照轨迹）集成测试。
/// 目标 DebugTarget：Work(n) 循环调 Compute(i, 1) n 次——Compute 入口断点可稳定命中 n 次。
/// </summary>
public sealed class HitCountTraceTests
{
    private static string Exe => TestPaths.DebugTargetExe;
    private static string Dll => Path.ChangeExtension(Exe, ".dll");

    [Fact]
    public async Task HitCount_第三命中起才停()
    {
        // Work(5)：Compute 被调 5 次；hitCount=3 → 第 1、2 次放行，第 3 次停下（剩余不再执行）
        using var target = DebugTargetProcess.Start("5 4");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited, "DebugTarget 提前退出");

        var computeToken = ReadMethodToken("Compute");
        Assert.True(computeToken > 0);

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, TestContext.Current.CancellationToken);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await session.SetBreakpointAsync("DebugTarget.dll", computeToken, 0, hitCount: 3, ct: TestContext.Current.CancellationToken);
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline
               && !events.Any(e => e.Kind is DebugEventKind.BreakpointHit or DebugEventKind.SessionStateChanged
                   && (e.Payload is SessionStateChangedPayload { State: DebugSessionState.Exited })))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        // 仅第 3 次命中产生停点；无 trace 噪声
        Assert.Single(events, e => e.Kind == DebugEventKind.BreakpointHit);
        Assert.DoesNotContain(events, e => e.Kind == DebugEventKind.TraceHit);

        await session.DisconnectAsync(TestContext.Current.CancellationToken);
        target.WaitForExit(10000);
        await reader.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Trace_记录快照且不停()
    {
        using var target = DebugTargetProcess.Start("5 4");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        var computeToken = ReadMethodToken("Compute");
        Assert.True(computeToken > 0);

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, TestContext.Current.CancellationToken);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await session.SetBreakpointAsync("DebugTarget.dll", computeToken, 0, mode: DebugBreakpointMode.Trace, ct: TestContext.Current.CancellationToken);
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        // 等 Exited（5 次 trace 全部不停，进程跑完）
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline
               && !events.Any(e => e.Payload is SessionStateChangedPayload { State: DebugSessionState.Exited }))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        // 每次 Compute 调用各一条轨迹（i=0..4 的 arguments 快照），全程无停点
        var traces = events.Where(e => e.Kind == DebugEventKind.TraceHit)
            .Select(e => Assert.IsType<TraceHitPayload>(e.Payload)).ToList();
        Assert.Equal(5, traces.Count);
        Assert.DoesNotContain(events, e => e.Kind == DebugEventKind.BreakpointHit);
        Assert.All(traces, t => Assert.Contains(t.Variables, v => v.Scope == "arguments"));
        // 快照值随迭代变化（i 递增的痕迹）：至少两条 display 不同
        Assert.True(traces.Select(t => string.Join("|", t.Variables.Select(v => v.Display))).Distinct().Count() > 1,
            "5 次快照内容应随迭代变化");

        await session.DisconnectAsync(TestContext.Current.CancellationToken);
        target.WaitForExit(10000);
        await reader.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    private static int ReadMethodToken(string methodName)
    {
        using var fs = File.OpenRead(Dll);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                if (mr.GetString(mr.GetMethodDefinition(mh).Name) == methodName)
                    return MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
    }
}
