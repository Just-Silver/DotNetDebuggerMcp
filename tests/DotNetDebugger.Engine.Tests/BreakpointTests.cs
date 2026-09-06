using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// 断点命中闭环（Task 5）：attach DebugTarget → 在 Work 入口(IL 0)下断点 → Continue → 命中(BreakpointHit)
/// → 再 Continue 到进程退出。验证 DebugSession 断点 API 与 BreakpointManager 绑定。
/// </summary>
public sealed class BreakpointTests
{
    [Fact]
    public async Task SetBreakpoint_OnWorkEntry_HitsAndResumes()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        // 目标：5 迭代 + 5s 启动延迟（attach 窗口）；断点命中后仍需进程继续跑完
        // 目标：5 迭代 + 5s 启动延迟（attach 窗口）；断点命中后仍需进程继续跑完
        using var target = DebugTargetProcess.Start("5 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        // 读 DebugTarget.dll 元数据拿 Work 的 mdMethodDef token（exe 是 native apphost 无元数据；Engine 测试不引 Decompiler，用 BCL）
        var workToken = ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "Work");
        Assert.True(workToken > 0, "DebugTarget 中未找到 Work 方法");

        // 收集事件
        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, TestContext.Current.CancellationToken);
        var readerTask = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken); // 让订阅追上缓冲事件

        // 设断点：Work 方法入口 IL offset 0
        var bp = await session.SetBreakpointAsync("DebugTarget.dll", workToken, ilOffset: 0, ct: TestContext.Current.CancellationToken);
        Assert.True(bp.Id > 0);

        // 模块路径反查：断点模块短名 → 全路径（停点无条件跟随数据源）
        var modulePath = await session.GetModulePathAsync("DebugTarget.dll", TestContext.Current.CancellationToken);
        Assert.Equal(Path.GetFullPath(Path.ChangeExtension(exe, ".dll")), modulePath);

        // attach 后进程停在初始同步点（供设断点）；设完断点后首次 Continue 启动进程
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        // 等 BreakpointHit（进程 delay 5s 后进 Work 触发；兜底 15s）
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline &&
               !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Contains(events, e => e.Kind == DebugEventKind.BreakpointHit);
        var hit = events.Last(e => e.Kind == DebugEventKind.BreakpointHit);
        var payload = Assert.IsType<BreakpointHitPayload>(hit.Payload);
        Assert.Equal(bp.Id, payload.BreakpointId);
        Assert.NotNull(payload.TopFrame);

        // 命中后进程停在断点：验证能恢复并最终退出
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(target.HasExited, "DebugTarget 在断点恢复后未正常退出");

        await readerTask.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SetBreakpoint_ModuleNotLoaded_RegistersPendingAndDoesNotHit()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        using var target = DebugTargetProcess.Start("1 2");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var workToken = ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "Work");

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id, TestContext.Current.CancellationToken);
        var readerTask = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // 模块名错误：不再抛错，登记为 pending（加载后自动绑定；该模块永不加载 → 永不命中）
        var bp = await session.SetBreakpointAsync("NoSuchModule.dll", workToken, ilOffset: 0, ct: TestContext.Current.CancellationToken);
        Assert.True(bp.Id > 0);
        Assert.False(bp.IsBound);

        await session.ContinueAsync(TestContext.Current.CancellationToken);

        // 进程正常跑完退出，期间不得有 BreakpointHit
        var exitDeadline = DateTime.UtcNow.AddSeconds(12);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(target.HasExited, "pending 断点不应影响进程运行退出");
        lock (events)
            Assert.DoesNotContain(events, e => e.Kind == DebugEventKind.BreakpointHit);

        await readerTask.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
    }

    /// <summary>用 System.Reflection.Metadata 读 exe 中指定名方法的 mdMethodDef token。</summary>
    private static int ReadMethodToken(string exePath, string methodName)
    {
        using var fs = File.OpenRead(exePath);
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

