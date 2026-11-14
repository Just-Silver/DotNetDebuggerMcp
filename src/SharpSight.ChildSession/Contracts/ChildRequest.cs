using System.Text.Json;

namespace SharpSight.ChildSession.Contracts;

public sealed record ChildRequest(
    string Instance, string RequestId, string Operation,
    JsonElement Arguments, DateTimeOffset Deadline);
