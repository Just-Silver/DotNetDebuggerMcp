using System.Text.Json;

namespace SharpSight.ChildSession.Contracts;

public sealed record ChildReply(
    string Instance, string RequestId, string Status, string Message, JsonElement Data)
{
    public static ChildReply Create(ChildRequest request, string status, string message, object? data = null)
        => new(request.Instance, request.RequestId, status, message,
            JsonSerializer.SerializeToElement(data ?? new { }));
}
