using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace DotNetDebuggerMcp.Client;

[SupportedOSPlatform("windows")]
internal sealed partial class ChildSessionCases(string tool, string output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<JsonElement> _evidence = [];
    private ClientRunner _mcp = null!;
    private string _instance = "";
    private string _roundDirectory = "";

    internal static async Task<int> RunAsync(string[] args)
    {
        if (Environment.GetEnvironmentVariable("DOTNETDEBUGGERMCP_CHILD_INTEGRATION") != "1")
        {
            Console.Error.WriteLine("未验收：须显式设置DOTNETDEBUGGERMCP_CHILD_INTEGRATION=1，不自动启用系统。");
            return 2;
        }
        if (args.Length != 2) throw new ArgumentException("--child <安装shim.exe> <本轮输出目录>");
        var cases = new ChildSessionCases(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
        Directory.CreateDirectory(cases.outputDirectory);
        try { await cases.ExecuteAsync(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(cases.outputDirectory, "evidence.json"),
                JsonSerializer.Serialize(cases._evidence, new JsonSerializerOptions(Json) { WriteIndented = true }));
        }
    }

    private string outputDirectory => output;

    private async Task ExecuteAsync()
    {
        var probe = await CliAsync("child_session", new { action = "probe" });
        Require(P(probe.Data, "ChildSessionId").ValueKind == JsonValueKind.Null, "发现活动child，拒绝接管。");
        Require(P(probe.Data, "Enabled").GetBoolean(), "未验收：系统开关关闭，请获授权后显式启用。");
        Require(P(probe.Data, "ActiveXAvailable").GetBoolean(), "未验收：RDP ActiveX不可用。");
        var existing = await CliAsync("child_session", new { action = "status" });
        Require(existing.Instance.Length == 0, "存在其他本产品实例，拒绝复用或停止。");
        for (var round = 1; round <= 3; round++) await RoundAsync(round);
        var after = await CliAsync("child_session", new { action = "probe" });
        Require(P(after.Data, "ChildSessionId").ValueKind == JsonValueKind.Null, "自有child未清理。");
        Console.WriteLine("[PASS] 三轮安装路径CLI/MCP交叉闭环；开关恢复由已授权操作者单独核验。");
    }

    private async Task RoundAsync(int round)
    {
        _roundDirectory = Path.Combine(output, $"round-{round}");
        Directory.CreateDirectory(_roundDirectory);
        _instance = "";
        await using var mcp = await ClientRunner.ConnectInstalledAsync(tool, CancellationToken.None);
        _mcp = mcp;
        JsonElement identity = default;
        try
        {
            var start = await CliAsync("child_session", new { action = "start", timeoutSeconds = 30 });
            _instance = start.Instance;
            Require(_instance.Length != 0, "创建未返回实例。");
            identity = await WaitReadyAsync();
            await CrossProcessAsync(identity, round);
        }
        finally
        {
            if (_instance.Length != 0)
            {
                var stop = await CliAsync("child_session", new { action = "stop", instance = _instance, timeoutSeconds = 30 });
                Require(P(stop.Data, "state").GetString() == "Stopped", "stop尚未完成，必须人工核对。");
                if (identity.ValueKind == JsonValueKind.Object) await RequireRolesExitedAsync(identity);
            }
        }
        Console.WriteLine($"[PASS] round={round} instance={_instance}");
    }

    private async Task<JsonElement> WaitReadyAsync()
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(3);
        while (DateTimeOffset.UtcNow < until)
        {
            var state = await CliAsync("child_session", new { action = "status", instance = _instance });
            var name = P(state.Data, "state").GetString();
            if (name == "Ready") return P(state.Data, "identity");
            Require(name is not ("Faulted" or "Stopped"), state.Message);
            Console.WriteLine($"等待本轮child：{name}；若显示系统认证窗口，请用户手动完成。");
            await Task.Delay(1000);
        }
        throw new TimeoutException("认证/建立超时，未验收。");
    }

    private async Task<Reply> McpAsync(string operation, object args)
    {
        var result = await _mcp.CallRawAsync(operation, Arguments(args), CancellationToken.None);
        Require(result.IsError != true, $"{operation}返回协议错误。");
        return Parse(operation, Text(result));
    }

    private async Task<string> DebugAsync(string operation, object args, string contains)
    {
        var result = await _mcp.CallRawAsync(operation, Arguments(args), CancellationToken.None);
        var text = Text(result);
        Record(operation, new { text });
        Require(result.IsError != true && text.Contains(contains, StringComparison.Ordinal), $"{operation}: {text}");
        return text;
    }

    private async Task<Reply> CliAsync(string operation, object args)
    {
        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "child", "call", operation, "--json", JsonSerializer.Serialize(args, Json) })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("CLI启动失败。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(budget.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        var text = await stdout;
        var error = await stderr;
        Record("cli-stderr", new { operation, text = error, exitCode = process.ExitCode });
        return Parse(operation, text);
    }

    private Reply Parse(string operation, string text)
    {
        var reply = JsonSerializer.Deserialize<Reply>(text, Json) ?? throw new InvalidOperationException(text);
        Record(operation, reply);
        Require(reply.Status == "completed", $"{operation}: {text}");
        return reply;
    }

    private void Record(string operation, object value)
    {
        _evidence.Add(JsonSerializer.SerializeToElement(new { at = DateTimeOffset.UtcNow, operation, value }, Json));
        Console.WriteLine($"{operation}: {JsonSerializer.Serialize(value, Json)}");
    }

    private static Dictionary<string, object?> Arguments(object value)
        => JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(value, Json), Json)!;

    private static string Text(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static JsonElement P(JsonElement element, string name)
        => element.EnumerateObject().First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Reply(string Instance, string RequestId, string Status, string Message, JsonElement Data);
}
