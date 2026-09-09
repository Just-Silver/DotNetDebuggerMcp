using DotNetDebugger.Engine.Models;
using System.Globalization;
using Xunit;

namespace DotNetDebugger.Session.Tests;

/// <summary>
/// DebugTimeline 三源归并纯内存测试（无进程，快速）：时间主序合并 log/事件/act、
/// filter 子串、kind 按源筛选、maxLines 取最近 N 且仍时间升序、同毫秒按 TagRank+Seq 稳定。
/// </summary>
public sealed class DebugTimelineTests
{
    [Fact]
    public void Build_MergesThreeSourcesChronologically()
    {
        var logs = new[] { new ProcessOutputLine(1, T("2026-09-09T12:00:01"), ProcessOutputStream.Stdout, "listening :5000") };
        var history = new[] { new DebugEvent("s", 1, T("2026-09-09T12:00:03"), DebugEventKind.BreakpointHit,
            new BreakpointHitPayload(3, 8, null)) };
        var actions = new[] { new AgentAction(1, T("2026-09-09T12:00:02"), "debug_evaluate", "i == 3", "ok") };
        var rows = DebugTimeline.Build(logs, history, actions, maxLines: 100);
        Assert.Equal(3, rows.Count);
        Assert.Equal(["log", "act", "bp"], rows.Select(r => r.Tag));      // 时间升序
        Assert.Contains(rows, r => r.Text.Contains("listening"));
        Assert.Contains(rows, r => r.Text.Contains("bp3"));               // 事件摘要
        Assert.Contains(rows, r => r.Text.Contains("debug_evaluate"));
    }

    [Fact]
    public void Build_FilterSubstring_OnlyKeepsMatches()
    {
        var logs = new[]
        {
            new ProcessOutputLine(1, T("2026-09-09T12:00:01"), ProcessOutputStream.Stdout, "listening :5000"),
            new ProcessOutputLine(2, T("2026-09-09T12:00:02"), ProcessOutputStream.Stdout, "WorkScores computing"),
        };
        var history = new[] { new DebugEvent("s", 1, T("2026-09-09T12:00:03"), DebugEventKind.BreakpointHit,
            new BreakpointHitPayload(3, 8, null)) };
        var rows = DebugTimeline.Build(logs, history, [], maxLines: 100, filter: "WorkScores");
        Assert.Single(rows);
        Assert.Equal("log", rows[0].Tag);
        Assert.Contains("WorkScores computing", rows[0].Text);
    }

    [Fact]
    public void Build_KindSelectsSource()
    {
        var logs = new[] { new ProcessOutputLine(1, T("2026-09-09T12:00:01"), ProcessOutputStream.Stdout, "log-line-1") };
        var history = new DebugEvent[]
        {
            new("s", 1, T("2026-09-09T12:00:02"), DebugEventKind.SessionStateChanged, new SessionStateChangedPayload(DebugSessionState.Running, null)),
            new("s", 2, T("2026-09-09T12:00:03"), DebugEventKind.BreakpointHit, new BreakpointHitPayload(1, 8, null)),
            new("s", 3, T("2026-09-09T12:00:04"), DebugEventKind.StepCompleted, new StepCompletedPayload(8, null, "into")),
            new("s", 4, T("2026-09-09T12:00:05"), DebugEventKind.ExceptionHit, new ExceptionHitPayload(8, "System.Exception", "boom", null)),
            new("s", 5, T("2026-09-09T12:00:06"), DebugEventKind.ExceptionSkipped, new ExceptionSkippedPayload(8, "System.IO.IOException", "noise")),
            new("s", 6, T("2026-09-09T12:00:07"), DebugEventKind.TraceHit, new TraceHitPayload(5, 8, T("2026-09-09T12:00:07"), null, [])),
            new("s", 7, T("2026-09-09T12:00:08"), DebugEventKind.EngineLog, new EngineLogPayload("info", "engine-hello")),
        };
        var actions = new[] { new AgentAction(1, T("2026-09-09T12:00:09"), "debug_state", "", "running") };

        foreach (var kind in new[] { "log", "act", "bp", "step", "exc", "skip", "trc", "state", "engine" })
        {
            var rows = DebugTimeline.Build(logs, history, actions, maxLines: 100, kind: kind);
            Assert.NotEmpty(rows);
            Assert.All(rows, r => Assert.Equal(kind, r.Tag));
        }
        // 全量：三源总数
        Assert.Equal(logs.Length + history.Length + actions.Length, DebugTimeline.Build(logs, history, actions, maxLines: 100).Count);
    }

    [Fact]
    public void Build_TakesLastMaxLinesChronological()
    {
        var logs = Enumerable.Range(1, 10)
            .Select(i => new ProcessOutputLine(i, T($"2026-09-09T12:00:{i:00}"), ProcessOutputStream.Stdout, $"log-{i}"))
            .ToArray();
        var rows = DebugTimeline.Build(logs, [], [], maxLines: 3);
        Assert.Equal(3, rows.Count);
        // 截最近 3 条且仍时间升序
        Assert.Equal(["log-8", "log-9", "log-10"], rows.Select(r => r.Text).ToArray());
    }

    [Fact]
    public void Build_SameMillisecondStableBySeq()
    {
        var utc = T("2026-09-09T12:00:00");
        // 同 Utc 打平：纯 Seq 序应为 bp(1)/act(2)/log(3)——TagOrder 优先则 log/act/bp
        var logs = new[] { new ProcessOutputLine(3, utc, ProcessOutputStream.Stdout, "log-3") };
        var history = new[] { new DebugEvent("s", 1, utc, DebugEventKind.BreakpointHit, new BreakpointHitPayload(9, 8, null)) };
        var actions = new[] { new AgentAction(2, utc, "debug_continue", "", "ok") };
        var rows = DebugTimeline.Build(logs, history, actions, maxLines: 100);
        Assert.Equal(["log", "act", "bp"], rows.Select(r => r.Tag).ToArray());
    }

    /// <summary>本地测试助手：按 ISO 文本解析为 DateTimeOffset（RoundtripKind 保偏移）。</summary>
    private static DateTimeOffset T(string s)
        => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
