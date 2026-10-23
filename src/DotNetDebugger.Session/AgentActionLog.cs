namespace DotNetDebugger.Session;

/// <summary>一条 agent 调试动作记录（debug_timeline 的「动作」来源）。</summary>
public sealed record AgentAction(long Sequence, DateTimeOffset UtcTimestamp, string Tool, string ArgsSummary, string ResultSummary);

/// <summary>agent 轨迹环形日志（v1 内存上限）。线程安全。</summary>
public sealed class AgentActionLog
{
    public const int MaxEntries = 1000;

    private readonly object _gate = new();
    private readonly Queue<AgentAction> _entries = new();
    private long _seq;

    /// <summary>轨迹变化事件（记录/清空后触发；订阅方自行切线程）。</summary>
    public event Action? Changed;

    public void Log(string tool, string argsSummary, string resultSummary)
    {
        lock (_gate)
        {
            _entries.Enqueue(new AgentAction(
                Interlocked.Increment(ref _seq), DateTimeOffset.UtcNow, tool, argsSummary, resultSummary));
            while (_entries.Count > MaxEntries) _entries.Dequeue();
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<AgentAction> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
        Changed?.Invoke();
    }
}
