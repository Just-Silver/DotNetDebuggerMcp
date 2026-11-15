using System.Buffers.Binary;
using System.Text.Json;
using SharpSight.ChildSession.Contracts;

namespace SharpSight.ChildSession.Ipc;

internal sealed record ChildEnvelope(int Version, string Secret, ChildPeer Peer, ChildRequest Request);

internal static class ChildWire
{
    internal const int Version = 1;
    internal const int MaximumBytes = 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static bool IsOperation(string operation) => operation is
        "child_session" or "child_process" or "child_pointer" or "child_keyboard" or
        "ui_find" or "ui_action" or "ui_input" or "ui_get" or "ui_wait" or "screenshot";

    /// <summary>
    /// 需要 child 可交互（连接窗非最小化）的操作：只有这些在转发前做不激活还原。
    /// child_session 全部动作与 child_process（含 start）都不还原，避免控制查询/启动程序弹出连接窗。
    /// </summary>
    internal static bool NeedsInteractiveWindow(string operation) => operation is
        "child_pointer" or "child_keyboard" or "ui_find" or "ui_action" or "ui_input" or "ui_get" or
        "ui_wait" or "screenshot";

    internal static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (data.Length > MaximumBytes) throw new InvalidOperationException("通信内容超过1 MiB上限。");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, data.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(data, ct);
        await stream.FlushAsync(ct);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is <= 0 or > MaximumBytes) throw new InvalidOperationException("通信内容长度无效或超过1 MiB。");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return JsonSerializer.Deserialize<T>(data, Json) ?? throw new InvalidOperationException("通信内容为空。");
    }
}
