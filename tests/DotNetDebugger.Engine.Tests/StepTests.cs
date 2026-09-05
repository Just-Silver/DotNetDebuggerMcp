using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// 单步测试（Task 6）：attach → Work 入口断点 → 命中后 StepOver → 等 StepCompleted → 多次 StepOver →
/// 最后 Continue 到退出。验证 step 命令在停点后恢复执行并产生 StepCompleted 事件。
/// </summary>
public sealed class StepTests
{
    [Fact]
    public async Task StepOver_AfterBreakpoint_EmitsStepCompletedAndResumes()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        using var target = DebugTargetProcess.Start("5 5");
        await Task.Delay(800);
        Assert.False(target.HasExited);

        var workToken = ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "Work");
        Assert.True(workToken > 0);

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200);

        // 断点 + 继续 → 命中
        await session.SetBreakpointAsync("DebugTarget.dll", workToken, 0);
        await session.ContinueAsync();
        await WaitForAsync(() => events.Any(e => e.Kind == DebugEventKind.BreakpointHit), 15_000);

        // 单步 over 3 次，每次等 StepCompleted
        for (var i = 0; i < 3; i++)
        {
            var before = events.Count(e => e.Kind == DebugEventKind.StepCompleted);
            await session.StepOverAsync();
            await WaitForAsync(() => events.Count(e => e.Kind == DebugEventKind.StepCompleted) > before, 15_000);
        }
        Assert.Contains(events, e => e.Kind == DebugEventKind.StepCompleted);

        // 最终 Continue 到退出
        await session.ContinueAsync();
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100);
        Assert.True(target.HasExited, "DebugTarget 在单步后未正常退出");
        reader.Wait(2000);
    }

    private static async Task WaitForAsync(Func<bool> cond, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !cond()) await Task.Delay(50);
        Assert.True(cond(), "等待条件超时未满足");
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

