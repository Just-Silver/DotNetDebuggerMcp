using DotNetDebuggerMcp.Tools.Debugger;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// 合并后工具入口的 action 分派：非法值必须列全合法值，未知动作不能被静默当成默认分支
/// （诊断类用例不需要调试会话，快）。
/// </summary>
public sealed class DebugToolMergeTests
{
    [Fact]
    public async Task 断点工具_未知action_列出合法值()
    {
        var result = await DebugBreakpointTool.DebugBreakpoint("bogus", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("set/list/remove/clear", result);
    }

    [Fact]
    public async Task 断点工具_action清单_无会话时给中文提示()
    {
        var result = await DebugBreakpointTool.DebugBreakpoint("list", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("无活动调试会话", result);
    }

    [Fact]
    public async Task 断点工具_action移除_缺id给中文提示()
    {
        var result = await DebugBreakpointTool.DebugBreakpoint("remove", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("无活动调试会话", result);
    }

    [Fact]
    public async Task 异常工具_未知action_列出合法值()
    {
        var result = await DebugExceptionTool.DebugExceptions("bogus", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("set/clear", result);
    }

    [Fact]
    public async Task 会话工具_未知action_列出合法值()
    {
        var result = await DebugSessionTool.DebugSession("bogus", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("disconnect/terminate/reset", result);
    }

    [Fact]
    public async Task 会话工具_action重置_无会话时给中文提示()
    {
        var result = await DebugSessionTool.DebugSession("reset", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("无活动调试会话", result);
    }
}
