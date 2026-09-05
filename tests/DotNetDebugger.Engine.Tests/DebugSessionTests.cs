using System.Diagnostics;
using DotNetDebugger.Engine;
using DotNetDebugger.Engine.Models;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// DebugSession 公共 API 集成测试（Task 4）：attach → Continue → 进程自然退出 → SessionStateChanged(Exited)。
/// 验证外观层把 DebugEngineCore 引导/事件流正确暴露。
/// </summary>
public sealed class DebugSessionTests
{
    [Fact]
    public async Task Attach_ThenContinue_ProcessRunsToExit_EmitsStateEvents()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        // 目标：3 迭代 + 3s 启动延迟（attach 窗口足够），随后自然退出
        using var target = Process.Start(new ProcessStartInfo(exe, "3 3")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        await Task.Delay(800);
        Assert.False(target.HasExited);

        // 收集事件
        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id);

        // AttachAsync 返回前可能已产生部分事件；订阅读端（Channel 缓冲，能追到历史事件）
        var readerTask = ConsumeAsync(session.Events, events);
        await Task.Delay(200);

        // attach 后进程停在同步态：显式 Continue 让其运行到自然退出
        await session.ContinueAsync();

        var exitDeadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100);
        Assert.True(target.HasExited, "DebugTarget 未在附加并 Continue 后退出");

        // 等 Exited 状态事件到达
        var evtDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < evtDeadline &&
               !events.Any(e => e.Kind == DebugEventKind.SessionStateChanged
                                && (e.Payload as SessionStateChangedPayload)?.State == DebugSessionState.Exited))
            await Task.Delay(50);

        Assert.Contains(events, e => e.Kind == DebugEventKind.SessionStateChanged
            && (e.Payload as SessionStateChangedPayload)?.State == DebugSessionState.Exited);
        readerTask.Wait(2000);
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
    }
}
