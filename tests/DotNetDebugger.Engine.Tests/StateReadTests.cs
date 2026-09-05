using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
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
        Assert.True(File.Exists(TestPaths.DebugTargetExe));

        using var target = DebugTargetProcess.Start("5 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var workToken = ReadMethodToken(Path.ChangeExtension(TestPaths.DebugTargetExe, ".dll"), "Work");
        Assert.True(workToken > 0);

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, TestContext.Current.CancellationToken);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await session.SetBreakpointAsync("DebugTarget.dll", workToken, 0, TestContext.Current.CancellationToken);
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        // 等命中
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline &&
               !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Contains(events, e => e.Kind == DebugEventKind.BreakpointHit);
        var hit = events.Last(e => e.Kind == DebugEventKind.BreakpointHit);
        var hitPayload = Assert.IsType<BreakpointHitPayload>(hit.Payload);
        var threadId = hitPayload.ThreadId;
        Assert.True(threadId > 0);

        // 读线程列表（应包含命中线程）
        var threads = await session.GetThreadsAsync(TestContext.Current.CancellationToken);
        Assert.Contains(threads, t => t.ThreadId == threadId);

        // 读调用栈：顶帧应在 DebugTarget.dll 的 Work（token 匹配）
        var frames = await session.GetStackFramesAsync(threadId, TestContext.Current.CancellationToken);
        Assert.NotEmpty(frames);
        var topFrame = frames[0];
        Assert.Equal("DebugTarget.dll", topFrame.Location.ModuleName);
        Assert.Equal(workToken, topFrame.Location.MethodToken);

        // 读局部变量（Work 有 for 的 i / acc；参数名取 DLL Param 表，局部名取模块旁 PDB）
        var vars = await session.GetVariablesAsync(threadId, TestContext.Current.CancellationToken);
        Assert.True(vars.ContainsKey("locals"), "应返回 locals 分组");
        Assert.True(vars.ContainsKey("arguments"), "应返回 arguments 分组");
        // Work 有参数 iterations + 至少 1 个局部变量
        Assert.NotEmpty(vars["arguments"]);
        Assert.NotEmpty(vars["locals"]);
        // 参数名（元数据）：Work(int iterations) → slot0 名为 iterations
        Assert.Equal("iterations", vars["arguments"][0].Name);
        // 局部名（PDB）：至少一个变量有名。局部名取模块旁 PDB，无 PDB 时回退 slot 编号是预期降级（该断言仅在 PDB 在位时生效）
        var pdbExists = File.Exists(Path.ChangeExtension(TestPaths.DebugTargetExe, ".pdb"));
        if (pdbExists)
            Assert.True(vars["locals"].Any(v => v.Name is not null),
                "局部变量应从 PDB 解析出名字（检查 tests/TestData/DebugTarget.pdb 是否已生成）");

        // 恢复并退出
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(target.HasExited, "读取状态后未正常退出");
        await reader.WaitBounded(2000, TestContext.Current.CancellationToken);
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

