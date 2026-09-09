using DotNetDebugger.Session;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 调试时间线工具：目标日志（log）+ 调试事件（bp/step/exc/skip/trc/state/engine）+ agent 动作（act）
/// 三源按时间归并输出，供复盘「先 A 日志 → 命中 B 断点 → agent 改值 → 再 C 异常」因果链。
/// </summary>
[McpServerToolType]
public static class DebugTimelineTool
{
    /// <summary>
    /// 查看当前调试会话的日志+事件+agent动作统一时间线。三源各自保留内存环形缓冲（输出 2000 行 / 事件 500 条 / 动作 1000 条），
    /// 拉取时按 UTC 归并排序（同毫秒按源内序号稳定）；取最近 N 条（默认 100，不做 start-end 翻页）；
    /// filter 子串过滤（忽略大小写）；kind 按源筛选。
    /// </summary>
    [McpServerTool]
    [Description("查看当前调试会话的日志+事件+agent动作统一时间线（按时间对齐复盘：先 A 日志→命中 B 断点→agent 改值→再 C 异常）。三源：目标进程输出（log，仅 launch 会话有）、调试事件（bp/step/exc/skip/trc/state/engine）、agent 动作（act）。lines 取最近 N 条（默认 100，v1 不做 start-end 翻页）；filter 子串过滤（忽略大小写）；kind 可选 all/log/act/bp/step/exc/skip/trc/state/engine 按源筛选。")]
    public static async Task<string> DebugTimeline(
        [Description("取最近 N 条（默认 100）。")] int lines = 100,
        [Description("子串过滤（忽略大小写），只保留文本含该子串的行。")] string filter = "",
        [Description("按源筛选：all/log/act/bp/step/exc/skip/trc/state/engine。")] string kind = "all",
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";
        var validKinds = new[] { "all", "log", "act", "bp", "step", "exc", "skip", "trc", "state", "engine" };
        var kindNorm = kind.Trim().ToLowerInvariant();
        if (!validKinds.Contains(kindNorm))
            return $"kind 无效：{kind}（可选 {string.Join("/", validKinds)}）。";
        var n = Math.Clamp(lines <= 0 ? 100 : lines, 1, 1000);

        var logs = active.Output?.Snapshot() ?? Array.Empty<DotNetDebugger.Session.ProcessOutputLine>();
        var history = active.Buffer.SnapshotHistory();
        var actions = active.Actions.Snapshot();
        var rows = DotNetDebugger.Session.DebugTimeline.Build(logs, history, actions, n, filter, kindNorm);

        var sb = new StringBuilder();
        var target = active.ProcessId > 0 ? $"目标 pid={active.ProcessId}" : "目标 ?";
        sb.Append($"时间线 {target}（共 {logs.Count} 日志 / {history.Count} 事件 / {actions.Count} 动作 → 显示 {rows.Count} 行）:");
        if (rows.Count > 0)
        {
            sb.AppendLine();
            sb.Append($"范围 {rows[0].Utc.ToLocalTime():HH:mm:ss.fff} – {rows[^1].Utc.ToLocalTime():HH:mm:ss.fff}"); // 显示统一本机时刻（源混用 Now/UtcNow 但 DTO 比较按瞬时）
        }
        // attach 即成功提示（顺手项②）：attach 到长活目标且进程在跑——不看起来像卡住
        if (active.IsAttach && active.Buffer.CurrentState == DotNetDebugger.Engine.Models.DebugSessionState.Running)
        {
            sb.AppendLine();
            sb.Append("attach 会话：已附加成功，目标进程继续独立运行（不会自行停下）；设断点后 debug_continue/debug_wait 等待命中。");
        }
        // attach 会话退出（顺手项①，对齐 debug_state）：退出码不可得
        if (active.IsAttach && active.Buffer.CurrentState == DotNetDebugger.Engine.Models.DebugSessionState.Exited)
            sb.AppendLine().Append("（attach 会话：退出码不可得——ICorDebug 不提供，目标独立于调试器退出）");
        DebugSessionService.Manager.Actions.Log("debug_timeline", $"lines={n} filter={filter} kind={kindNorm}", $"{rows.Count} 行"); // 轨迹惯例
        foreach (var row in rows)
        {
            sb.AppendLine();
            sb.Append($"[{row.Utc.ToLocalTime():HH:mm:ss.fff}] {row.Tag,-6} {row.Text}");
        }
        return sb.ToString();
    }
}
