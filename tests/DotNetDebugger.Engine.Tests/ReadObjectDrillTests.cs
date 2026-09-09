using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// D1 对象树深读引擎集成（ReadObjectAtPathAsync）：attach DebugTarget drill 模式 → 断点 Drill 入口停住 →
/// 受控递归展开对象/数组 children（depth 层预算 + 沿路径 <cyclic> 环占位）+ 标量/字符串/null 终值错误语义。
/// 锚点 drill 5（delay 5s 提供 attach 窗口）：Main 在 sleep 后才进 drill 分支，断点入口必命中——
/// 用 drill 0 会因 attach 窗口过后 Drill 已执行过而不命中。
/// </summary>
public sealed class ReadObjectDrillTests
{
    [Fact]
    public async Task ReadObjectAtPath_DepthLimitCyclicAndErrorSemantics()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var dll = Path.ChangeExtension(exe, ".dll");

        // drill 模式：Main sleep 5s 后 Drill(a, new[]{3,1,4}, ghost:null)（a→b→c→a 成环；ghost=null 供 null 语义测试）
        using var target = DebugTargetProcess.Start("drill 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var drillToken = ReadMethodToken(dll, "Drill");
        Assert.True(drillToken > 0);

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, null, TestContext.Current.CancellationToken);
        var readerTask = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // ---- 停在 Drill 入口：root/arr/ghost 参数自入口即存活 ----
        await session.SetBreakpointAsync("DebugTarget.dll", drillToken, ilOffset: 0, ct: TestContext.Current.CancellationToken);
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var tid = await WaitForHitAsync(events, 0);

        // depth=1：仅一层 children（与 debug_variables/Evaluate 一致）
        var d1 = await session.ReadObjectAtPathAsync(tid, "root", [], 1, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(d1.Children);
        Assert.Contains(d1.Children!, c => c.Name == "Next");

        // depth=2：Next 的 children（Name/Value/Next）也展开
        var d2 = await session.ReadObjectAtPathAsync(tid, "root", [], 2, ct: TestContext.Current.CancellationToken);
        var next = d2.Children!.Single(c => c.Name == "Next");
        Assert.NotNull(next.Value.Children);
        Assert.Contains(next.Value.Children!, c => c.Name == "Name");

        // 数组：arr 根 depth=1 → 3 个元素
        var arr1 = await session.ReadObjectAtPathAsync(tid, "arr", [], 1, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(arr1.Children);
        Assert.Equal(3, arr1.Children!.Count);

        // 环：沿 Next 链递归找 <cyclic> 占位（根的 Display 是「N 字段」，不会含 cyclic）
        var deep = await session.ReadObjectAtPathAsync(tid, "root", [], 6, ct: TestContext.Current.CancellationToken);
        Assert.True(ContainsCyclic(deep));

        // limit 越界钳制不拒绝：limit=0 钳到 1、limit=9999 钳到 128，仍正常返回（depth 越界同）
        var clamped = await session.ReadObjectAtPathAsync(tid, "root", [], 0, limit: 0, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(clamped.Children);
        Assert.NotEmpty(clamped.Children!);

        // 错误语义：标量/字符串/null 非对象目标（spec §4.1 同文案）
        var scalarErr = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ReadObjectAtPathAsync(tid, "root", [new PathSegment.Field("Value")], 1, ct: TestContext.Current.CancellationToken));
        Assert.Contains("不是对象/数组", scalarErr.Message);
        var nullErr = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.ReadObjectAtPathAsync(tid, "ghost", [], 1, ct: TestContext.Current.CancellationToken)); // Drill(..., ghost: null) 参数：null 非对象
        Assert.Contains("不是对象/数组", nullErr.Message);

        // 恢复到退出，不留挂起进程
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var exitDeadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(target.HasExited, "DebugTarget drill 恢复后未正常退出");

        await readerTask.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    /// <summary>沿 children 递归查找 <cyclic> 环占位（根的 Display 是「N 字段」，不含 cyclic）。</summary>
    private static bool ContainsCyclic(DebugValue v) =>
        v.Display == "<cyclic>" || (v.Children?.Any(c => ContainsCyclic(c.Value)) ?? false);

    /// <summary>等第 expectedCount+1 次断点命中，返回停点线程 id（命中后进程处于 Stopped，可直接求值）。</summary>
    private static async Task<int> WaitForHitAsync(List<DebugEvent> events, int expectedCount)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            int hits;
            lock (events) hits = events.Count(e => e.Kind == DebugEventKind.BreakpointHit);
            if (hits > expectedCount)
            {
                lock (events)
                {
                    var payload = Assert.IsType<BreakpointHitPayload>(
                        events.Last(e => e.Kind == DebugEventKind.BreakpointHit).Payload);
                    return payload.ThreadId;
                }
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"等待第 {expectedCount + 1} 次断点命中超时");
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
    }

    /// <summary>用 System.Reflection.Metadata 读指定名方法的 mdMethodDef token（exe 是 apphost 无元数据，读 dll）。</summary>
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
