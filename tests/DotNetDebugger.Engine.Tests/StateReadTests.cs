using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine;
using DotNetDebugger.Engine.Models;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// 状态读取测试（Task 7）：断点命中 Work 后读取线程列表/调用栈/局部变量，验证能拿到栈帧位置与变量值。
/// </summary>
public sealed class StateReadTests
{
    [Fact]
    public async Task AfterBreakpoint_ReadStackAndVariables()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe));

        using var target = Process.Start(new ProcessStartInfo(exe, "5 5")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        await Task.Delay(800);
        Assert.False(target.HasExited);

        var workToken = ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "Work");
        Assert.True(workToken > 0);

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200);

        await session.SetBreakpointAsync("DebugTarget.dll", workToken, 0);
        await session.ContinueAsync();

        // 等命中
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline &&
               !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100);
        Assert.Contains(events, e => e.Kind == DebugEventKind.BreakpointHit);
        var hit = events.Last(e => e.Kind == DebugEventKind.BreakpointHit);
        var hitPayload = Assert.IsType<BreakpointHitPayload>(hit.Payload);
        var threadId = hitPayload.ThreadId;
        Assert.True(threadId > 0);

        // 读线程列表（应包含命中线程）
        var threads = await session.GetThreadsAsync();
        Assert.Contains(threads, t => t.ThreadId == threadId);

        // 读调用栈：顶帧应在 DebugTarget.dll 的 Work（token 匹配）
        var frames = await session.GetStackFramesAsync(threadId);
        Assert.NotEmpty(frames);
        var topFrame = frames[0];
        Assert.Equal("DebugTarget.dll", topFrame.Location.ModuleName);
        Assert.Equal(workToken, topFrame.Location.MethodToken);

        // 读局部变量（Work 有 for 的 i / acc；v1 名字空，槽位有值）
        var vars = await session.GetVariablesAsync(threadId);
        Assert.True(vars.ContainsKey("locals"), "应返回 locals 分组");
        Assert.True(vars.ContainsKey("arguments"), "应返回 arguments 分组");
        // Work 有参数 iterations + 至少 1 个局部变量
        Assert.NotEmpty(vars["arguments"]);
        Assert.NotEmpty(vars["locals"]);

        // 恢复并退出
        await session.ContinueAsync();
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100);
        Assert.True(target.HasExited, "读取状态后未正常退出");
        reader.Wait(2000);
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
