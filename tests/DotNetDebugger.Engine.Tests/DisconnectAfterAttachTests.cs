using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// attach 后立刻断开（用户最常见的「先看一眼就撤」）：目标仍在初始冻结态，detach 不应因
/// CORDBG_E_PROCESS_NOT_SYNCHRONIZED 失败，且目标必须继续独立运行。
/// </summary>
public sealed class DisconnectAfterAttachTests
{
    [Fact]
    public async Task Attach后立即断开_成功且目标继续运行()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        using var target = DebugTargetProcess.Start("5 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        await using var session = await DebugSession.AttachAsync(target.Id, null, TestContext.Current.CancellationToken);

        await session.DisconnectAsync(TestContext.Current.CancellationToken);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited, "断开调试后目标应继续独立运行");
    }

    [Fact]
    public async Task Attach后立即断开_目标阻塞在Sleep中()
    {
        // 复现现场：目标主线程阻塞在 Thread.Sleep（attach 时刻无托管代码执行点）
        using var target = DebugTargetProcess.Start("sleep 12");
        await Task.Delay(800, TestContext.Current.CancellationToken);

        await using var session = await DebugSession.AttachAsync(target.Id, null, TestContext.Current.CancellationToken);

        await session.DisconnectAsync(TestContext.Current.CancellationToken);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);
    }
}
