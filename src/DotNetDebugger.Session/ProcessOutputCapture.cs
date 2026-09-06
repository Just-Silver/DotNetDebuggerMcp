namespace DotNetDebugger.Session;

/// <summary>目标输出流别。</summary>
public enum ProcessOutputStream
{
    Stdout,
    Stderr,
}

/// <summary>一行目标输出（序号全局递增，供工具层展示与排序）。</summary>
public sealed record ProcessOutputLine(int Sequence, ProcessOutputStream Stream, string Text);

/// <summary>
/// 目标进程 stdout/stderr 环形缓冲。DataReceived 回调在线程池线程，全部经锁串行化；
/// 超 <see cref="MaxLines"/> 逐最旧。进程退出标记行由管理器经 <see cref="AppendSystem"/> 追加。
/// </summary>
public sealed class ProcessOutputCapture
{
    /// <summary>缓冲上限（行），超出丢最旧。</summary>
    public const int MaxLines = 500;

    private readonly object _gate = new();
    private readonly Queue<ProcessOutputLine> _lines = new();
    private int _sequence;

    /// <summary>当前缓冲行数。</summary>
    public int Count { get { lock (_gate) return _lines.Count; } }

    /// <summary>追加一行输出；空行忽略。</summary>
    public void Append(ProcessOutputStream stream, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            _sequence++;
            _lines.Enqueue(new ProcessOutputLine(_sequence, stream, text));
            while (_lines.Count > MaxLines) _lines.Dequeue();
        }
    }

    /// <summary>追加系统标记行（如进程退出），按 stdout 流别记录。</summary>
    public void AppendSystem(string text) => Append(ProcessOutputStream.Stdout, text);

    /// <summary>取尾部至多 maxLines 行（旧→新排序）。</summary>
    public IReadOnlyList<ProcessOutputLine> Tail(int maxLines)
    {
        lock (_gate)
        {
            var skip = Math.Max(0, _lines.Count - Math.Max(0, maxLines));
            return _lines.Skip(skip).ToArray();
        }
    }
}
