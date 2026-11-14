using System.Text.Json;

namespace SharpSight.ChildSession.Observations;

public sealed class ObservationRegistry
{
    private readonly Dictionary<string, ObservationSource> _records = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _sync = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Register(JsonElement source)
    {
        var record = Read(source);
        var id = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            _records.Add(id, record);
            _order.Enqueue(id);
            while (_order.Count > 256) _records.Remove(_order.Dequeue());
        }
        return id;
    }

    public JsonElement SourceFor(string observationId)
    {
        lock (_sync)
            return JsonSerializer.SerializeToElement(Find(observationId), Json);
    }

    public void Validate(string observationId, JsonElement current, int frameId)
    {
        if (string.IsNullOrEmpty(observationId)) return;
        var now = Read(current);
        lock (_sync)
        {
            var old = Find(observationId);
            if (old.Instance != now.Instance || old.WorkerPid != now.WorkerPid ||
                old.WorkerStartedTicks != now.WorkerStartedTicks || old.SessionId != now.SessionId)
                throw new InvalidOperationException("观察不属于当前实例或worker代际，请重新观察。");
            if (old.Layout != now.Layout || (old.ProcessId != 0 &&
                (old.ProcessId != now.ProcessId || old.StartedTicks != now.StartedTicks ||
                 old.Hwnd != now.Hwnd || old.Geometry != now.Geometry)))
                throw new InvalidOperationException("观察的目标身份、窗口几何或显示布局已变化，请重新观察。");
            if (now.Kind == "uia-index" && old.Kind != "uia")
                throw new InvalidOperationException("index只能消费UIA清单观察，不能使用视觉截图引用。");
            if ((old.Kind is "uia" or "element" && old.FrameId != now.FrameId) ||
                (frameId != 0 && (old.Kind is not ("uia" or "element") || old.FrameId != frameId)))
                throw new InvalidOperationException("观察与当前UIA帧不同源或已过期，请重新ui_find。");
        }
    }

    private ObservationSource Find(string id)
        => _records.TryGetValue(id, out var value) ? value :
            throw new InvalidOperationException("未保留该观察或它属于其他实例，请重新观察。");

    private static ObservationSource Read(JsonElement source)
    {
        var record = source.Deserialize<ObservationSource>(Json)
            ?? throw new InvalidOperationException("观察来源不可用。");
        if (string.IsNullOrWhiteSpace(record.Instance) || record.WorkerPid <= 0 ||
            record.WorkerStartedTicks <= 0 || record.SessionId <= 0 ||
            record.Kind is not ("uia" or "uia-index" or "element" or "window" or "screen" or "input"))
            throw new InvalidOperationException("观察来源身份无效。");
        return record;
    }

    private sealed record ObservationSource(string Kind, string Instance, int WorkerPid,
        long WorkerStartedTicks, int SessionId, int ProcessId, long StartedTicks,
        string Hwnd, string Geometry, string Layout, int FrameId);
}
