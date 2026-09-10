using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session.Models;
using DotNetDebuggerMcp.Tools.Debugger;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// run_to 陈旧停点判定（review：单步后立即 run_to 被残留 StepCompleted 吞掉）。纯函数单测。
/// </summary>
public sealed class RunToStaleStopTests
{
    [Fact]
    public void IsStaleStop_ResidualStepOrSameInstance_IsStale()
    {
        var t0 = DateTimeOffset.UtcNow;
        var step = new StopContext(t0, DebugEventKind.StepCompleted, 1, null, "STEP_NORMAL");
        var target = new StopContext(t0, DebugEventKind.BreakpointHit, 1, null, "breakpoint 3", 3);

        // 残留的单步完成事件（run_to 自身不单步）→ 陈旧，须忽略继续等
        Assert.True(DebugRunToTool.IsStaleStop(step, null, DebugSessionState.Stopped));
        // continue 前同一停点实例（缓冲滞后返旧快照）→ 陈旧
        Assert.True(DebugRunToTool.IsStaleStop(target, target, DebugSessionState.Stopped));
        // 目标/其它断点命中 → 非陈旧（应判定）
        Assert.False(DebugRunToTool.IsStaleStop(target, null, DebugSessionState.Stopped));
        // 进程退出 → 交给退出判定，不当陈旧
        Assert.False(DebugRunToTool.IsStaleStop(step, null, DebugSessionState.Exited));
    }
}
