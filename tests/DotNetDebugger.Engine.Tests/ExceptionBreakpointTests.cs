using System.Diagnostics;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// first-chance 异常断点测试（Task 7）：attach 到 DebugTarget(throw 模式，delay 后抛 DivideByZeroException)，
/// 设异常过滤器 → Continue → 进程抛异常时收到 ExceptionHit 事件。
/// </summary>
public sealed class ExceptionBreakpointTests
{
    [Fact]
    public async Task SetExceptionBreakpoint_CatchesFirstChance()
    {
        Assert.True(File.Exists(TestPaths.DebugTargetExe));

        // throw 模式 + 5s delay（attach 窗口）
        using var target = DebugTargetProcess.Start("1 throw 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited, "DebugTarget(throw) 提前退出");

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, null, TestContext.Current.CancellationToken);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // 设异常断点：全部 first-chance 异常停下
        await session.SetExceptionBreakpointAsync(null, TestContext.Current.CancellationToken); // typeName 空 = 全部
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        // 等 ExceptionHit（进程 delay 5s 后抛异常；兜底 15s）
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline &&
               !events.Any(e => e.Kind == DebugEventKind.ExceptionHit))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Contains(events, e => e.Kind == DebugEventKind.ExceptionHit);
        var hit = events.Last(e => e.Kind == DebugEventKind.ExceptionHit);
        var payload = Assert.IsType<ExceptionHitPayload>(hit.Payload);
        Assert.True(payload.ThreadId > 0);
        // P2：类型全名解析（TypeRef 行内 namespace+name）+ Message 捕获（_message 字段）
        Assert.Equal("System.DivideByZeroException", payload.ExceptionType);
        Assert.Equal("value is zero", payload.Message);

        // P2：$exception 伪变量——异常停点 GetVariables 首节，展示含类型全名
        var vars = await session.GetVariablesAsync(payload.ThreadId, TestContext.Current.CancellationToken);
        Assert.True(vars.ContainsKey("exception"), "异常停点应含 exception 节");
        var exc = Assert.Single(vars["exception"]);
        Assert.Equal("$exception", exc.Name);
        Assert.Contains("System.DivideByZeroException", exc.Value.Display);

        // 命中后清理（进程是未处理异常，会崩——detach 让进程走完）
        await session.DisconnectAsync(TestContext.Current.CancellationToken);
        target.WaitForExit(10000);
        Assert.True(target.HasExited);
        await reader.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExceptionTypeFilter_NonMatch_SkipsWithoutStopping()
    {
        Assert.True(File.Exists(TestPaths.DebugTargetExe));

        using var target = DebugTargetProcess.Start("1 throw 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited, "DebugTarget(throw) 提前退出");

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, null, TestContext.Current.CancellationToken);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // 设不匹配的过滤器：DivideByZeroException 不以 .FileNotFoundException 结尾 → 跳过不停
        await session.SetExceptionBreakpointAsync("System.IO.FileNotFoundException", TestContext.Current.CancellationToken);
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        // 等进程跑完退出（未处理异常直接崩）；兜底 20s
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !events.Any(e => e.Kind == DebugEventKind.SessionStateChanged
               && e.Payload is SessionStateChangedPayload { State: DebugSessionState.Exited }))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(events, e => e.Kind == DebugEventKind.ExceptionHit);
        var skipped = events.Where(e => e.Kind == DebugEventKind.ExceptionSkipped)
            .Select(e => Assert.IsType<ExceptionSkippedPayload>(e.Payload)).ToList();
        Assert.NotEmpty(skipped);
        // 只在 FIRST_CHANCE 计一次（USER_FIRST_CHANCE/UNHANDLED 等后续阶段不重复计数）
        Assert.Single(skipped);
        Assert.Equal("System.DivideByZeroException", skipped[0].ExceptionType);

        await session.DisconnectAsync(TestContext.Current.CancellationToken);
        target.WaitForExit(10000);
        await reader.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
    }
}

