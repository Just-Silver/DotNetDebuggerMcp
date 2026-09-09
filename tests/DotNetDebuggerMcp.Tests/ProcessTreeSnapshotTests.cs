using System.Diagnostics;
using DotNetDebugger.Engine.Engine;
using DotNetDebuggerMcp.Tools.Debugger;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// D2 ProcessTreeSnapshot（kernel32 Toolhelp 父子快照助手）单测：以当前测试进程为父 spawn 真实
/// DebugTarget 子进程，断言 <see cref="ProcessTreeSnapshot.QueryParentMap"/> 快照含 (childPid → 测试进程
/// pid)、<see cref="ProcessTreeSnapshot.DescendantsOf"/> 深度 1 + 真实父 pid；空 parentMap / 空 dotnet
/// 输入降级返回空（P/Invoke 不可用路径的单测代理）。
/// </summary>
public sealed class ProcessTreeSnapshotTests
{
    private static string DebugTargetExe => Path.Combine(
        Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, "DebugTarget.exe");

    [Fact]
    public async Task QueryParentMap_And_DescendantsOf_TrackSpawnedDotnetChildren()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var parentPid = Environment.ProcessId;

        var children = new List<Process>();
        try
        {
            // 以测试进程为父起 2 个 DebugTarget 子进程（delay 20s 提供探测/断言窗口；用完 kill 清理）
            for (var i = 0; i < 2; i++)
            {
                var p = new Process
                {
                    StartInfo = new ProcessStartInfo(exe, "2 20")
                    {
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                Assert.True(p.Start(), $"第 {i + 1} 个子进程启动失败");
                // 排空 stdout/stderr（自起子进程不排空会卡死在管道写入）
                _ = p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                _ = p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                children.Add(p);
            }
            var childPids = children.Select(c => c.Id).ToHashSet();

            // 轮询直到两个子进程都被 ClrProcessFinder（dbgshim 探测）识别为 .NET（CLR 加载有短暂窗口）
            IReadOnlyList<ClrProcessInfo> dotnet = Array.Empty<ClrProcessInfo>();
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                dotnet = ClrProcessFinder.List();
                if (childPids.All(pid => dotnet.Any(d => d.ProcessId == pid))) break;
                await Task.Delay(300, TestContext.Current.CancellationToken);
            }
            Assert.True(childPids.All(pid => dotnet.Any(d => d.ProcessId == pid)),
                $"子进程未被 ClrProcessFinder 识别为 .NET（pid={string.Join(",", childPids)}）。探测到的 .NET 进程：{string.Join(",", dotnet.Select(d => d.ProcessId))}");

            // 快照语义：单次 QueryParentMap 全表应含 (child → 测试进程 pid)
            var map = ProcessTreeSnapshot.QueryParentMap();
            Assert.True(map.Count > 0, "QueryParentMap 快照为空（Toolhelp 失败降级）");
            foreach (var pid in childPids)
            {
                Assert.True(map.TryGetValue(pid, out var ppid), $"快照缺子进程 {pid}");
                Assert.Equal(parentPid, ppid);
            }

            // DescendantsOf：root=测试进程 → 每个子进程 Depth=1、真实父 pid=测试进程
            var desc = ProcessTreeSnapshot.DescendantsOf(parentPid, dotnet, map);
            foreach (var pid in childPids)
            {
                Assert.Contains(desc, d => d.Info.ProcessId == pid && d.Depth == 1 && d.ParentPid == parentPid);
            }
        }
        finally
        {
            foreach (var p in children)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 已退出竞态 */ }
                p.Dispose();
            }
        }
    }

    [Fact]
    public void DescendantsOf_EmptyParentMap_ReturnsEmpty()
    {
        // P/Invoke 快照失败降级路径的单测代理：空 parentMap 输入 → DescendantsOf 返回空
        var dotnet = new[] { new ClrProcessInfo(Environment.ProcessId, "test", "10.0.0") };
        Assert.Empty(ProcessTreeSnapshot.DescendantsOf(1, dotnet, new Dictionary<int, int>()));
    }

    [Fact]
    public void DescendantsOf_EmptyDotnetList_ReturnsEmpty()
    {
        var map = new Dictionary<int, int> { [2] = 1, [3] = 1 };
        Assert.Empty(ProcessTreeSnapshot.DescendantsOf(1, Array.Empty<ClrProcessInfo>(), map));
    }
}
