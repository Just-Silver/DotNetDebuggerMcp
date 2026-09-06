using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// 对象/数组成员展开测试：args[0]="bag" 触发 WorkBag(Bag, int)（Bag 含 int/string 字段）；
/// WorkBag 入口断点停住后读变量 → 对象参数 b 的 Children 应含字段 A=7 / S="sx"，n 为标量 2。
/// </summary>
public sealed class ObjectValueTests
{
    [Fact]
    public async Task ObjectVariable_ExpandsFieldChildren()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        using var target = DebugTargetProcess.Start("bag 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var bagToken = ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "WorkBag");
        Assert.True(bagToken > 0, "DebugTarget 中未找到 WorkBag 方法");

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, TestContext.Current.CancellationToken);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        var bp = await session.SetBreakpointAsync("DebugTarget.dll", bagToken, 0, ct: TestContext.Current.CancellationToken);
        Assert.True(bp.Id > 0);
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline &&
               !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Contains(events, e => e.Kind == DebugEventKind.BreakpointHit);

        var hit = events.Last(e => e.Kind == DebugEventKind.BreakpointHit);
        var payload = Assert.IsType<BreakpointHitPayload>(hit.Payload);
        var threadId = payload.ThreadId;

        var vars = await session.GetVariablesAsync(threadId, TestContext.Current.CancellationToken);
        var b = vars["arguments"].First(v => v.Name == "b");
        var children = b.Value.Children;
        Assert.NotNull(children);
        Assert.Contains(children!, c => c.Name == "A" && c.Value.Display == "7");
        Assert.Contains(children!, c => c.Name == "S" && c.Value.Display == "\"sx\"");
        var n = vars["arguments"].First(v => v.Name == "n");
        Assert.Equal("5", n.Value.Display);

        // 恢复并退出
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(target.HasExited, "读取变量后未正常退出");
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
