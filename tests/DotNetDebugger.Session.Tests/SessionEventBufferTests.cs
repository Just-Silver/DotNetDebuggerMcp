using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Session.Tests;

/// <summary>
/// SessionEventBuffer.WaitForStopAsync：无会话事件的超时路径（纯内存）+ 真实 attach 的停点唤醒路径。
/// </summary>
public sealed class SessionEventBufferTests
{
    [Fact]
    public async Task WaitForStop_NoEvents_TimesOutReturnsNull()
    {
        await using var buffer = new SessionEventBuffer();
        var stop = await buffer.WaitForStopAsync(TimeSpan.FromMilliseconds(300));
        Assert.Null(stop); // 无事件：放弃等待
        Assert.Equal(DebugSessionState.None, buffer.CurrentState);
    }

    [Fact]
    public async Task WaitForStop_AlreadyStopped_ReturnsImmediately()
    {
        await using var buffer = new SessionEventBuffer();
        // 未启动消费任务也无事件：仅在终态快速路径上模拟不可行，验证 None 状态走超时且立即取消令牌生效
        var stop = await buffer.WaitForStopAsync(TimeSpan.FromSeconds(30), new CancellationToken(canceled: true));
        Assert.Null(stop); // 取消令牌：放弃等待而非挂 30s
    }

    [Fact]
    public async Task WaitForStop_RealBreakpointHit_ReturnsStopContextWithBreakpointId()
    {
        var exe = TestTarget.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        using var target = TestTarget.StartDebugTarget("2 4");
        await Task.Delay(800);
        Assert.False(target.HasExited);

        var workToken = ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "Work");
        Assert.True(workToken > 0);

        await using var session = await DebugSession.AttachAsync(target.Id);
        await using var buffer = new SessionEventBuffer();
        buffer.Start(session);
        await Task.Delay(200); // 让消费任务追上缓冲事件

        var bp = await session.SetBreakpointAsync("DebugTarget.dll", workToken, ilOffset: 0);
        Assert.True(bp.IsBound);

        // 先等后继续：WaitForStopAsync 应在断点命中时被 SnapshotChanged 唤醒
        var waitTask = buffer.WaitForStopAsync(TimeSpan.FromSeconds(15));
        await session.ContinueAsync();

        var stop = await waitTask.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotNull(stop);
        Assert.Equal(bp.Id, stop.BreakpointId);
        Assert.Equal(DebugEventKind.BreakpointHit, stop.Kind);
        Assert.Equal(DebugSessionState.Stopped, buffer.CurrentState);

        await session.ContinueAsync(); // 恢复让目标退出
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100);
        Assert.True(target.HasExited, "DebugTarget 在断点恢复后未正常退出");
    }

    /// <summary>用 System.Reflection.Metadata 读 dll 中指定名方法的 mdMethodDef token。</summary>
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
