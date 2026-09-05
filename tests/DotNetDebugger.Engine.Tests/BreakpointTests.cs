using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine;
using DotNetDebugger.Engine.Models;
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
        using var target = Process.Start(new ProcessStartInfo(exe, "5 5")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        await Task.Delay(800);
        Assert.False(target.HasExited);

        // 读 DebugTarget.dll 元数据拿 Work 的 mdMethodDef token（exe 是 native apphost 无元数据；Engine 测试不引 Decompiler，用 BCL）
        var workToken = ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "Work");
        Assert.True(workToken > 0, "DebugTarget 中未找到 Work 方法");

        // 收集事件
        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id);
        var readerTask = ConsumeAsync(session.Events, events);
        await Task.Delay(200); // 让订阅追上缓冲事件

        // 设断点：Work 方法入口 IL offset 0
        var bp = await session.SetBreakpointAsync("DebugTarget.dll", workToken, ilOffset: 0);
        Assert.True(bp.Id > 0);

        // 下断点后进程需停在断点才 Continue；attach 后进程在跑（delay 中）→ 需先让它跑，断点在 Work 进入时命中。
        // 进程在 delay(5s) 后进入 Work → 断点命中。但 attach 后进程是 running（初始事件已 Continue），
        // 我们无需额外 Continue——等 Work 被调用即可。不过为覆盖「停后继续」，等 BreakpointHit 后调 Continue。
        await session.ContinueAsync(); // 若已在跑则 SafeContinue 容忍

        // 等 BreakpointHit（delay 5s 结束、Work 被调时触发；兜底 15s）
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline &&
               !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100);

        Assert.Contains(events, e => e.Kind == DebugEventKind.BreakpointHit);
        var hit = events.Last(e => e.Kind == DebugEventKind.BreakpointHit);
        var payload = Assert.IsType<BreakpointHitPayload>(hit.Payload);
        Assert.Equal(bp.Id, payload.BreakpointId);
        Assert.NotNull(payload.TopFrame);

        // 命中后进程停在断点：验证能恢复并最终退出
        await session.ContinueAsync();
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100);
        Assert.True(target.HasExited, "DebugTarget 在断点恢复后未正常退出");

        readerTask.Wait(2000);
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
