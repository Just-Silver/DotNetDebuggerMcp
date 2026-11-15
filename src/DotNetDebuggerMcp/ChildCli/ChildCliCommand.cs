using System.Text.Json;
using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Services.Child;
using SharpSight.ChildSession.Contracts;

namespace DotNetDebuggerMcp.ChildCli;

/// <summary>
/// <c>child</c> 子命令（enable/disable/call）。只做参数解析、白名单校验与退出码映射；
/// 业务一律经共享调用入口，便于与 MCP 复用同一套实例/门控语义。
/// </summary>
internal sealed class ChildCliCommand(ChildCallPolicy caller, TextWriter output, TextWriter diagnostics)
{
    private const string OperationList =
        "child_session/child_process/child_pointer/child_keyboard/ui_find/ui_action/ui_input/ui_get/ui_wait/screenshot";

    private sealed record CallInput(string Operation, JsonElement Arguments, string FilePath);

    internal static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var caller = new ChildCallPolicy(FeatureFamily.All, ChildServices.CreateClient().SendAsync);
            return await new ChildCliCommand(caller, Console.Out, Console.Error).ExecuteAsync(args, stop.Token);
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    internal async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
            return Fail("缺少子命令。用法：child enable | child disable | child login | child logout | child call <operation> [--json <对象>|--json-file <路径>]。");
        try
        {
            return args[0] switch
            {
                "enable" => await ChangeAsync(true, args, ct),
                "disable" => await ChangeAsync(false, args, ct),
                "login" => await CredentialAsync("login", args, ct),
                "logout" => await CredentialAsync("logout", args, ct),
                "call" => await CallAsync(args, ct),
                _ => Fail($"未知子命令：{args[0]}。合法取值为 enable/disable/login/logout/call。"),
            };
        }
        catch (OperationCanceledException) { return Fail("已取消等待，结果未确认。", "unknown"); }
        catch (Exception ex) { return Fail($"子命令执行失败：{ex.Message}"); }
    }

    private async Task<int> CredentialAsync(string action, string[] args, CancellationToken ct)
    {
        if (args.Length != 1) return Fail($"{action} 不接受额外参数。");
        // CLI 退出会结束同一进程里的凭据窗口；给足够时间让用户完成一次输入。
        var reply = await caller.CallAsync("child_session",
            JsonSerializer.SerializeToElement(new { action, timeoutSeconds = 300 }), ct);
        return WriteReply(reply);
    }

    private async Task<int> ChangeAsync(bool enabled, string[] args, CancellationToken ct)
    {
        if (args.Length != 1) return Fail($"{args[0]} 不接受额外参数。");
        return WriteReply(await ChildServices.ChangeSettingAsync(enabled, ct));
    }

    private async Task<int> CallAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 2) return Fail($"缺少操作名。合法操作：{OperationList}。");
        if (!IsKnownOperation(args[1])) return Fail($"未知操作：{args[1]}。合法操作：{OperationList}。");
        var (input, error) = await BuildInputAsync(args, ct);
        if (input is null) return Fail(error!);
        var reply = await caller.CallAsync(input.Operation, input.Arguments, ct);
        if (input.Operation == "screenshot" && HasArtifact(reply))
            reply = await ChildServices.MaterializeScreenshotAsync(reply, input.FilePath, ct);
        return WriteReply(reply);
    }

    private async Task<(CallInput? Input, string? Error)> BuildInputAsync(string[] args, CancellationToken ct)
    {
        string? json = null;
        string? file = null;
        for (var i = 2; i < args.Length; i++)
        {
            var option = args[i];
            if (option is not ("--json" or "--json-file"))
                return (null, $"多余参数：{option}。call 只接受 --json 或 --json-file。");
            if (json is not null || file is not null)
                return (null, $"{option} 只能出现一次，且不能与另一种输入同时使用。");
            if (i + 1 >= args.Length) return (null, $"{option} 缺少取值。");
            if (option == "--json") json = args[++i];
            else file = args[++i];
        }
        var text = "{}";
        if (json is not null) text = json;
        else if (file is not null)
        {
            try { text = await File.ReadAllTextAsync(Path.GetFullPath(file), ct); }
            catch (OperationCanceledException) { return (null, "--json-file读取已取消，尚未发送请求。"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { return (null, $"--json-file 读取失败：{ex.Message}"); }
        }
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return (null, "参数必须是 JSON 对象。");
            root = document.RootElement.Clone();
        }
        catch (JsonException) { return (null, "参数不是合法 JSON 对象。"); }
        if (!TryPrepare(args[1], root, out var arguments, out var filePath, out var error))
            return (null, error);
        return (new CallInput(args[1], arguments, filePath), null);
    }

    // 外部路径仅在调用端物化，不能让worker按调用方提供的任意位置写盘。
    private static bool TryPrepare(string operation, JsonElement source, out JsonElement arguments,
        out string filePath, out string? error)
    {
        filePath = "";
        error = null;
        arguments = source;
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject()) fields[property.Name] = property.Value;
        var instance = "";
        if (fields.TryGetValue("instance", out var value))
        {
            if (value.ValueKind == JsonValueKind.Null) fields["instance"] = JsonSerializer.SerializeToElement("");
            else if (value.ValueKind != JsonValueKind.String) { error = "instance 必须是字符串。"; return false; }
            else instance = value.GetString() ?? "";
        }
        if (operation == "screenshot" && fields.TryGetValue("filePath", out var path))
        {
            if (path.ValueKind != JsonValueKind.String) { error = "filePath 必须是字符串。"; return false; }
            filePath = path.GetString() ?? "";
            fields.Remove("filePath");
        }
        if (instance.Trim().Length == 0)
        {
            instance = "";
            if (fields.ContainsKey("instance")) fields["instance"] = JsonSerializer.SerializeToElement("");
        }
        arguments = JsonSerializer.SerializeToElement(fields);
        if (instance.Length == 0 && !AllowsEmptyInstance(operation, arguments))
        {
            error = $"操作 {operation} 必须显式指定 instance；CLI 不会回退到父会话。";
            return false;
        }
        return true;
    }

    private static bool AllowsEmptyInstance(string operation, JsonElement arguments)
    {
        if (operation != "child_session") return false;
        var action = arguments.TryGetProperty("action", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : "status";
        return action is "probe" or "start" or "status" or "login" or "logout";
    }

    private static bool IsKnownOperation(string operation) => operation switch
    {
        "child_session" or "child_process" or "child_pointer" or "child_keyboard"
            or "ui_find" or "ui_action" or "ui_input" or "ui_get" or "ui_wait" or "screenshot" => true,
        _ => false,
    };

    private static bool HasArtifact(ChildReply reply)
        => reply.Status == "completed" && reply.Data.ValueKind == JsonValueKind.Object
            && reply.Data.TryGetProperty("artifact", out var artifact)
            && artifact.ValueKind == JsonValueKind.Object;

    private int WriteReply(ChildReply reply)
    {
        output.WriteLine(ChildServices.Serialize(reply));
        return ExitCode(reply.Status);
    }

    private static int ExitCode(string status) => status switch
    {
        "completed" or "queued" or "running" => 0,
        _ => 1,
    };

    private int Fail(string message, string status = "not-executed")
    {
        diagnostics.WriteLine(message);
        return WriteReply(new ChildReply("", "", status, message, JsonSerializer.SerializeToElement(new { })));
    }
}
