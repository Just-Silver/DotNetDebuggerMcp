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
        // 目标延迟 25s（覆盖慢速 List() 全表枚举——整机数百进程逐个 dbgshim 探测在高负载下可能耗数秒）
        using var target = DebugTargetProcess.Start("3 25");
        Assert.False(target.HasExited);

        // 条件轮询：CLR 加载有短暂窗口，List() 又较慢——不假定固定 800ms 即就绪，找到即止（上限 20s）
        ClrProcessInfo? hit = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && hit is null && !target.HasExited)
        {
            hit = ClrProcessFinder.List().FirstOrDefault(p => p.ProcessId == target.Id);
            if (hit is null) await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        // 运行中的目标进程被发现，且版本串形如 10.0.9
        Assert.NotNull(hit);
        Assert.Equal("DebugTarget", hit!.ProcessName);
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+"), hit.ClrVersion);

        // 调试器自身被排除（attach 自身死锁）
        Assert.DoesNotContain(ClrProcessFinder.List(), p => p.ProcessId == Environment.ProcessId);
    }

    [Fact]
    public void List_不含无CLR进程_不虚报unknown()
    {
        // 本机必有非 .NET 进程（svchost/conhost 等）；dbgshim EnumerateCLRs 对它们返回 S_OK + 空枚举，
        // 此类进程不属「可附加 .NET 进程」，不得记为 CLR "<unknown>"（否则 debug_processes 会把整机
        // 非 .NET 进程虚报为可附加——实测本机 397 条中 390 条为 <unknown>）。
        var list = ClrProcessFinder.List();

        Assert.DoesNotContain(list, p => p.ClrVersion == "<unknown>");
    }
}
