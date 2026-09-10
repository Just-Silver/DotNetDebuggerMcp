using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// 实例方法参数名解析回归：有 this 时 ilf.Arguments 槽 0 = this，参数名须从槽 1 起对齐
/// （否则整体右移——this 占用首形参名、真实末参退化成 slotN）。
/// 锚点 InstanceProbe.Run(alpha, beta)（实例方法，seed 字段）。
/// </summary>
public sealed class InstanceArgumentNameTests
{
    [Fact]
    public async Task InstanceMethod_ArgumentsNamesAlignWithThis()
    {
        Assert.True(File.Exists(TestPaths.DebugTargetExe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var dll = Path.ChangeExtension(TestPaths.DebugTargetExe, ".dll");

        // inst 模式：delay 5s 后 new InstanceProbe{Seed=42}.Run(7, "hi")
        using var target = DebugTargetProcess.Start("inst 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var runToken = ReadMethodToken(dll, "InstanceProbe", "Run");
        Assert.True(runToken > 0, "未找到 InstanceProbe.Run token（请确认 generate-testdata.ps1 已生成 inst 样本）");

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, null, TestContext.Current.CancellationToken);
        var reader = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await session.SetBreakpointAsync("DebugTarget.dll", runToken, 0, ct: TestContext.Current.CancellationToken);
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Contains(events, e => e.Kind == DebugEventKind.BreakpointHit);
        var hit = events.Last(e => e.Kind == DebugEventKind.BreakpointHit);
        var threadId = Assert.IsType<BreakpointHitPayload>(hit.Payload).ThreadId;

        var vars = await session.GetVariablesAsync(threadId, TestContext.Current.CancellationToken);
        var args = vars["arguments"];

        // 实例方法：槽 0 = this，槽 1/2 = 形参 alpha/beta（有 this 则参数名从槽 1 起套用）
        Assert.Equal(3, args.Count);
        Assert.Equal("this", args[0].Name);
        Assert.Equal("alpha", args[1].Name);
        Assert.Equal("beta", args[2].Name);
        // 值对齐：alpha=7、beta="hi"（此断言同时证明名字与槽位不错位）
        Assert.Equal("7", args[1].Value.Display);
        Assert.Equal("\"hi\"", args[2].Value.Display);

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

    private static int ReadMethodToken(string dllPath, string typeName, string methodName)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            if (mr.GetString(td.Name) != typeName) continue;
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
