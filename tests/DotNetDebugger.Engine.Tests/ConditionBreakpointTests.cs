using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// P7 引擎条件断点接线集成：桩求值器（true/false/fail 三种，Engine 测试不引 Session——真求值器由
/// Session 单测与宿主 e2e 覆盖）。验证：条件 false 放行不计数、true 停、求值失败发
/// BreakpointConditionFailed 且放行、条件先于计数（spec §3.4）。
/// </summary>
public sealed class ConditionBreakpointTests
{
    /// <summary>P7 测试桩：true/false/fail 三种条件（真实表达式求值在 Session 层）。</summary>
    private sealed class StubConditionEvaluator : IBreakpointConditionEvaluator
    {
        public bool Evaluate(int threadId, string expression, PathValueResolver pathResolver)
            => expression switch
            {
                "true" => true,
                "false" => false,
                "fail" => throw new InvalidOperationException("stub 求值失败：变量不可见"),
                _ => throw new InvalidOperationException($"未知桩条件 {expression}"),
            };
    }

    [Fact]
    public async Task ConditionFalse_NeverStops_NoFailureEvents()
    {
        var (target, session, events) = await AttachWithBreakpointAsync("false");
        await using var sessionHolder = session;
        using var targetHolder = target;

        await session.ContinueAsync(TestContext.Current.CancellationToken);
        await WaitForExitAsync(target);

        // 永假条件：全程无命中、无失败事件（真性 false 静默放行——spec §3.5），Hits=0（条件先于计数）
        var bps = await session.GetBreakpointsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, bps[0].Hits);
        lock (events)
        {
            Assert.DoesNotContain(events, e => e.Kind == DebugEventKind.BreakpointHit);
            Assert.DoesNotContain(events, e => e.Kind == DebugEventKind.BreakpointConditionFailed);
        }
    }

    [Fact]
    public async Task ConditionTrue_StopsAtFirstHit()
    {
        var (target, session, events) = await AttachWithBreakpointAsync("true");
        await using var sessionHolder = session;
        using var targetHolder = target;

        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        lock (events)
        {
            var hit = events.FirstOrDefault(e => e.Kind == DebugEventKind.BreakpointHit);
            Assert.NotNull(hit);
            var payload = Assert.IsType<BreakpointHitPayload>(hit!.Payload);
            Assert.Equal(1, payload.BreakpointId);
        }
        var bps = await session.GetBreakpointsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, bps[0].Hits);

        // 恢复到退出，不留挂起进程
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        await WaitForExitAsync(target);
    }

    [Fact]
    public async Task ConditionFailure_PublishesEventAndContinues()
    {
        var (target, session, events) = await AttachWithBreakpointAsync("fail");
        await using var sessionHolder = session;
        using var targetHolder = target;

        await session.ContinueAsync(TestContext.Current.CancellationToken);
        await WaitForExitAsync(target);

        // 求值失败：发 BreakpointConditionFailed（Session 计数反馈的数据源）且放行到退出，不产生命中
        lock (events)
        {
            Assert.DoesNotContain(events, e => e.Kind == DebugEventKind.BreakpointHit);
            Assert.Contains(events, e => e.Kind == DebugEventKind.BreakpointConditionFailed);
            var failed = events.Last(e => e.Kind == DebugEventKind.BreakpointConditionFailed);
            var payload = Assert.IsType<BreakpointConditionFailedPayload>(failed.Payload);
            Assert.Equal(1, payload.BreakpointId);
            Assert.Contains("stub 求值失败", payload.Error);
        }
        var bps = await session.GetBreakpointsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, bps[0].Hits);
    }

    /// <summary>attach（bag 5 模式）→ WorkBag 入口断点带桩条件。返回 target/session/事件收集（Dispose 归调用方）。</summary>
    private static async Task<(DebugTargetProcess Target, DebugSession Session, List<DebugEvent> Events)> AttachWithBreakpointAsync(string condition)
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workBagToken = ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "WorkBag");
        Assert.True(workBagToken > 0);

        // 注意：这里不能 using——target/session 生命周期由调用方持有（方法返回即 Dispose 会让 HasExited 抛
        // "No process is associated"，BreakpointTests 把两者都放测试方法顶层正是为此）
        var target = DebugTargetProcess.Start("bag 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var events = new List<DebugEvent>();
        var session = await DebugSession.AttachAsync(target.Id, new StubConditionEvaluator(), TestContext.Current.CancellationToken);
        var readerTask = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var bp = await session.SetBreakpointAsync("DebugTarget.dll", workBagToken, ilOffset: 0, condition: condition, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(bp.Condition);
        return (target, session, events);
    }

    private static async Task WaitForExitAsync(DebugTargetProcess target)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !target.HasExited)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(target.HasExited, "DebugTarget 未按预期退出（条件断点疑似停住了进程）");
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
    }

    private static int ReadMethodToken(string dllPath, string methodName)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                var md = mr.GetMethodDefinition(mh);
                if (mr.GetString(md.Name) == methodName)
                    return MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }
}
