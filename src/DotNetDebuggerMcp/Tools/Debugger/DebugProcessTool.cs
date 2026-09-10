using DotNetDebugger.Engine.Engine;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 进程发现工具（P8）：列出本机可附加的 .NET 进程（dbgshim EnumerateCLRs 权威探测），
/// 供 agent 选 pid 走 debug_attach。调试器自身已排除。D2：存在当前会话目标时，
/// 其 .NET 子孙进程行尾标注父链 + 返回尾部附切换引导（单活动会话边界）。
/// </summary>
[McpServerToolType]
public static class DebugProcessTool
{
    /// <summary>
    /// 列出本机可附加的 .NET 进程（pid、进程名、CLR 版本）。
    /// </summary>
    /// <param name="filter">进程名子串过滤（忽略大小写）；缺省空 = 列出全部。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进程列表文本或无结果提示。</returns>
    [McpServerTool]
    [Description("列出本机可附加的 .NET 进程（pid、进程名、CLR 版本；调试器自身已排除）。用于找到目标进程 id 后 debug_attach(processId) 附加调试。进程名子串过滤可用 filter。存在当前会话目标时，其 .NET 子孙进程行尾标注「← 会话目标(X) 的子进程/第N代孙进程（父 Y）」并附切换引导（单活动会话：需停当前会话后 debug_attach 子进程单独调试）。")]
    public static Task<string> DebugProcesses(
        [Description("进程名子串过滤（忽略大小写），缺省空 = 列出全部 .NET 进程。")] string filter = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            // ClrProcessFinder 已在 Engine 侧跳过无 CLR 进程（EnumerateCLRs 对它们返回 S_OK + 空枚举），
            // 这里直接使用即可得到「可附加 .NET 进程」全集（D2 子进程链交集同源）。
            var all = ClrProcessFinder.List();
            var hits = string.IsNullOrWhiteSpace(filter)
                ? all
                : all.Where(p => p.ProcessName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

            if (hits.Count == 0)
                return Task.FromResult(string.IsNullOrWhiteSpace(filter)
                    ? "未发现可附加的 .NET 进程（可先启动目标再刷新）。"
                    : $"未发现进程名含 \"{filter.Trim()}\" 的 .NET 进程（当前共 {all.Count} 个 .NET 进程）。");

            var sb = new StringBuilder();
            var currentPid = DebugSessionService.Manager.Active?.ProcessId ?? 0;
            // D2：当前会话目标存在时，Toolhelp 快照其 .NET 子孙进程链（只读标注，不做多会话并行）
            var parentMap = currentPid > 0 ? ProcessTreeSnapshot.QueryParentMap() : new Dictionary<int, int>();
            var chain = currentPid > 0
                ? ProcessTreeSnapshot.DescendantsOf(currentPid, all, parentMap).ToDictionary(d => d.Info.ProcessId)
                : new Dictionary<int, (ClrProcessInfo Info, int Depth, int ParentPid)>();
            var sorted = hits.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProcessId).ToList();
            sb.Append($".NET 进程（{hits.Count} 个）:");
            const int maxShown = 100;
            var shown = sorted.Count > maxShown ? sorted.Take(maxShown).ToList() : sorted;
            foreach (var p in shown)
            {
                sb.Append($"{Environment.NewLine}  pid={p.ProcessId}  {p.ProcessName}  (CLR {p.ClrVersion})");
                if (p.ProcessId == currentPid)
                    sb.Append("  ← 当前会话");
                else if (chain.TryGetValue(p.ProcessId, out var node))
                    sb.Append($"  ← 会话目标({currentPid}) 的{(node.Depth == 1 ? "子进程" : "第" + node.Depth + "代孙进程")}（父 {node.ParentPid}）");
            }
            if (sorted.Count > maxShown)
                sb.Append($"{Environment.NewLine}  … 其余 {sorted.Count - maxShown} 个已省略，用 filter 缩小范围。");
            sb.Append($"{Environment.NewLine}用 debug_attach(processId) 附加调试。");
            if (chain.Count > 0)
                sb.Append($"{Environment.NewLine}发现 {chain.Count} 个会话目标的 .NET 子进程链——业务代码在子进程时需停当前会话（debug_disconnect/停断点）后 debug_attach <childPid> 单独调试（子进程输出不在 debug_output 范围）。");
            if (currentPid > 0 && parentMap.Count == 0)
                sb.Append($"{Environment.NewLine}无法判定父子关系（进程快照失败），子进程链标注不可用。");
            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex)
        {
            return Task.FromResult($"列出进程失败：{ex.Message}");
        }
    }
}
