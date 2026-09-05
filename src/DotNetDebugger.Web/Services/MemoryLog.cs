namespace DotNetDebugger.Web.Services;

/// <summary>一条内存日志。Level/Message/UtcTimestamp 供 /logs 页展示。</summary>
public sealed record MemoryLogEntry(DateTimeOffset UtcTimestamp, string Source, string Message);

/// <summary>
/// 进程内环形内存日志（联调诊断用，不持久化）：Web 组件/服务把关键路径打点写到这，
/// /logs 页面实时读最近条目。上限 <see cref="MaxEntries"/> 条，超限丢最旧。线程安全（宿主工具线程与 Blazor 渲染线程并发写）。
/// </summary>
public static class MemoryLog
{
    public const int MaxEntries = 2000;

    private static readonly object _gate = new();
    private static readonly Queue<MemoryLogEntry> _entries = new();
    private static long _seq;

    /// <summary>日志变化事件（写入/清空后触发；订阅方自行切线程）。LogPanel 推送通道——Web 零轮询铁律，禁止定时器拉取。</summary>
    public static event Action? Changed;

    /// <summary>写一条日志（时间戳自动取当前 UTC）。</summary>
    public static void Write(string source, string message)
    {
        lock (_gate)
        {
            _entries.Enqueue(new MemoryLogEntry(DateTimeOffset.UtcNow, source, message));
            while (_entries.Count > MaxEntries) _entries.Dequeue();
            _seq++;
        }
        Changed?.Invoke();
    }

    /// <summary>当前日志快照（新的在后）。线程安全。</summary>
    public static IReadOnlyList<MemoryLogEntry> Snapshot()
    {
        lock (_gate) return _entries.ToArray();
    }

    /// <summary>清空（联调时刷新看新日志用）。</summary>
    public static void Clear()
    {
        lock (_gate) _entries.Clear();
        Changed?.Invoke();
    }

    public static long Count { get { lock (_gate) return _seq; } }
}
