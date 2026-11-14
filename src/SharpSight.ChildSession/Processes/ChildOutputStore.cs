using System.Text;
using System.Text.Json;

namespace SharpSight.ChildSession.Processes;

internal sealed class ChildLogQuota
{
    private readonly object _sync = new();
    private long _bytes;
    internal bool Full { get { lock (_sync) return _bytes >= 256L * 1024 * 1024; } }
    internal bool Reserve(int bytes)
    {
        lock (_sync)
        {
            if (_bytes + bytes > 256L * 1024 * 1024) return false;
            _bytes += bytes;
            return true;
        }
    }
}

internal sealed class ChildOutputStore : IAsyncDisposable
{
    private readonly string _directory;
    private readonly ChildLogQuota _quota;
    private readonly SemaphoreSlim _gate = new(1);
    private long _stored;
    private bool _truncated;
    internal ChildOutputStore(string directory, ChildLogQuota quota)
    {
        Directory.CreateDirectory(directory);
        _directory = directory;
        _quota = quota;
    }

    internal async Task AppendAsync(string stream, string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        var record = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { stream, text }) + "\n");
        await _gate.WaitAsync();
        try
        {
            var count = raw.Length + record.Length;
            if (_stored + count > 16 * 1024 * 1024 || !_quota.Reserve(count)) { _truncated = true; return; }
            await using (var file = new FileStream(Path.Combine(_directory, stream + ".log"),
                FileMode.Append, FileAccess.Write, FileShare.Read))
                await file.WriteAsync(raw);
            await using (var file = new FileStream(Path.Combine(_directory, "output.jsonl"),
                FileMode.Append, FileAccess.Write, FileShare.Read))
                await file.WriteAsync(record);
            _stored += count;
        }
        finally { _gate.Release(); }
    }

    internal async Task<object> ReadAsync(long cursor, int limit, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = Path.Combine(_directory, "output.jsonl");
            if (!File.Exists(path)) return new { text = "", nextCursor = 0L, truncated = _truncated };
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (cursor < 0 || cursor > file.Length) throw new ArgumentException("cursor超出输出范围。");
            file.Position = cursor;
            var bytes = new byte[Math.Clamp(limit, 1, 32768) + 4];
            var read = await file.ReadAsync(bytes.AsMemory(0, Math.Min(bytes.Length, checked((int)Math.Min(file.Length - cursor, bytes.Length)))), ct);
            var take = Math.Min(read, Math.Clamp(limit, 1, 32768));
            while (take < read && (bytes[take] & 0xc0) == 0x80) take--;
            if (take == 0 && read > 0)
            {
                take = 1;
                while (take < read && (bytes[take] & 0xc0) == 0x80) take++;
            }
            return new { text = Encoding.UTF8.GetString(bytes, 0, take), nextCursor = cursor + take, truncated = _truncated };
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync() { _gate.Dispose(); return ValueTask.CompletedTask; }
}
