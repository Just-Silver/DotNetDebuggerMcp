using System.Text.Json;
using SharpSight.ChildSession.Contracts;

namespace SharpSight.ChildSession.Requests;

public sealed class RequestJournal(int capacity = 256, TimeProvider? timeProvider = null)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _sequence;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    internal sealed class Entry(ChildRequest request, long sequence, DateTimeOffset acceptedAt)
    {
        internal ChildRequest Request { get; } = request;
        internal long Sequence { get; } = sequence;
        internal DateTimeOffset AcceptedAt { get; } = acceptedAt;
        internal ChildReply Reply = ChildReply.Create(request, "queued", "已排队。");
        internal TaskCompletionSource<ChildReply> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal (Entry Entry, bool Added) Accept(ChildRequest request)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
                throw new ArgumentException("请求标识不能为空。");
            if (_entries.TryGetValue(request.RequestId, out var existing))
            {
                if (existing.Request.Instance != request.Instance || existing.Request.Operation != request.Operation ||
                    existing.Request.Arguments.GetRawText() != request.Arguments.GetRawText())
                    throw new InvalidOperationException("请求标识已用于其他参数，拒绝重放。");
                return (existing, false);
            }
            if (_entries.Count >= capacity)
            {
                var oldest = _entries.Values.Where(e => e.Completion.Task.IsCompleted)
                    .MinBy(e => e.Sequence);
                if (oldest is null) throw new InvalidOperationException("执行中的请求已达上限，请等待后再提交。");
                _entries.Remove(oldest.Request.RequestId);
            }
            var entry = new Entry(request, ++_sequence, _time.GetUtcNow());
            _entries.Add(request.RequestId, entry);
            return (entry, true);
        }
    }

    internal void Set(Entry entry, ChildReply reply, bool complete)
    {
        lock (_sync)
        {
            entry.Reply = reply;
            if (complete) entry.Completion.TrySetResult(reply);
        }
    }

    public ChildReply Find(string requestId)
    {
        lock (_sync)
            return _entries.TryGetValue(requestId, out var entry) ? entry.Reply :
                new("", requestId, "unknown", "未保留该请求，不能据此确认未执行。", JsonSerializer.SerializeToElement(new { }));
    }

    public JsonElement ReadResult(string requestId)
    {
        lock (_sync)
            return JsonSerializer.SerializeToElement(new
            {
                operation = _entries.TryGetValue(requestId, out var entry) ? entry.Request.Operation : "",
                reply = Find(requestId)
            });
    }

    public JsonElement List(long cursor, int limit)
    {
        lock (_sync)
        {
            var page = _entries.Values.Where(e => e.Sequence > cursor)
                .OrderBy(e => e.Sequence).Take(Math.Clamp(limit, 1, 100)).ToArray();
            var next = page.Length == 0 ? cursor : page[^1].Sequence;
            return JsonSerializer.SerializeToElement(new
            {
                items = page.Select(e => new { sequence = e.Sequence, requestId = e.Request.RequestId,
                    operation = e.Request.Operation, status = e.Reply.Status, acceptedAt = e.AcceptedAt,
                    processId = TargetPid(e), taskId = TaskId(e) }),
                nextCursor = next,
                hasMore = _entries.Values.Any(e => e.Sequence > next)
            });
        }
    }

    private static int TargetPid(Entry entry)
    {
        var args = entry.Request.Arguments;
        if (args.ValueKind == JsonValueKind.Object)
        {
            if (args.TryGetProperty("processId", out var pid) && pid.ValueKind == JsonValueKind.Number &&
                pid.TryGetInt32(out var number)) return number;
            if (args.TryGetProperty("process", out var process) && process.ValueKind == JsonValueKind.String &&
                int.TryParse(process.GetString(), out var parsed)) return parsed;
        }
        var data = entry.Reply.Data;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("target", out var target) &&
            target.ValueKind == JsonValueKind.Object && target.TryGetProperty("processId", out var result) &&
            result.ValueKind == JsonValueKind.Number && result.TryGetInt32(out var current)) return current;
        return 0;
    }

    private static string TaskId(Entry entry)
    {
        foreach (var data in new[] { entry.Request.Arguments, entry.Reply.Data })
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("taskId", out var id) &&
                id.ValueKind == JsonValueKind.String) return id.GetString() ?? "";
        return "";
    }
}
