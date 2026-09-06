using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// P6 引擎路径求值集成（EvaluatePathAsync）：attach DebugTarget → 断点停住 →
/// 字段链/数组任意下标/字符串索引正路径 + 未知根/缺字段/越界/标量误用错误语义（报错附可用字段清单/长度/段号）。
/// 锚点用 bag 模式的 WorkBag/WorkScores 入口（attach 发生在 Main 已执行后，Main 入口不可达——同 BreakpointTests 锚 Work 的时序理由）。
/// </summary>
public sealed class EvaluatePathTests
{
    [Fact]
    public async Task EvaluatePath_FieldsArrayStringsAndErrors()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var dll = Path.ChangeExtension(exe, ".dll");

        // bag 模式：delay 5s 后 WorkBag(new Bag{A=7,S="sx"}, 5) → WorkScores(new[]{3,1,4,1,5}, 5)
        using var target = DebugTargetProcess.Start("bag 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var workBagToken = ReadMethodToken(dll, "WorkBag");
        var workScoresToken = ReadMethodToken(dll, "WorkScores");
        Assert.True(workBagToken > 0 && workScoresToken > 0);

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, null, TestContext.Current.CancellationToken);
        var readerTask = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // ---- 停在 WorkBag 入口：Bag 字段链（参数自入口即存活） ----
        await session.SetBreakpointAsync("DebugTarget.dll", workBagToken, ilOffset: 0, ct: TestContext.Current.CancellationToken);
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var tid = await WaitForHitAsync(events, 0);

        var a = await session.EvaluatePathAsync(tid, "b", [new PathSegment.Field("A")], TestContext.Current.CancellationToken);
        Assert.Equal(DebugEvalKind.Scalar, a.Kind);
        Assert.Equal("7", a.Display);
        Assert.Equal(7, Assert.IsType<int>(a.ScalarValue));

        var s = await session.EvaluatePathAsync(tid, "b", [new PathSegment.Field("S")], TestContext.Current.CancellationToken);
        Assert.Equal(DebugEvalKind.Scalar, s.Kind);
        Assert.Equal("\"sx\"", s.Display);
        Assert.Equal("System.String", s.TypeName);

        // 字符串索引得单字符字符串
        var ch = await session.EvaluatePathAsync(tid, "b", [new PathSegment.Field("S"), new PathSegment.Index(0)], TestContext.Current.CancellationToken);
        Assert.Equal(DebugEvalKind.Scalar, ch.Kind);
        Assert.Equal("\"s\"", ch.Display);
        Assert.Equal("s", Assert.IsType<string>(ch.ScalarValue));

        // 对象终值：与 debug_variables 同款（Display + children 一级）
        var whole = await session.EvaluatePathAsync(tid, "b", [], TestContext.Current.CancellationToken);
        Assert.Equal(DebugEvalKind.Object, whole.Kind);
        Assert.NotNull(whole.Children);
        Assert.Contains(whole.Children!, c => c.Name == "A");
        Assert.Contains(whole.Children!, c => c.Name == "S");

        // 错误语义：缺字段（附可用字段清单）/ 标量误用索引 / 未知根（附可用清单）/ $exception 仅异常停点可用
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.EvaluatePathAsync(tid, "b", [new PathSegment.Field("Missing")], TestContext.Current.CancellationToken));
        Assert.Contains("无此字段", missing.Message);
        Assert.Contains("A, S", missing.Message);
        var scalarIndex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.EvaluatePathAsync(tid, "b", [new PathSegment.Field("A"), new PathSegment.Index(0)], TestContext.Current.CancellationToken));
        Assert.Contains("仅数组/字符串支持索引", scalarIndex.Message);
        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.EvaluatePathAsync(tid, "noSuchVar", [], TestContext.Current.CancellationToken));
        Assert.Contains("栈顶帧无变量", unknown.Message);
        Assert.Contains("b", unknown.Message);
        var noException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.EvaluatePathAsync(tid, "$exception", [], TestContext.Current.CancellationToken));
        Assert.Contains("$exception", noException.Message);

        // ---- 停在 WorkScores 入口：数组任意下标（绕开 MaxChildren=32 截断的直读路径） ----
        await session.SetBreakpointAsync("DebugTarget.dll", workScoresToken, ilOffset: 0, ct: TestContext.Current.CancellationToken);
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        tid = await WaitForHitAsync(events, 1);

        var arr = await session.EvaluatePathAsync(tid, "scores", [], TestContext.Current.CancellationToken);
        Assert.Equal(DebugEvalKind.Array, arr.Kind);
        Assert.NotNull(arr.Children);

        var elem = await session.EvaluatePathAsync(tid, "scores", [new PathSegment.Index(3)], TestContext.Current.CancellationToken);
        Assert.Equal(DebugEvalKind.Scalar, elem.Kind);
        Assert.Equal("1", elem.Display);

        // 错误语义：越界（附长度）/ 数组无字段（提示用索引）
        var oob = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.EvaluatePathAsync(tid, "scores", [new PathSegment.Index(5)], TestContext.Current.CancellationToken));
        Assert.Contains("越界", oob.Message);
        Assert.Contains("5", oob.Message);
        var arrField = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.EvaluatePathAsync(tid, "scores", [new PathSegment.Field("Length")], TestContext.Current.CancellationToken));
        Assert.Contains("数组无字段", arrField.Message);

        // 恢复到退出，不留挂起进程
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited)
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(target.HasExited, "DebugTarget 求值恢复后未正常退出");

        await readerTask.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

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
