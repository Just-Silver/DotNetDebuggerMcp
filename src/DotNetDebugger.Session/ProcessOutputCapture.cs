namespace DotNetDebugger.Session;

/// <summary>目标输出流别。</summary>
public enum ProcessOutputStream
{
    Stdout,
    Stderr,
}

/// <summary>一行目标输出（序号全局递增；Timestamp 在 Append 入口锁外取本机时刻，供与断点/异常命中对时）。</summary>
public sealed record ProcessOutputLine(int Sequence, DateTimeOffset Timestamp, ProcessOutputStream Stream, string Text);

/// <summary>
/// 目标进程 stdout/stderr 环形缓冲。DataReceived 回调在线程池线程，全部经锁串行化；
/// 超 <see cref="MaxLines"/> 逐最旧。进程退出标记行由管理器经 <see cref="AppendSystem"/> 追加。
/// </summary>
public sealed class ProcessOutputCapture
{
    /// <summary>缓冲上限（行），超出丢最旧。2000 行约数百 KB——Web 应用启动轮询日志下仍能保留更久早期关键行（R8①）。</summary>
    public const int MaxLines = 2000;

    private readonly object _gate = new();
    private readonly Queue<ProcessOutputLine> _lines = new();
    private int _sequence;

    /// <summary>当前缓冲行数。</summary>
    public int Count { get { lock (_gate) return _lines.Count; } }

    /// <summary>追加一行输出；空行忽略。时刻在入口（锁外）取——防排队等锁把时刻扭曲到入队时。</summary>
    public void Append(ProcessOutputStream stream, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var timestamp = DateTimeOffset.Now;
        lock (_gate)
        {
            _sequence++;
            _lines.Enqueue(new ProcessOutputLine(_sequence, timestamp, stream, text));
            while (_lines.Count > MaxLines) _lines.Dequeue();
        }
    }

    /// <summary>追加系统标记行（如进程退出），按 stdout 流别记录。</summary>
    public void AppendSystem(string text) => Append(ProcessOutputStream.Stdout, text);

    /// <summary>全量快照（旧→新；debug_timeline 归并用，Tail 保留给既有语义）。</summary>
    public IReadOnlyList<ProcessOutputLine> Snapshot()
    {
        lock (_gate) return _lines.ToArray();
    }

    /// <summary>取尾部至多 maxLines 行（旧→新排序）。filter 非空时只保留文本含该子串（忽略大小写）的行，
    /// 仍从尾部向上取够 maxLines 条命中行——高频噪声日志场景下可筛出关键行（R8①）。</summary>
    public IReadOnlyList<ProcessOutputLine> Tail(int maxLines, string? filter = null)
    {
        lock (_gate)
        {
            IEnumerable<ProcessOutputLine> src = _lines;
            if (!string.IsNullOrWhiteSpace(filter))
                src = src.Where(l => l.Text.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));
            var list = src.ToList();
            var skip = Math.Max(0, list.Count - Math.Max(0, maxLines));
            return list.Skip(skip).ToArray();
        }
    }
}
