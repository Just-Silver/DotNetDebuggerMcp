using System.Diagnostics;
using ClrDebug;

namespace DotNetDebugger.Engine.Engine;

/// <summary>一个可调试的 .NET 进程（P8 进程发现）。</summary>
public sealed record ClrProcessInfo(int ProcessId, string ProcessName, string ClrVersion);

/// <summary>
/// .NET 进程发现（P8）：枚举系统进程，经 dbgshim EnumerateCLRs 逐个探测已加载 CLR——「可被 ICorDebug 附加」的
/// 权威判定（非 .NET/无 CLR 进程该调用快速失败）。排除调试器自身（attach 自身必然死锁）。
/// EnumerateCLRs 成功后须 CloseCLREnumeration 释放句柄与内存（ClrDebug 文档约束）。
/// 与引擎无会话关联：纯发现入口，可在无活动会话时调用；线程安全（每次调用独立加载 DbgShim 实例句柄）。
/// </summary>
public static class ClrProcessFinder
{
    /// <summary>列出本机可附加的 .NET 进程（按 pid 升序）。单个进程探测失败静默跳过（权限/竞态常见，不虚报）。</summary>
    public static IReadOnlyList<ClrProcessInfo> List()
    {
        var currentPid = Environment.ProcessId;
        var results = new List<ClrProcessInfo>();

        // DbgShim 句柄一次加载复用（枚举为逐 pid 的轻量远程查询）
        using var shimRef = new DbgShimRef(DbgShimLoader.Load());

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentPid) continue; // 调试器自身：排除（attach 自身死锁）
                if (process.HasExited) continue;

                if (shimRef.Shim.TryEnumerateCLRs(process.Id, out var result) != HRESULT.S_OK)
                    continue; // 非 .NET 进程 / CLR 未加载：快速失败跳过

                try
                {
                    // 取首个 CLR 版本串（多 CLR 同进程极罕见）；串形如 .../Microsoft.NETCore.App/10.0.9/System.Private.CoreLib.dll
                    var clrPath = result.Items.FirstOrDefault().Path ?? "";
                    var version = ExtractVersion(clrPath);
                    results.Add(new ClrProcessInfo(process.Id, process.ProcessName, version));
                }
                finally
                {
                    shimRef.Shim.TryCloseCLREnumeration(result);
                }
            }
            catch
            {
                // 权限不足/进程已退出等竞态：静默跳过
            }
            finally
            {
                process.Dispose();
            }
        }

        return results.OrderBy(p => p.ProcessId).ToList();
    }

    /// <summary>从 CLR 路径提取版本段（如 10.0.9）；解析不出返回原文末段。</summary>
    private static string ExtractVersion(string clrPath)
    {
        if (string.IsNullOrEmpty(clrPath)) return "<unknown>";
        var segments = clrPath.Replace('/', '\\').Split('\\');
        var version = segments.TakeWhile(s => s != "Microsoft.NETCore.App").Count() < segments.Length
            ? segments[Array.IndexOf(segments, "Microsoft.NETCore.App") + 1]
            : segments[^1];
        return version;
    }

    /// <summary>DbgShim 句柄持有者（List 单次调用生命周期内复用；暂无 Release 导出需求，进程退出自动释放）。</summary>
    private sealed class DbgShimRef(DbgShim shim) : IDisposable
    {
        public DbgShim Shim { get; } = shim;

        public void Dispose()
        {
            // dbgshim 库句柄无显式 Release 导出；NativeLibrary 句柄随进程生命周期，无需释放
        }
    }
}
