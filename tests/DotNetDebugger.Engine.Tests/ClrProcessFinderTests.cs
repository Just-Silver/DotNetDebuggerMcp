using System.Text.RegularExpressions;
using DotNetDebugger.Engine.Engine;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>P8：ClrProcessFinder（dbgshim EnumerateCLRs 进程发现）集成测试。</summary>
public sealed class ClrProcessFinderTests
{
    [Fact]
    public async Task List_含运行中的DebugTarget_且排除自身()
    {
        // DebugTarget 启动即加载 CLR；800ms 足够进入可枚举状态
        using var target = DebugTargetProcess.Start("3 6");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var list = ClrProcessFinder.List();

        // 运行中的目标进程被发现，且版本串形如 10.0.9
        var hit = list.FirstOrDefault(p => p.ProcessId == target.Id);
        Assert.NotNull(hit);
        Assert.Equal("DebugTarget", hit!.ProcessName);
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+"), hit.ClrVersion);

        // 调试器自身被排除（attach 自身死锁）
        Assert.DoesNotContain(list, p => p.ProcessId == Environment.ProcessId);
    }
}
