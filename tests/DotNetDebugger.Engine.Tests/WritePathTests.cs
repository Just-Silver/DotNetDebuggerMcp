using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// W1 引擎路径写原语集成（SetPathValueAsync）：attach DebugTarget probe 模式 → 断点停住 →
/// 值类型参数/对象字段/数组元素/引用置 null/引用重定向/struct 内层字段写回 + readonly/值类型非法文本降级语义。
/// 支持矩阵来自 spec §7（Task 1 spike 实测定案）：enum 对象字段直写为 v1 降级（断言中文拒绝文案）。
/// </summary>
public sealed class WritePathTests
{
    [Fact]
    public async Task SetPathValue_ScalarsNullRedirectStructAndRejections()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var dll = Path.ChangeExtension(exe, ".dll");

        // probe 模式：delay 5s 提供 attach 窗口 → WriteProbe.Run(h{N=5,Tag=tagA,Kind=Mon,P(X=1,Y=2)}, alt{N=9,Tag=tagB}, {3,1,4}, seed=0)
        using var target = DebugTargetProcess.Start("probe 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var runToken = ReadMethodToken(dll, "Run");
        Assert.True(runToken > 0, "未找到 WriteProbe.Run token（请确认 generate-testdata.ps1 已生成 WriteProbe）");

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, null, TestContext.Current.CancellationToken);
        var readerTask = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        await session.SetBreakpointAsync("DebugTarget.dll", runToken, ilOffset: 0, ct: TestContext.Current.CancellationToken);
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var tid = await WaitForHitAsync(events, 0);
        var ct = TestContext.Current.CancellationToken;

        // 参数写回（栈上值类型活引用；spec §7 保留护栏——Task 1 spike 删后长期回归）
        var setSeed = await session.SetPathValueAsync(tid, "seed", [], new DebugWriteValue.Scalar("6"), ct);
        Assert.StartsWith("0", setSeed.OldDisplay);
        var seedBack = await session.EvaluatePathAsync(tid, "seed", [], ct);
        Assert.Equal("6", seedBack.Display);

        // 对象值类型字段 h.N
        var setN = await session.SetPathValueAsync(tid, "h", [new PathSegment.Field("N")], new DebugWriteValue.Scalar("99"), ct);
        Assert.StartsWith("5", setN.OldDisplay); // 原值回显
        Assert.Equal("99", setN.NewDisplay);
        var a = await session.EvaluatePathAsync(tid, "h", [new PathSegment.Field("N")], ct);
        Assert.Equal(DebugEvalKind.Scalar, a.Kind);
        Assert.Equal(99, Assert.IsType<int>(a.ScalarValue));

        // 数组元素 arr[0]
        var arr0 = await session.SetPathValueAsync(tid, "arr", [new PathSegment.Index(0)], new DebugWriteValue.Scalar("42"), ct);
        Assert.StartsWith("3", arr0.OldDisplay);
        var arrBack = await session.EvaluatePathAsync(tid, "arr", [new PathSegment.Index(0)], ct);
        Assert.Equal("42", arrBack.Display);

        // struct 值类型字段内层（spike 通过：对象值 GetFieldValue → 内层标量直写）
        var setPx = await session.SetPathValueAsync(tid, "h", [new PathSegment.Field("P"), new PathSegment.Field("X")], new DebugWriteValue.Scalar("7"), ct);
        Assert.StartsWith("1", setPx.OldDisplay);
        var pxBack = await session.EvaluatePathAsync(tid, "h", [new PathSegment.Field("P"), new PathSegment.Field("X")], ct);
        Assert.Equal("7", pxBack.Display);

        // 引用置 null → 读回 Null
        var nullTag = await session.SetPathValueAsync(tid, "h", [new PathSegment.Field("Tag")], new DebugWriteValue.Null(), ct);
        Assert.Contains("tagA", nullTag.OldDisplay);
        var nullBack = await session.EvaluatePathAsync(tid, "h", [new PathSegment.Field("Tag")], ct);
        Assert.Equal(DebugEvalKind.Null, nullBack.Kind);

        // 引用重定向 → 读回 "tagB"
        var peer = await session.SetPathValueAsync(tid, "h", [new PathSegment.Field("Tag")],
            new DebugWriteValue.CopyPath("alt", [new PathSegment.Field("Tag")]), ct);
        Assert.Contains("tagB", peer.NewDisplay);
        var redirectBack = await session.EvaluatePathAsync(tid, "h", [new PathSegment.Field("Tag")], ct);
        Assert.Equal("\"tagB\"", redirectBack.Display);

        // readonly 字段拒绝
        var readOnly = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SetPathValueAsync(tid, "h", [new PathSegment.Field("FixedVal")], new DebugWriteValue.Scalar("1"), ct));
        Assert.Contains("readonly", readOnly.Message);

        // 值类型非法文本（"abc" 不是数字 → 中文报错附目标描述）
        var badScalar = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SetPathValueAsync(tid, "h", [new PathSegment.Field("N")], new DebugWriteValue.Scalar("abc"), ct));
        Assert.Contains("abc", badScalar.Message);

        // enum 对象字段直写 = v1 降级（spike 定案：字段终端为对象值非 GenericValue）——断言中文拒绝文案
        var enumWrite = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SetPathValueAsync(tid, "h", [new PathSegment.Field("Kind")], new DebugWriteValue.Scalar("2"), ct));
        Assert.Contains("暂不支持写", enumWrite.Message);

        // 无此线程 → 抛错同 EvaluatePath（线程定位守卫）
        var noThread = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SetPathValueAsync(999999, "h", [new PathSegment.Field("N")], new DebugWriteValue.Scalar("1"), ct));
        Assert.Contains("找不到线程", noThread.Message);

        // 恢复到退出，不留挂起进程（改 seed=6 后循环 j 从 6 起；h.N=99 应在输出可见）
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(target.HasExited, "DebugTarget 求值恢复后未正常退出");

        await readerTask.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    /// <summary>等第 expectedCount+1 次断点命中，返回停点线程 id（命中后进程处于 Stopped，可直接写值）。</summary>
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
