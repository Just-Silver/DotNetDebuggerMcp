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
        await Task.Delay(800);
        Assert.False(target.HasExited, "DebugTarget(throw) 提前退出");

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200);

        // 设异常断点：全部 first-chance 异常停下
        await session.SetExceptionBreakpointAsync(); // typeName 空 = 全部
        await session.ContinueAsync();

        // 等 ExceptionHit（进程 delay 5s 后抛异常；兜底 15s）
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline &&
               !events.Any(e => e.Kind == DebugEventKind.ExceptionHit))
            await Task.Delay(100);

        Assert.Contains(events, e => e.Kind == DebugEventKind.ExceptionHit);
        var hit = events.Last(e => e.Kind == DebugEventKind.ExceptionHit);
        var payload = Assert.IsType<ExceptionHitPayload>(hit.Payload);
        Assert.True(payload.ThreadId > 0);

        // 命中后清理（进程是未处理异常，会崩——detach 让进程走完）
        await session.DisconnectAsync();
        target.WaitForExit(10000);
        Assert.True(target.HasExited);
        reader.Wait(2000);
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
    }
}

