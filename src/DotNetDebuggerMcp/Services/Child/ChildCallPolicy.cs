using System.Text.Json;
using DotNetDebuggerMcp.Configuration;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Processes;

namespace DotNetDebuggerMcp.Services.Child;

internal sealed class ChildCallPolicy(FeatureFamily enabled,
    Func<ChildRequest, CancellationToken, Task<ChildReply>> send,
    Func<string, CancellationToken, Task<bool>>? attachedToInstance = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<ChildReply> CallAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        var request = new ChildRequest("", Guid.NewGuid().ToString("N"), operation, arguments, DateTimeOffset.UtcNow.AddSeconds(5));
        try
        {
            if (!IsOperation(operation)) throw new ArgumentException("未知子会话操作，拒绝执行。");
            if (!Allowed(operation)) throw new InvalidOperationException("子会话能力未启用（需child及对应操作族）。");
            if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数必须是JSON对象。");
            var instance = Text(arguments, "instance");
            var action = Text(arguments, "action", operation == "child_session" ? "status" : "");
            if (instance.Length == 0 && (operation != "child_session" || action is not ("probe" or "start" or "status")))
                throw new ArgumentException("必须显式给出instance，不会回退主会话。");
            var timeout = Timeout(operation, action, arguments);
            request = request with
            {
                Instance = instance, Deadline = DateTimeOffset.UtcNow.AddSeconds(timeout == 0 ? 5 : timeout),
                Arguments = Normalize(operation, arguments)
            };
            if (operation == "child_session" && action == "result") return await ResultAsync(request, timeout, ct);
            if (operation == "child_session" && action == "stop" && attachedToInstance is not null &&
                await attachedToInstance(instance, ct))
                return ChildReply.Create(request, "not-executed", "本服务器仍附加该child的调试目标，请先debug_session disconnect。");
            var reply = await send(request, ct);
            return operation == "child_session" && action == "requests" ? FilterRequests(reply) : reply;
        }
        catch (OperationCanceledException) { return ChildReply.Create(request, "unknown", "已放弃等待，请按instance用requests/result核对，不自动重放。"); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", ex.Message); }
    }

    internal static bool IsOperation(string operation) => operation is
        "child_session" or "child_process" or "child_pointer" or "child_keyboard" or
        "ui_find" or "ui_action" or "ui_input" or "ui_get" or "ui_wait" or "screenshot";

    private bool Allowed(string operation)
    {
        var required = operation switch
        {
            "ui_find" or "ui_action" or "ui_input" or "ui_get" or "ui_wait" => FeatureFamily.Child | FeatureFamily.Ui,
            "screenshot" => FeatureFamily.Child | FeatureFamily.Screenshot,
            _ => FeatureFamily.Child
        };
        return IsOperation(operation) && enabled.HasFlag(required);
    }

    private async Task<ChildReply> ResultAsync(ChildRequest request, int timeout, CancellationToken ct)
    {
        var wanted = Text(request.Arguments, "requestId");
        if (wanted.Length == 0) throw new ArgumentException("result必须给出原请求requestId。");
        var operation = await FindOperationAsync(request, wanted, ct);
        if (operation.Length == 0) return ChildReply.Create(request with { RequestId = wanted }, "unknown", "未保留原请求，不能据此确认未执行。");
        if (!Allowed(operation)) return ChildReply.Create(request, "not-executed", "原请求所需能力族未启用，拒绝读取结果。");
        var until = timeout == 0 ? DateTimeOffset.UtcNow : request.Deadline;
        while (true)
        {
            var outer = await send(request with { RequestId = Guid.NewGuid().ToString("N") }, ct);
            if (outer.Status != "completed") return outer;
            var stored = outer.Data.Deserialize<StoredResult>(Json);
            if (stored is null || stored.Operation != operation) return ChildReply.Create(request, "unknown", "原请求记录已变化或失效。");
            var reply = stored.Reply;
            if (reply.Status is not ("queued" or "running") || DateTimeOffset.UtcNow >= until)
            {
                var data = reply.Data.Deserialize<Dictionary<string, JsonElement>>(Json) ?? [];
                data["operation"] = JsonSerializer.SerializeToElement(operation);
                return reply with { Instance = request.Instance, Data = JsonSerializer.SerializeToElement(data) };
            }
            await Task.Delay(50, ct);
        }
    }

    private async Task<string> FindOperationAsync(ChildRequest request, string wanted, CancellationToken ct)
    {
        long cursor = 0;
        for (var page = 0; page < 3; page++)
        {
            var query = request with
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Arguments = JsonSerializer.SerializeToElement(new { action = "requests", cursor, limit = 100 })
            };
            var listing = await send(query, ct);
            if (listing.Status != "completed") throw new InvalidOperationException(listing.Message);
            foreach (var item in listing.Data.GetProperty("items").EnumerateArray())
                if (item.GetProperty("requestId").GetString() == wanted) return item.GetProperty("operation").GetString() ?? "";
            if (!listing.Data.GetProperty("hasMore").GetBoolean()) break;
            cursor = listing.Data.GetProperty("nextCursor").GetInt64();
        }
        return "";
    }

    private ChildReply FilterRequests(ChildReply reply)
    {
        if (reply.Status != "completed" || !reply.Data.TryGetProperty("items", out var items)) return reply;
        var allowed = items.EnumerateArray().Where(item => Allowed(item.GetProperty("operation").GetString() ?? "")).ToArray();
        var data = reply.Data.Deserialize<Dictionary<string, JsonElement>>()!;
        data["items"] = JsonSerializer.SerializeToElement(allowed);
        return reply with { Data = JsonSerializer.SerializeToElement(data) };
    }

    private static JsonElement Normalize(string operation, JsonElement arguments)
    {
        if (operation == "child_process") return ChildProcessNormalizer.Normalize(arguments);
        if (operation != "screenshot") return arguments;
        var fields = arguments.Deserialize<Dictionary<string, JsonElement>>()!;
        fields.Remove("filePath");
        return JsonSerializer.SerializeToElement(fields);
    }

    private static int Timeout(string operation, string action, JsonElement args)
    {
        var fallback = operation == "child_session" || operation == "ui_wait" ? 30 : 5;
        var timeout = args.TryGetProperty("timeoutSeconds", out var value) ? value.GetInt32() : fallback;
        var minimum = operation == "screenshot" || operation == "child_session" && action == "result" ? 0 : 1;
        var maximum = operation is "child_pointer" or "child_keyboard" or "screenshot" ? 30 : 300;
        if (timeout < minimum || timeout > maximum) throw new ArgumentException($"timeoutSeconds范围{minimum}-{maximum}。");
        return timeout;
    }

    private static string Text(JsonElement args, string name, string fallback = "")
        => args.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;

    private sealed record StoredResult(string Operation, ChildReply Reply);
}
