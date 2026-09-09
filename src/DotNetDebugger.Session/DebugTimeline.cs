using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Session;

/// <summary>统一时间线行（三源归并后的统一输出单元）。</summary>
public sealed record TimelineRow(DateTimeOffset Utc, long Seq, string Tag, string Text);

/// <summary>
/// 统一时间线行渲染：三源（目标日志 ProcessOutputCapture / 调试事件历史 SessionEventBuffer / agent 动作 AgentActionLog）
/// 合并（时间主序；同毫秒用 TagRank+Seq 稳定次序），filter/kind/maxLines 后返回统一行。纯内存归并，无后台任务。
/// 行文本格式：[HH:mm:ss.fff] tag 内容（tag 两字母：log/act/bp/step/exc/skip/trc/state/engine）。
/// </summary>
public static class DebugTimeline
{
    private static readonly string[] TagOrder = ["log", "act", "bp", "step", "exc", "skip", "trc", "state", "engine"];

    public static IReadOnlyList<TimelineRow> Build(
        IReadOnlyList<ProcessOutputLine> logs, IReadOnlyList<DebugEvent> history,
        IReadOnlyList<AgentAction> actions, int maxLines = 100, string? filter = null, string kind = "all")
    {
        var rows = new List<TimelineRow>();
        if (kind is "all" or "log")
            foreach (var l in logs)
                rows.Add(new TimelineRow(l.Timestamp, l.Sequence, "log", l.Text));
        if (kind is "all" or "act")
            foreach (var a in actions)
                rows.Add(new TimelineRow(a.UtcTimestamp, a.Sequence, "act", $"{a.Tool} {a.ArgsSummary}".Trim()));
        if (kind is "all" or "bp" or "step" or "exc" or "skip" or "trc" or "state" or "engine")
            foreach (var e in history)
            {
                var tag = TagOf(e.Kind);
                if (tag is null || (kind != "all" && tag != kind)) continue;
                rows.Add(new TimelineRow(e.UtcTimestamp, e.Sequence, tag, Summarize(e)));
            }

        var sorted = rows
            .OrderBy(r => r.Utc)
            .ThenBy(r => Array.IndexOf(TagOrder, r.Tag))   // 同 Utc：按 TagOrder 序号稳定
            .ThenBy(r => r.Seq)
            .ToList();
        if (!string.IsNullOrWhiteSpace(filter))
            sorted = sorted.Where(r => r.Text.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var skip = Math.Max(0, sorted.Count - Math.Max(1, maxLines));
        return sorted.Skip(skip).ToArray();
    }

    private static string? TagOf(DebugEventKind k) => k switch
    {
        DebugEventKind.BreakpointHit => "bp",
        DebugEventKind.StepCompleted => "step",
        DebugEventKind.ExceptionHit => "exc",
        DebugEventKind.ExceptionSkipped => "skip",
        DebugEventKind.TraceHit => "trc",
        DebugEventKind.SessionStateChanged => "state",
        DebugEventKind.EngineLog => "engine",
        _ => null, // BreakpointConditionFailed/ThreadsChanged/BreakpointsChanged 不入历史不渲染
    };

    private static string Summarize(DebugEvent e) => e.Kind switch
    {
        DebugEventKind.SessionStateChanged when e.Payload is SessionStateChangedPayload sp => sp.State.ToString() + (string.IsNullOrEmpty(sp.Reason) ? "" : $"（{sp.Reason}）"),
        DebugEventKind.BreakpointHit when e.Payload is BreakpointHitPayload bp =>
            $"bp{bp.BreakpointId} @ {Loc(bp.TopFrame)} (t={bp.ThreadId})",
        DebugEventKind.StepCompleted when e.Payload is StepCompletedPayload sc => $"step {sc.Reason} @ {Loc(sc.TopFrame)} (t={sc.ThreadId})",
        DebugEventKind.ExceptionHit when e.Payload is ExceptionHitPayload ex =>
            $"exception {ex.ExceptionType}" + (string.IsNullOrEmpty(ex.Message) ? "" : $": {ex.Message}") + $" @ {Loc(ex.TopFrame)}",
        DebugEventKind.ExceptionSkipped when e.Payload is ExceptionSkippedPayload sk => $"skip {sk.ExceptionType}（过滤器放行）",
        DebugEventKind.TraceHit when e.Payload is TraceHitPayload th =>
            $"bp{th.BreakpointId} t={th.ThreadId} {string.Join(" ", th.Variables.Take(3).Select(v => $"{v.Scope}.{v.Name ?? "slot" + v.Slot}={v.Display}"))}" + (th.Variables.Count > 3 ? " …" : ""),
        DebugEventKind.EngineLog when e.Payload is EngineLogPayload el => $"[{el.Level}] {el.Message}",
        _ => e.Kind.ToString(),
    };

    private static string Loc(FrameLocation? f) => f is null ? "?" : $"{f.ModuleName}!{f.MethodTokenText}+0x{f.IlOffset:x}";
}
