using DotNetDebugger.Engine.Engine;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 进程发现工具（P8）：列出本机可附加的 .NET 进程（dbgshim EnumerateCLRs 权威探测），
/// 供 agent 选 pid 走 debug_attach。调试器自身已排除。
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
    [Description("列出本机可附加的 .NET 进程（pid、进程名、CLR 版本；调试器自身已排除）。用于找到目标进程 id 后 debug_attach(processId) 附加调试。进程名子串过滤可用 filter。")]
    public static Task<string> DebugProcesses(
        [Description("进程名子串过滤（忽略大小写），缺省空 = 列出全部 .NET 进程。")] string filter = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
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
            var sorted = hits.OrderBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.ProcessId).ToList();
            sb.Append($".NET 进程（{hits.Count} 个）:");
            const int maxShown = 100;
            var shown = sorted.Count > maxShown ? sorted.Take(maxShown).ToList() : sorted;
            foreach (var p in shown)
            {
                sb.Append($"{Environment.NewLine}  pid={p.ProcessId}  {p.ProcessName}  (CLR {p.ClrVersion})");
                if (p.ProcessId == currentPid) sb.Append("  ← 当前会话");
            }
            if (sorted.Count > maxShown)
                sb.Append($"{Environment.NewLine}  … 其余 {sorted.Count - maxShown} 个已省略，用 filter 缩小范围。");
            sb.Append($"{Environment.NewLine}用 debug_attach(processId) 附加调试。");
            return Task.FromResult(sb.ToString());
        }
        catch (Exception ex)
        {
            return Task.FromResult($"列出进程失败：{ex.Message}");
        }
    }
}
